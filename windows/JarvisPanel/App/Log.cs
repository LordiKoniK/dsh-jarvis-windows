using System.Text;
using JarvisPanel.Core;

namespace JarvisPanel.App;

/// <summary>
/// Append-only diagnostic log at <c>%USERPROFILE%\.dsh\jarvis\panel.log</c>.
/// </summary>
/// <remarks>
/// The panel is a windowless process spawned by the host, so a file is the only
/// place a startup failure can be seen at all. Logging therefore never throws and
/// never blocks: whatever goes wrong here is swallowed, and the panel keeps going.
/// </remarks>
internal static class Log
{
    private const long LimitBytes = 1_048_576;
    private static readonly LogRotation Rotation = new();
    private static readonly object Sync = new();

    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "jarvis", "panel.log");

    public static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                var dir = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                if (Rotation.RecordWrite())
                {
                    var info = new FileInfo(Path);
                    if (info.Exists && LogRotation.ShouldRotate(info.Length, LimitBytes))
                        File.Move(Path, Path + ".1", overwrite: true);
                }
                var line = new StringBuilder()
                    .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                    .Append("  ")
                    .Append(message)
                    .Append(Environment.NewLine)
                    .ToString();
                File.AppendAllText(Path, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Diagnostics are best-effort; a read-only home must not break the panel.
        }
    }

    /// <summary>One-line rendering of an exception, including inner messages.</summary>
    public static string Describe(Exception error)
    {
        var text = new StringBuilder();
        for (var e = error; e is not null; e = e.InnerException)
        {
            if (text.Length > 0) text.Append(" <- ");
            text.Append(e.GetType().Name).Append(": ").Append(e.Message);
        }
        return text.ToString();
    }
}
