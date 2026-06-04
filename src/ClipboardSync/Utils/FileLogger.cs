namespace ClipboardSync.Utils;

public sealed class FileLogger(string logPath, int maxLogDays = 7)
{
    private readonly string _logPath = logPath;
    private readonly object _lock = new();
    private readonly int _maxLogDays = maxLogDays;
    private string? _lastDate;

    public void Log(string level, string message, Exception? ex = null)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var logEntry = ex != null
            ? $"[{timestamp}] [{level}] {message}{Environment.NewLine}  Exception: {ex}"
            : $"[{timestamp}] [{level}] {message}";

        lock (_lock)
        {
            try
            {
                var dir = Path.GetDirectoryName(_logPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                // Rotate if date changed
                PurgeOldLogs(Path.GetDirectoryName(_logPath)!);

                // Write to today's log
                File.AppendAllText(_logPath, logEntry + Environment.NewLine);
            }
            catch (Exception logEx)
            {
                Console.Error.WriteLine($"[FileLogger] Failed to write log to {_logPath}: {logEx.Message}");
                Console.Error.WriteLine(logEntry);
            }
        }
    }

    private void PurgeOldLogs(string logDir)
    {
        var today = DateTime.Now.ToString("yyyyMMdd");
        if (_lastDate == today) return;
        _lastDate = today;

        try
        {
            var prefix = Path.GetFileNameWithoutExtension(_logPath);
            var ext = Path.GetExtension(_logPath);
            var files = Directory.GetFiles(logDir, $"{prefix}_*{ext}");
            var cutoff = DateTime.Now.AddDays(-_maxLogDays).Date;

            foreach (var f in files)
            {
                var name = Path.GetFileName(f);
                var dateStr = name.Replace($"{prefix}_", "").Replace(ext, "");
                if (DateTime.TryParseExact(dateStr, "yyyyMMdd", null,
                    System.Globalization.DateTimeStyles.None, out var fileDate))
                {
                    if (fileDate < cutoff)
                        File.Delete(f);
                }
            }
        }
        catch { }
    }

    public void Info(string message) => Log("INFO", message);
    public void Warn(string message) => Log("WARN", message);
    public void Error(string message, Exception? ex = null) => Log("ERROR", message, ex);
    public void Debug(string message) => Log("DEBUG", message);
}
