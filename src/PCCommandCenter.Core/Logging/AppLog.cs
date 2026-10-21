using System.Collections.Concurrent;
using System.Text;

namespace PCCommandCenter.Core;

public enum LogLevel { Debug, Info, Warning, Error }

/// <summary>
/// Lightweight asynchronous file logger writing to %LOCALAPPDATA%\PCCommandCenter\logs\app.log.
/// Never throws: logging failures are swallowed so they cannot take the application down.
/// </summary>
public static class AppLog
{
    private const long MaxLogBytes = 5 * 1024 * 1024;
    private static readonly BlockingCollection<string> Queue = new(new ConcurrentQueue<string>(), 10_000);
    private static readonly Thread Writer;
    private static volatile bool _stopped;

    public static LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    static AppLog()
    {
        RotateIfNeeded();
        Writer = new Thread(WriteLoop) { IsBackground = true, Name = "AppLog writer", Priority = ThreadPriority.BelowNormal };
        Writer.Start();
    }

    public static void Debug(string message) => Write(LogLevel.Debug, message, null);
    public static void Info(string message) => Write(LogLevel.Info, message, null);
    public static void Warn(string message, Exception? ex = null) => Write(LogLevel.Warning, message, ex);
    public static void Error(string message, Exception? ex = null) => Write(LogLevel.Error, message, ex);

    public static void Write(LogLevel level, string message, Exception? ex)
    {
        if (level < MinimumLevel || _stopped) return;
        var sb = new StringBuilder(256);
        sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
          .Append(" [").Append(level switch
          {
              LogLevel.Debug => "DEBUG",
              LogLevel.Info => "INFO ",
              LogLevel.Warning => "WARN ",
              _ => "ERROR"
          }).Append("] ")
          .Append('[').Append(Environment.CurrentManagedThreadId).Append("] ")
          .Append(message);
        if (ex != null) sb.AppendLine().Append("    ").Append(ex.GetType().FullName).Append(": ").Append(ex.Message)
                          .AppendLine().Append(ex.StackTrace);
        Queue.TryAdd(sb.ToString());
    }

    /// <summary>Flushes pending entries; call on shutdown.</summary>
    public static void Shutdown()
    {
        _stopped = true;
        Queue.CompleteAdding();
        Writer.Join(TimeSpan.FromSeconds(2));
    }

    private static void WriteLoop()
    {
        try
        {
            foreach (var first in Queue.GetConsumingEnumerable())
            {
                var batch = new StringBuilder(first).AppendLine();
                while (Queue.TryTake(out var next)) batch.AppendLine(next);
                try { File.AppendAllText(AppInfo.LogFile, batch.ToString(), Encoding.UTF8); }
                catch { /* disk full or locked: drop the batch */ }
            }
        }
        catch { /* never crash the process because of logging */ }
    }

    private static void RotateIfNeeded()
    {
        try
        {
            var fi = new FileInfo(AppInfo.LogFile);
            if (fi.Exists && fi.Length > MaxLogBytes)
            {
                var old = AppInfo.LogFile + ".1";
                if (File.Exists(old)) File.Delete(old);
                File.Move(AppInfo.LogFile, old);
            }
        }
        catch { }
    }
}
