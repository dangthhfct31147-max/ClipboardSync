namespace ClipboardSync.Utils;

public sealed class FileLogger(string logPath)
{
    private readonly string _logPath = logPath;
    private readonly object _lock = new();

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
                File.AppendAllText(_logPath, logEntry + Environment.NewLine);
            }
            catch (Exception logEx)
            {
                Console.Error.WriteLine($"[FileLogger] Failed to write log to {_logPath}: {logEx.Message}");
                Console.Error.WriteLine(logEntry);
            }
        }
    }

    public void Info(string message) => Log("INFO", message);
    public void Warn(string message) => Log("WARN", message);
    public void Error(string message, Exception? ex = null) => Log("ERROR", message, ex);
    public void Debug(string message) => Log("DEBUG", message);
}
