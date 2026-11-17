namespace JarvisPanel.Core;

public readonly record struct Caption(string Text, SpeechSource Source, string? SessionId);

/// <summary>
/// Keeps the last line only across a speaking → silent transition. Polls while silent
/// must not extend its lifetime; textless speech must not reuse old words.
/// </summary>
public sealed class CaptionState
{
    public const double HoldDuration = 1.5;

    private Caption? _last;
    private bool _speaking;

    public double? LastEnd { get; private set; }

    public void Update(VoiceState voice, double now)
    {
        if (voice.Speaking)
        {
            var text = (voice.Text ?? string.Empty).Trim();
            _last = text.Length == 0 ? null : new Caption(text, voice.Source ?? SpeechSource.Jarvis, voice.SessionId);
            LastEnd = null;
        }
        else if (_speaking)
        {
            LastEnd = now;
        }
        _speaking = voice.Speaking;
    }

    public Caption? Caption(double now, bool enabled = true)
    {
        if (!enabled) return null;
        if (_speaking) return _last;
        if (LastEnd is not { } lastEnd || now < lastEnd || now - lastEnd >= HoldDuration) return null;
        return _last;
    }
}
