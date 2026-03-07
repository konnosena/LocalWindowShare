using System.Text;

internal sealed class PortalFileLogWriter : IDisposable
{
    private readonly object _sync = new();
    private readonly StreamWriter _writer;

    public PortalFileLogWriter(string contentRoot)
    {
        var logsDirectory = Path.Combine(contentRoot, "logs");
        Directory.CreateDirectory(logsDirectory);

        LogFilePath = Path.Combine(logsDirectory, $"portal-{DateTime.Now:yyyyMMdd}.log");
        var stream = new FileStream(LogFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };

        WriteSessionBanner("session-start");
    }

    public string LogFilePath { get; }

    public void Write(DateTimeOffset timestampUtc, string level, string source, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var normalizedMessage = message.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

        lock (_sync)
        {
            _writer.WriteLine($"[{timestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff}] [{level}] [{source}] {normalizedMessage}");
            _writer.Flush();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            WriteSessionBanner("session-end");
            _writer.Dispose();
        }
    }

    private void WriteSessionBanner(string label)
    {
        _writer.WriteLine($"===== {label} {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} =====");
        _writer.Flush();
    }
}
