namespace JarvisPanel.Core;

public sealed class LogRotation
{
    private int _writes;

    public bool RecordWrite()
    {
        _writes = (_writes + 1) % 100;
        return _writes == 0;
    }

    public static bool ShouldRotate(long size, long limit = 1_048_576) => limit >= 0 && size > limit;
}
