using System.Collections.Concurrent;
using System.Text;

namespace Osmium.Core.Services;

public sealed class LoggingService : IDisposable
{
    private readonly string _logDir;
    private readonly string _logFilePath;
    private readonly StreamWriter? _writer;
    private readonly object _lock = new();
    private readonly ConcurrentQueue<string> _recentLines = new();

    public event Action<string>? LogLineAdded;

    public string LogDirectory => _logDir;
    public string CurrentLogFile => _logFilePath;

    public LoggingService(string appDataDir)
    {
        _logDir = Path.Combine(appDataDir, "logs");
        Directory.CreateDirectory(_logDir);

        PruneOldLogs();

        _logFilePath = Path.Combine(_logDir, $"Osmium-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.log");
        try
        {
            var fs = new FileStream(_logFilePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            _writer = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch
        {
            _writer = null;
        }
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);
    public void RawNative(string message) => Write("NATIVE", message);

    public IReadOnlyList<string> GetRecentLines() => _recentLines.ToArray();

    private void Write(string level, string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}";
        _recentLines.Enqueue(line);
        while (_recentLines.Count > 300)
        {
            _recentLines.TryDequeue(out _);
        }

        lock (_lock)
        {
            _writer?.WriteLine(line);
        }

        LogLineAdded?.Invoke(line);
    }

    private void PruneOldLogs()
    {
        try
        {
            var files = new DirectoryInfo(_logDir)
                .GetFiles("Osmium-*.log")
                .OrderByDescending(f => f.CreationTimeUtc)
                .Skip(9);
            foreach (var f in files)
            {
                try
                {
                    f.Delete();
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
        }
    }
}