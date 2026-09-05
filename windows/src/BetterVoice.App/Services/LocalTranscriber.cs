using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BetterVoice.Core;
using Whisper.net;

namespace BetterVoice.App.Services;

public sealed class LocalTranscriber : IDisposable
{
    private static readonly SemaphoreSlim GlobalInferenceLock = new(1, 1);
    private static readonly Regex NoSpeechMarker = new(
        @"\[(?:BLANK_AUDIO|SILENCE|NOISE|MUSIC|APPLAUSE|LAUGHTER)\]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly SettingsManager _settingsManager;
    private readonly GrammarCorrector _grammarCorrector = new();
    private readonly SemaphoreSlim _modelDownloadLock = new(1, 1);
    private readonly object _factoryLock = new();
    private readonly int _threadCount;
    private readonly bool _useVocabularyPrompt;
    private WhisperFactory? _factory;
    private string? _loadedModelPath;
    private string? _warmedModelPath;

    public LocalTranscriber(
        SettingsManager settingsManager,
        int? threadCount = null,
        bool useVocabularyPrompt = true)
    {
        _settingsManager = settingsManager;
        _threadCount = threadCount ?? RecommendedThreadCount();
        _useVocabularyPrompt = useVocabularyPrompt;
    }

    private WhisperFactory GetOrCreateFactory(string modelPath)
    {
        lock (_factoryLock)
        {
            if (_factory != null && _loadedModelPath == modelPath)
            {
                return _factory;
            }

            _factory?.Dispose();
            _factory = WhisperFactory.FromPath(modelPath);
            _loadedModelPath = modelPath;
            _warmedModelPath = null;
            return _factory;
        }
    }

    public static int RecommendedThreadCount(int? logicalProcessorCount = null)
    {
        int processors = Math.Max(1, logicalProcessorCount ?? Environment.ProcessorCount);
        return Math.Clamp((processors + 1) / 2, 1, 8);
    }

    public async Task<bool> PreloadAsync()
    {
        var language = TranscriptionLanguage.FromStoredCode(_settingsManager.Current.TranscriptionLanguageCode);
        var modelSize = _settingsManager.Current.TranscriptionModelSize;
        string modelPath = GetModelPath(language, modelSize);
        if (!await EnsureModelDownloadedAsync(modelPath, language.UsesEnglishOnlyModel, modelSize)) return false;

        Task grammarPreload = _settingsManager.Current.GrammarCorrectionEnabled && language.AllowsGrammarCorrection
            ? _grammarCorrector.PreloadAsync()
            : Task.CompletedTask;

        bool whisperReady = await WarmWhisperAsync(modelPath, language);
        await grammarPreload;
        return whisperReady;
    }

    public async Task<string> TranscribeAsync(string audioWavPath, DeveloperAppProfile profile)
    {
        if (!File.Exists(audioWavPath)) return string.Empty;
        var info = new FileInfo(audioWavPath);
        if (info.Length < 2048) return string.Empty;

        var language = TranscriptionLanguage.FromStoredCode(_settingsManager.Current.TranscriptionLanguageCode);
        var modelSize = _settingsManager.Current.TranscriptionModelSize;
        string modelPath = GetModelPath(language, modelSize);
        if (!await EnsureModelDownloadedAsync(modelPath, language.UsesEnglishOnlyModel, modelSize)) return string.Empty;

        var overrides = _settingsManager.Current.DeveloperCleanupEnabled
            ? VocabularyFile.Terms(VocabularyFile.DefaultPath())
            : [];
        string? prompt = _useVocabularyPrompt ? BuildVocabularyPrompt(overrides) : null;

        string raw = await RunWhisperAsync(audioWavPath, modelPath, prompt);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        string result = CleanWhisperOutput(raw);
        if (string.IsNullOrWhiteSpace(result))
        {
            return string.Empty;
        }

        if (_settingsManager.Current.DeveloperCleanupEnabled)
        {
            result = DeveloperTextCleanup.Apply(result, profile, overrides);
        }

        var lang = TranscriptionLanguage.FromStoredCode(_settingsManager.Current.TranscriptionLanguageCode);
        if (_settingsManager.Current.GrammarCorrectionEnabled && lang.AllowsGrammarCorrection)
        {
            result = await _grammarCorrector.CorrectAsync(result);
        }

        return result;
    }

    public static string CleanWhisperOutput(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        string cleaned = NoSpeechMarker.Replace(text, " ");
        return string.Join(' ', cleaned.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    public static string BuildVocabularyPrompt(IReadOnlyList<(string Key, string Value)>? overrides)
    {
        string[] builtInTerms =
        [
            "BetterVoice", "JavaScript", "TypeScript", "JSON", "API", "GitHub",
            "PostgreSQL", "Kubernetes", "kubectl", "Docker", "OpenAI", "ChatGPT",
            ".NET", "WPF", "ONNX", "Whisper"
        ];

        IEnumerable<string> customTerms = overrides?.Select(item => item.Value) ?? [];
        string terms = string.Join(", ", builtInTerms.Concat(customTerms)
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Distinct(StringComparer.OrdinalIgnoreCase));
        const int maximumPromptCharacters = 240;
        if (terms.Length > maximumPromptCharacters)
        {
            terms = terms[..maximumPromptCharacters];
            int lastSeparator = terms.LastIndexOf(", ", StringComparison.Ordinal);
            if (lastSeparator > 0) terms = terms[..lastSeparator];
        }
        return terms;
    }

    private async Task<string> RunWhisperAsync(string wavPath, string modelPath, string? prompt)
    {
        await GlobalInferenceLock.WaitAsync();
        try
        {
            var factory = GetOrCreateFactory(modelPath);
            var lang = TranscriptionLanguage.FromStoredCode(_settingsManager.Current.TranscriptionLanguageCode);
            string whisperLang = lang.UsesEnglishOnlyModel ? "en" : (lang.Code == TranscriptionLanguage.AutomaticCode ? "auto" : lang.Code);

            var builder = factory.CreateBuilder()
                .WithLanguage(whisperLang)
                .WithThreads(_threadCount);
            if (!string.IsNullOrWhiteSpace(prompt)) builder.WithPrompt(prompt);
            using var processor = builder.Build();

            await using var fileStream = File.OpenRead(wavPath);
            var sb = new StringBuilder();

            await foreach (var segment in processor.ProcessAsync(fileStream))
            {
                if (!string.IsNullOrWhiteSpace(segment.Text))
                {
                    sb.Append(segment.Text).Append(' ');
                }
            }

            _warmedModelPath = modelPath;
            return sb.ToString().Trim();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Whisper transcription error: {ex}");
            return string.Empty;
        }
        finally
        {
            GlobalInferenceLock.Release();
        }
    }

    private async Task<bool> WarmWhisperAsync(string modelPath, TranscriptionLanguage language)
    {
        await GlobalInferenceLock.WaitAsync();
        try
        {
            if (string.Equals(_warmedModelPath, modelPath, StringComparison.Ordinal)) return true;

            var factory = GetOrCreateFactory(modelPath);
            string whisperLanguage = language.UsesEnglishOnlyModel
                ? "en"
                : language.Code == TranscriptionLanguage.AutomaticCode ? "auto" : language.Code;
            using var processor = factory.CreateBuilder()
                .WithLanguage(whisperLanguage)
                .WithThreads(_threadCount)
                .Build();
            await using Stream silence = CreateSilentWarmupWav();
            await foreach (var _ in processor.ProcessAsync(silence))
            {
                // Enumerating the stream forces native runtime and model initialization.
            }

            _warmedModelPath = modelPath;
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Whisper preload error: {ex}");
            return false;
        }
        finally
        {
            GlobalInferenceLock.Release();
        }
    }

    private static Stream CreateSilentWarmupWav()
    {
        const int sampleRate = 16_000;
        const short channelCount = 1;
        const short bitsPerSample = 16;
        const int sampleCount = sampleRate;
        int dataLength = sampleCount * channelCount * bitsPerSample / 8;

        var stream = new MemoryStream(44 + dataLength);
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataLength);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write(channelCount);
            writer.Write(sampleRate);
            writer.Write(sampleRate * channelCount * bitsPerSample / 8);
            writer.Write((short)(channelCount * bitsPerSample / 8));
            writer.Write(bitsPerSample);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(dataLength);
            writer.Write(new byte[dataLength]);
        }
        stream.Position = 0;
        return stream;
    }

    public static string GetModelPath() => GetModelPath(TranscriptionLanguage.English, TranscriptionModelSize.Balanced);

    public static string GetModelPath(
        TranscriptionLanguage language,
        TranscriptionModelSize modelSize = TranscriptionModelSize.Balanced)
    {
        string suffix = language.UsesEnglishOnlyModel ? ".en" : string.Empty;
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BetterVoice", "Models", $"ggml-{modelSize.ModelStem()}{suffix}.bin");
    }

    private async Task<bool> EnsureModelDownloadedAsync(
        string modelPath,
        bool englishOnly,
        TranscriptionModelSize modelSize)
    {
        if (File.Exists(modelPath)) return true;

        await _modelDownloadLock.WaitAsync();
        try
        {
            if (File.Exists(modelPath)) return true;

            string? modelDirectory = Path.GetDirectoryName(modelPath);
            if (!string.IsNullOrEmpty(modelDirectory)) Directory.CreateDirectory(modelDirectory);

            const string revision = "5359861c739e955e79d9a303bcbc70fb988958b1";
            string suffix = englishOnly ? ".en" : string.Empty;
            string modelName = $"ggml-{modelSize.ModelStem()}{suffix}.bin";
            string url = $"https://huggingface.co/ggerganov/whisper.cpp/resolve/{revision}/{modelName}";
            string temporary = modelPath + $".{Guid.NewGuid():N}.download";
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                await using Stream source = await client.GetStreamAsync(url);
                await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                {
                    await source.CopyToAsync(target);
                    await target.FlushAsync();
                }
                File.Move(temporary, modelPath, false);
                return true;
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            _modelDownloadLock.Release();
        }
    }

    public void Dispose()
    {
        lock (_factoryLock)
        {
            _factory?.Dispose();
            _factory = null;
        }
        _grammarCorrector.Dispose();
        _modelDownloadLock.Dispose();
    }
}
