using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BetterVoice.App.Native;
using BetterVoice.App.Services;
using BetterVoice.Core;
using Xunit;
using Xunit.Abstractions;

namespace BetterVoice.Integration.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PerformanceBenchmarkCollection
{
    public const string Name = "Performance benchmarks";
}

[Collection(PerformanceBenchmarkCollection.Name)]
public class PerformanceBenchmarks
{
    private readonly ITestOutputHelper _output;

    public PerformanceBenchmarks(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Benchmark_CircleGestureDetector_Throughput()
    {
        var detector = new CircleGestureDetector();
        const int circleCount = 2_000;
        const int samplesPerCircle = 48;
        int iterations = circleCount * (samplesPerCircle + 1);
        var center = new PointD(500, 500);

        var sw = Stopwatch.StartNew();
        int gesturesFound = 0;
        double time = 0;
        for (int circle = 0; circle < circleCount; circle++)
        {
            for (int sample = 0; sample < samplesPerCircle; sample++)
            {
                double angle = (double)sample / (samplesPerCircle - 1) * 2.0 * Math.PI;
                time += 1.0 / 60.0;
                if (detector.Add(new PointD(center.X + 50 * Math.Cos(angle), center.Y + 50 * Math.Sin(angle)), time) != null)
                {
                    gesturesFound++;
                }
            }

            time += 1.0;
            detector.Add(new PointD(center.X + 200, center.Y), time);
            time += 0.5;
        }
        sw.Stop();

        double totalMs = sw.Elapsed.TotalMilliseconds;
        double opsPerSec = iterations / sw.Elapsed.TotalSeconds;
        double usPerOp = (totalMs * 1000.0) / iterations;

        _output.WriteLine($"--- CIRCLE GESTURE DETECTOR PERFORMANCE ---");
        _output.WriteLine($"Total Samples: {iterations:N0}");
        _output.WriteLine($"Total Time: {totalMs:F2} ms");
        _output.WriteLine($"Latency per sample: {usPerOp:F3} µs (microseconds)");
        _output.WriteLine($"Throughput: {opsPerSec:N0} samples/second");
        _output.WriteLine($"Recognized Gestures: {gesturesFound:N0}");

        Assert.True(gesturesFound >= circleCount * 0.95, "Benchmark must exercise the full recognition path");
        Assert.True(opsPerSec > 20_000, "Should process realistic circle streams far above the 60 Hz input rate");
    }

    [Fact]
    public void Benchmark_DeveloperTextCleanup_Throughput()
    {
        string sampleText = "Please inspect the json and javascript code on github, then deploy with cube cuttle to the rest api and test with curl and npm install. ";
        var sb = new StringBuilder();
        for (int i = 0; i < 500; i++) sb.Append(sampleText);
        string bigText = sb.ToString(); // ~70,000 characters

        var overrides = new List<(string, string)> { ("cube cuttle", "kubectl") };

        // Warmup
        _ = DeveloperTextCleanup.Apply(sampleText, DeveloperAppProfile.Terminal, overrides);

        var sw = Stopwatch.StartNew();
        int runs = 50;
        for (int i = 0; i < runs; i++)
        {
            _ = DeveloperTextCleanup.Apply(bigText, DeveloperAppProfile.Terminal, overrides);
        }
        sw.Stop();

        long totalChars = (long)bigText.Length * runs;
        double totalSec = sw.Elapsed.TotalSeconds;
        double charsPerSec = totalChars / totalSec;
        double mbPerSec = (totalChars * sizeof(char)) / (1024.0 * 1024.0) / totalSec;

        _output.WriteLine($"--- DEVELOPER TEXT CLEANUP PERFORMANCE ---");
        _output.WriteLine($"Total Characters Processed: {totalChars:N0}");
        _output.WriteLine($"Total Time: {sw.Elapsed.TotalMilliseconds:F2} ms");
        _output.WriteLine($"Throughput: {charsPerSec:N0} characters/sec ({mbPerSec:F2} MB/sec)");

        Assert.True(charsPerSec > 1_000_000, "Should process at least 1M chars/sec");
    }

    [Fact]
    public void Benchmark_TrailSegments_Throughput()
    {
        int pointCount = 10_000;
        var points = new List<PointD>(pointCount);
        var times = new List<double>(pointCount);

        for (int i = 0; i < pointCount; i++)
        {
            points.Add(new PointD(i * 1.5, (i % 20) * 2.0));
            times.Add(i * 0.016);
        }

        // Warmup
        _ = TrailSegments.Calculate(points.GetRange(0, 100), times.GetRange(0, 100));

        var sw = Stopwatch.StartNew();
        int iterations = 100;
        int totalSegments = 0;
        for (int i = 0; i < iterations; i++)
        {
            var segments = TrailSegments.Calculate(points, times);
            totalSegments += segments.Count;
        }
        sw.Stop();

        long totalPointsProcessed = (long)pointCount * iterations;
        double pointsPerSec = totalPointsProcessed / sw.Elapsed.TotalSeconds;

        _output.WriteLine($"--- TRAIL SEGMENTS PERFORMANCE ---");
        _output.WriteLine($"Total Points Processed: {totalPointsProcessed:N0}");
        _output.WriteLine($"Total Time: {sw.Elapsed.TotalMilliseconds:F2} ms");
        _output.WriteLine($"Throughput: {pointsPerSec:N0} points/second");

        Assert.True(pointsPerSec > 2_000_000, "Should process at least 2M points/sec");
    }

    [Fact]
    public void Benchmark_SessionRetention_Scaling()
    {
        var now = DateTime.UtcNow;
        int sessionCount = 10_000;
        var sessions = new List<StoredSession>(sessionCount);
        var rand = new Random(42);

        for (int i = 0; i < sessionCount; i++)
        {
            sessions.Add(new StoredSession(
                $"2026-08-23T15-16-45Z-{Guid.NewGuid()}",
                now.AddDays(-rand.NextDouble() * 14.0),
                rand.Next(10_000, 500_000)));
        }

        var policy = new SessionRetentionPolicy(TimeSpan.FromDays(7), 50_000_000);

        var sw = Stopwatch.StartNew();
        int removedCount = 0;
        for (int i = 0; i < 20; i++)
        {
            var removed = policy.SessionsToRemove(sessions, now);
            removedCount = removed.Count;
        }
        sw.Stop();

        _output.WriteLine($"--- SESSION RETENTION QUOTA SCALING ---");
        _output.WriteLine($"Session count per run: {sessionCount:N0}");
        _output.WriteLine($"Total Runs: 20");
        _output.WriteLine($"Average Time per 10k items: {sw.Elapsed.TotalMilliseconds / 20.0:F2} ms");
        _output.WriteLine($"Sessions marked for removal: {removedCount:N0}");

        Assert.True(sw.Elapsed.TotalMilliseconds / 20.0 < 50.0, "Should evaluate 10k sessions in < 50ms");
    }

    [Fact]
    public async Task Benchmark_Whisper_Inference_Latency_And_RTF()
    {
        string wavPath = Path.Combine(Path.GetTempPath(), "bettervoice_speech_test.wav");
        Assert.True(File.Exists(wavPath), "Spoken test WAV file should exist");

        var fileInfo = new FileInfo(wavPath);
        // 16kHz 16-bit mono = 32,000 bytes/sec
        double audioDurationSeconds = (fileInfo.Length - 44) / 32000.0;

        var settingsManager = new SettingsManager(TestPaths.SettingsFile());
        settingsManager.Current.TranscriptionModelSize = TranscriptionModelSize.Balanced;
        using var transcriber = new LocalTranscriber(settingsManager);

        var preload = Stopwatch.StartNew();
        Assert.True(await transcriber.PreloadAsync());
        preload.Stop();

        var firstUse = Stopwatch.StartNew();
        string firstResult = await transcriber.TranscribeAsync(wavPath, DeveloperAppProfile.General);
        firstUse.Stop();

        var sw = Stopwatch.StartNew();
        int iterations = 3;
        string lastResult = string.Empty;
        for (int i = 0; i < iterations; i++)
        {
            lastResult = await transcriber.TranscribeAsync(wavPath, DeveloperAppProfile.General);
        }
        sw.Stop();

        double avgInferenceMs = sw.Elapsed.TotalMilliseconds / iterations;
        double avgInferenceSec = avgInferenceMs / 1000.0;
        double rtf = avgInferenceSec / audioDurationSeconds;

        _output.WriteLine($"--- WHISPER.NET SPEECH RECOGNITION PERFORMANCE ---");
        _output.WriteLine($"Model: {settingsManager.Current.TranscriptionModelSize.DisplayName()}");
        _output.WriteLine($"Native runtime: {Whisper.net.LibraryLoader.RuntimeOptions.LoadedLibrary}");
        _output.WriteLine($"Background preload: {preload.Elapsed.TotalMilliseconds:F1} ms");
        _output.WriteLine($"First user inference after preload: {firstUse.Elapsed.TotalMilliseconds:F1} ms");
        _output.WriteLine($"Audio Length: {audioDurationSeconds:F2} seconds");
        _output.WriteLine($"Average Inference Time: {avgInferenceMs:F1} ms");
        _output.WriteLine($"Real-Time Factor (RTF): {rtf:F3}x ({(1.0 / rtf):F1}x faster than real time!)");
        _output.WriteLine($"Decoded Text: \"{lastResult}\"");

        Assert.True(rtf < 0.5, "Whisper.net should be at least 2x faster than real-time (RTF < 0.5)");
        Assert.Contains("JavaScript", firstResult, StringComparison.Ordinal);
        Assert.True(firstUse.Elapsed < TimeSpan.FromSeconds(2), "Preloading should remove the native runtime cold-start from first use");
        Assert.Contains("JavaScript", lastResult, StringComparison.Ordinal);
        Assert.Contains("JSON", lastResult, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Benchmark_GrammarCorrection_WarmLatency()
    {
        using var corrector = new GrammarCorrector();
        Assert.True(await corrector.PreloadAsync());
        _ = await corrector.CorrectAsync("he go to school yesterday");

        const int iterations = 5;
        var samples = new double[iterations];
        for (int index = 0; index < iterations; index++)
        {
            var sw = Stopwatch.StartNew();
            string result = await corrector.CorrectAsync("she dont like apples");
            sw.Stop();
            Assert.Equal("She doesn't like apples.", result);
            samples[index] = sw.Elapsed.TotalMilliseconds;
        }

        Array.Sort(samples);
        double average = samples.Average();
        double p95 = samples[^1];
        _output.WriteLine("--- GRAMMAR CORRECTION PERFORMANCE ---");
        _output.WriteLine($"Average warm inference: {average:F1} ms");
        _output.WriteLine($"Observed p95: {p95:F1} ms");
        Assert.True(p95 < 500, "Warm grammar correction should complete within 500 ms");
    }

    [Fact]
    public void Benchmark_ScreenshotCapture_JpegLatency()
    {
        const int iterations = 5;
        var samples = new double[iterations];
        var gesture = new CircleGesture(new PointD(350, 250), 55);

        for (int index = 0; index < iterations; index++)
        {
            string path = Path.Combine(Path.GetTempPath(), $"bettervoice_capture_{Guid.NewGuid():N}.jpg");
            try
            {
                var sw = Stopwatch.StartNew();
                ScreenshotCapture.Capture(gesture, path, ScreenContextCaptureMode.FullDisplayWithHighlight);
                sw.Stop();
                samples[index] = sw.Elapsed.TotalMilliseconds;
                Assert.True(new FileInfo(path).Length > 5_000);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        Array.Sort(samples);
        double average = samples.Average();
        double p95 = samples[^1];
        _output.WriteLine("--- SCREENSHOT CAPTURE PERFORMANCE ---");
        _output.WriteLine($"Average full-monitor JPEG capture: {average:F1} ms");
        _output.WriteLine($"Observed p95: {p95:F1} ms");
        Assert.True(p95 < 250, "Screenshot capture should avoid a visible UI stall");
    }
}
