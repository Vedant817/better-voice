using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using BetterVoice.App.Audio;
using BetterVoice.App.Native;
using BetterVoice.App.Services;
using BetterVoice.Core;
using NAudio.Wave;
using Xunit;

namespace BetterVoice.Integration.Tests;

public class RealWorldVoiceAndSubsystemsTests
{
    [Fact]
    public async Task TestRealWhisperTranscriberWithDeveloperTextCleanup()
    {
        string wavPath = Path.Combine(Path.GetTempPath(), "bettervoice_speech_test.wav");
        Assert.True(File.Exists(wavPath), "Spoken test WAV file should exist");

        var settingsManager = new SettingsManager(TestPaths.SettingsFile());
        settingsManager.Current.DeveloperCleanupEnabled = true;
        settingsManager.Current.TranscriptionLanguageCode = "en";
        settingsManager.Current.TranscriptionModelSize = TranscriptionModelSize.Balanced;

        using var transcriber = new LocalTranscriber(settingsManager);
        string transcript = await transcriber.TranscribeAsync(wavPath, DeveloperAppProfile.General);

        Assert.False(string.IsNullOrWhiteSpace(transcript), "Transcript should not be empty");

        // Verify that Whisper decoded the audio and DeveloperTextCleanup applied proper developer casing!
        Assert.Contains("BetterVoice", transcript, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("JavaScript", transcript, StringComparison.Ordinal);
        Assert.Contains("JSON", transcript, StringComparison.Ordinal);
        Assert.Contains("API", transcript, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("he go to school yesterday", "He went to school yesterday.")]
    [InlineData("she dont like apples", "She doesn't like apples.")]
    public async Task TestRealGrammarCorrection(string input, string expected)
    {
        using var corrector = new GrammarCorrector();
        string corrected = await corrector.CorrectAsync(input);
        Assert.Equal(expected, corrected);
    }

    [Fact]
    public void TestRealScreenCaptureWithTargetHighlight()
    {
        string tempPng = Path.Combine(Path.GetTempPath(), $"circle_capture_test_{Guid.NewGuid()}.png");
        try
        {
            var screenBounds = System.Windows.Forms.Screen.PrimaryScreen?.Bounds
                ?? new Rectangle(0, 0, 1920, 1080);
            var gesture = new CircleGesture(
                new PointD(screenBounds.Left + screenBounds.Width / 2.0, screenBounds.Top + screenBounds.Height / 2.0),
                55);
            Rectangle expectedCrop = ScreenshotCapture.GetCropBounds(gesture, screenBounds);
            ScreenshotCapture.Capture(gesture, tempPng, ScreenContextCaptureMode.CroppedSelection);

            Assert.True(File.Exists(tempPng), "Screenshot file should be created");
            var fileInfo = new FileInfo(tempPng);
            Assert.True(fileInfo.Length > 256, "Screenshot should contain encoded image data");

            using var img = new Bitmap(tempPng);
            Assert.Equal(expectedCrop.Width, img.Width);
            Assert.Equal(expectedCrop.Height, img.Height);
            Assert.True(
                img.Width < screenBounds.Width || img.Height < screenBounds.Height,
                "A context capture should contain only the circled crop, not the whole display");

            int markerX = Math.Clamp((int)Math.Round(gesture.Center.X - expectedCrop.Left + gesture.Radius), 0, img.Width - 1);
            int markerY = Math.Clamp((int)Math.Round(gesture.Center.Y - expectedCrop.Top), 0, img.Height - 1);
            Color marker = img.GetPixel(markerX, markerY);
            Assert.True(
                marker.B > marker.G && marker.G > marker.R && marker.R >= 50 && marker.A > 200,
                "The cropped image must retain the muted steel-blue target marker");
        }
        finally
        {
            if (File.Exists(tempPng))
            {
                File.Delete(tempPng);
            }
        }
    }

    [Fact]
    public void TestRealFullDisplayCaptureKeepsMonitorAndHighlightsTarget()
    {
        string tempPng = Path.Combine(Path.GetTempPath(), $"full_display_capture_test_{Guid.NewGuid()}.png");
        try
        {
            Rectangle screenBounds = System.Windows.Forms.Screen.PrimaryScreen?.Bounds
                ?? new Rectangle(0, 0, 1920, 1080);
            var gesture = new CircleGesture(
                new PointD(screenBounds.Left + screenBounds.Width / 2.0, screenBounds.Top + screenBounds.Height / 2.0),
                55);

            ScreenshotCapture.Capture(
                gesture,
                tempPng,
                ScreenContextCaptureMode.FullDisplayWithHighlight);

            using var image = new Bitmap(tempPng);
            Assert.Equal(screenBounds.Width, image.Width);
            Assert.Equal(screenBounds.Height, image.Height);

            int markerX = Math.Clamp((int)Math.Round(gesture.Center.X - screenBounds.Left + gesture.Radius), 0, image.Width - 1);
            int markerY = Math.Clamp((int)Math.Round(gesture.Center.Y - screenBounds.Top), 0, image.Height - 1);
            Color marker = image.GetPixel(markerX, markerY);
            Assert.True(
                marker.B > marker.G && marker.G > marker.R && marker.R >= 50 && marker.A > 200,
                "The full-display image must retain the muted steel-blue target marker");
        }
        finally
        {
            if (File.Exists(tempPng)) File.Delete(tempPng);
        }
    }

    [Fact]
    public void TestAudioCaptureDeviceEnumeration()
    {
        var devices = AudioRecorder.GetInputDevices();
        Assert.NotNull(devices);
        // Should not crash even if no mics are connected or default mic is active
    }

    [Fact]
    public async Task TestAudioCaptureDurationMatchesWallClock()
    {
        if (AudioRecorder.GetInputDevices().Count == 0) return;

        string outputPath = Path.Combine(Path.GetTempPath(), $"bettervoice_capture_{Guid.NewGuid():N}.wav");
        try
        {
            using var recorder = new AudioRecorder();
            recorder.Start(outputPath);
            await Task.Delay(1_000);
            await recorder.StopAsync();

            using var reader = new WaveFileReader(outputPath);
            Assert.Equal(16_000, reader.WaveFormat.SampleRate);
            Assert.Equal(1, reader.WaveFormat.Channels);
            Assert.InRange(reader.TotalTime.TotalSeconds, 0.75, 1.35);
        }
        finally
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

    [Fact]
    public void TestCurrentAppContextDetection()
    {
        var context = TextInsertion.GetCurrentContext();
        Assert.False(string.IsNullOrEmpty(context.ProcessName));
    }

    [Fact]
    public void TestSettingsAndVocabularyRoundTrip()
    {
        string settingsPath = TestPaths.SettingsFile();
        var settings = new SettingsManager(settingsPath);
        int changeNotifications = 0;
        settings.SettingsChanged += _ => changeNotifications++;
        settings.Current.CircleMinimumAngleDegrees = 345;
        settings.Current.QuickTriggerMode = RecordingTriggerMode.DoubleTap;
        settings.Current.TranscriptionModelSize = TranscriptionModelSize.Accurate;
        settings.Current.ScreenContextCaptureMode = ScreenContextCaptureMode.CroppedSelection;
        settings.Save();

        Assert.Equal(1, changeNotifications);
        var reloaded = new SettingsManager(settingsPath);
        Assert.Equal(345, reloaded.Current.CircleMinimumAngleDegrees);
        Assert.Equal(RecordingTriggerMode.DoubleTap, reloaded.Current.QuickTriggerMode);
        Assert.Equal(TranscriptionModelSize.Accurate, reloaded.Current.TranscriptionModelSize);
        Assert.Equal(ScreenContextCaptureMode.CroppedSelection, reloaded.Current.ScreenContextCaptureMode);

        // Reset to default
        settings.Current.CircleMinimumAngleDegrees = 340;
        settings.Current.QuickTriggerMode = RecordingTriggerMode.Hold;
        settings.Current.TranscriptionModelSize = TranscriptionModelSize.Balanced;
        settings.Current.ScreenContextCaptureMode = ScreenContextCaptureMode.FullDisplayWithHighlight;
        settings.Save();
    }

    [Fact]
    public void TestTranscriberSelectsEnglishAndMultilingualModels()
    {
        string english = LocalTranscriber.GetModelPath(TranscriptionLanguage.English, TranscriptionModelSize.Balanced);
        string multilingual = LocalTranscriber.GetModelPath(TranscriptionLanguage.Automatic, TranscriptionModelSize.Balanced);
        string fast = LocalTranscriber.GetModelPath(TranscriptionLanguage.English, TranscriptionModelSize.Fast);
        string accurate = LocalTranscriber.GetModelPath(TranscriptionLanguage.English, TranscriptionModelSize.Accurate);

        Assert.EndsWith("ggml-base.en.bin", english, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("ggml-base.bin", multilingual, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("ggml-tiny.en.bin", fast, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("ggml-small.en.bin", accurate, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(english, multilingual);
    }

    [Theory]
    [InlineData(500, 400, 50, 442, 342, 116, 116)]
    [InlineData(5, 5, 50, 0, 0, 63, 63)]
    [InlineData(995, 795, 50, 937, 737, 63, 63)]
    public void TestScreenshotCropMatchesCircleAndClampsToDisplay(
        double centerX,
        double centerY,
        double radius,
        int expectedX,
        int expectedY,
        int expectedWidth,
        int expectedHeight)
    {
        var screenBounds = new Rectangle(0, 0, 1000, 800);
        Rectangle crop = ScreenshotCapture.GetCropBounds(
            new CircleGesture(new PointD(centerX, centerY), radius),
            screenBounds);

        Assert.Equal(new Rectangle(expectedX, expectedY, expectedWidth, expectedHeight), crop);
        Assert.True(screenBounds.Contains(crop));
    }

    [Fact]
    public void TestScreenshotCropSupportsNegativeMonitorCoordinates()
    {
        var screenBounds = new Rectangle(-1920, 0, 1920, 1080);
        var gesture = new CircleGesture(new PointD(-1800, 300), 80);

        Rectangle crop = ScreenshotCapture.GetCropBounds(gesture, screenBounds);

        Assert.Equal(new Rectangle(-1888, 212, 176, 176), crop);
        Assert.True(screenBounds.Contains(crop));
    }

    [Fact]
    public void TestScreenshotCropUsesDrawnLoopExtentsForWideSelections()
    {
        var screenBounds = new Rectangle(0, 0, 1000, 800);
        var gesture = new CircleGesture(
            new PointD(500, 400),
            Radius: 72,
            HalfWidth: 100,
            HalfHeight: 40);

        Rectangle crop = ScreenshotCapture.GetCropBounds(gesture, screenBounds);

        Assert.Equal(new Rectangle(390, 350, 220, 100), crop);
    }

    [Fact]
    public void TestCaptureModeSelectsExactlyOneFramingBoundary()
    {
        var screenBounds = new Rectangle(-1920, 0, 1920, 1080);
        var gesture = new CircleGesture(new PointD(-1200, 400), 75, 120, 60);

        Rectangle full = ScreenshotCapture.GetCaptureBounds(
            gesture,
            screenBounds,
            ScreenContextCaptureMode.FullDisplayWithHighlight);
        Rectangle cropped = ScreenshotCapture.GetCaptureBounds(
            gesture,
            screenBounds,
            ScreenContextCaptureMode.CroppedSelection);

        Assert.Equal(screenBounds, full);
        Assert.Equal(new Rectangle(-1332, 328, 264, 144), cropped);
        Assert.NotEqual(full, cropped);
    }

    [Fact]
    public void TestWhisperPromptIsBoundedAndIncludesCustomVocabulary()
    {
        string prompt = LocalTranscriber.BuildVocabularyPrompt(
            [("cube cuttle", "kubectl"), ("better voice", "BetterVoice")]);

        Assert.Contains("JavaScript", prompt, StringComparison.Ordinal);
        Assert.Contains("kubectl", prompt, StringComparison.Ordinal);
        Assert.True(prompt.Length <= 240);
        Assert.Equal(1, prompt.Split("BetterVoice").Length - 1);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(4, 2)]
    [InlineData(16, 8)]
    [InlineData(24, 8)]
    public void TestRecommendedWhisperThreadCount(int logicalProcessors, int expected)
    {
        Assert.Equal(expected, LocalTranscriber.RecommendedThreadCount(logicalProcessors));
    }

    [Theory]
    [InlineData("[BLANK_AUDIO]", "")]
    [InlineData(" [silence]  [NOISE] ", "")]
    [InlineData("Hello [MUSIC] world", "Hello world")]
    public void TestTranscriberRemovesWhisperNoSpeechMarkers(string raw, string expected)
    {
        Assert.Equal(expected, LocalTranscriber.CleanWhisperOutput(raw));
    }
}
