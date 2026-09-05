namespace BetterVoice.Core;

/// <summary>
/// Selects the local Whisper model used for transcription. The labels describe
/// the user-facing trade-off rather than exposing model implementation details.
/// </summary>
public enum TranscriptionModelSize
{
    Fast = 0,
    Balanced = 1,
    Accurate = 2
}

public static class TranscriptionModelSizeExtensions
{
    public static string ModelStem(this TranscriptionModelSize size) => size switch
    {
        TranscriptionModelSize.Fast => "tiny",
        TranscriptionModelSize.Balanced => "base",
        TranscriptionModelSize.Accurate => "small",
        _ => "base"
    };

    public static string DisplayName(this TranscriptionModelSize size) => size switch
    {
        TranscriptionModelSize.Fast => "Fast (tiny)",
        TranscriptionModelSize.Balanced => "Balanced (base)",
        TranscriptionModelSize.Accurate => "Accurate (small)",
        _ => "Balanced (base)"
    };
}
