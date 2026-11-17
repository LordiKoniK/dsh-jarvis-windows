namespace JarvisPanel.Core;

/// <summary>
/// Collapsing the quick bar takes two Esc presses within <see cref="Window"/>, so a
/// stray press while typing only shows a hint.
/// </summary>
public sealed class EscapeGate
{
    public const double Window = 0.6;

    public enum Action { Hint, Close }

    private double? _armedAt;

    /// <summary><paramref name="time"/> is monotonic seconds (e.g. <see cref="Environment.TickCount64"/> / 1000).</summary>
    public Action Press(double time)
    {
        if (_armedAt is { } armed && time >= armed && time - armed <= Window)
        {
            _armedAt = null;
            return Action.Close;
        }
        _armedAt = time;
        return Action.Hint;
    }

    public void Reset() => _armedAt = null;
}
