namespace Frametide.Core.Infrastructure;

public enum LogLevel { Info, Ok, Warn, Err }

/// <summary>
/// Technical log (English on purpose: easier to search and share). Line format:
/// "HH:mm:ss [LEVEL] message". Thread-safe; the UI listens to <see cref="LineAdded"/>.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly List<string> Lines = [];

    public static event Action<string>? LineAdded;

    public static IReadOnlyList<string> Snapshot()
    {
        lock (Gate) return Lines.ToArray();
    }

    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Ok(string message) => Write(LogLevel.Ok, message);
    public static void Warn(string message) => Write(LogLevel.Warn, message);
    public static void Error(string message) => Write(LogLevel.Err, message);

    public static void Write(LogLevel level, string message)
    {
        var tag = level switch { LogLevel.Ok => "OK", LogLevel.Warn => "WARN", LogLevel.Err => "ERR", _ => "INFO" };
        var line = $"{DateTime.Now:HH:mm:ss} [{tag}] {message}";
        lock (Gate)
        {
            Lines.Add(line);
            if (Lines.Count > 5000) Lines.RemoveRange(0, 1000);
            try { File.AppendAllText(AppPaths.LogFile, line + Environment.NewLine); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        LineAdded?.Invoke(line);
    }
}
