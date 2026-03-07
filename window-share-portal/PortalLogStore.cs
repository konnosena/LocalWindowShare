using Microsoft.Extensions.Logging;

internal sealed class PortalLogStore
{
    private readonly object _sync = new();
    private readonly int _capacity;
    private readonly List<PortalLogEntry> _entries = new();
    private long _nextSequenceId = 1;

    public PortalLogStore(int capacity = 200)
    {
        _capacity = Math.Max(50, capacity);
    }

    public long LastSequenceId
    {
        get
        {
            lock (_sync)
            {
                return _entries.Count == 0 ? 0 : _entries[^1].SequenceId;
            }
        }
    }

    public IReadOnlyList<PortalLogEntry> GetEntries()
    {
        lock (_sync)
        {
            return _entries.ToArray();
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
        }
    }

    public void Add(LogLevel level, string source, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        lock (_sync)
        {
            _entries.Add(new PortalLogEntry(
                _nextSequenceId++,
                DateTimeOffset.UtcNow,
                level.ToString(),
                NormalizeSource(source),
                message.Trim()));

            var overflow = _entries.Count - _capacity;
            if (overflow > 0)
            {
                _entries.RemoveRange(0, overflow);
            }
        }
    }

    public void AddInformation(string source, string message) => Add(LogLevel.Information, source, message);

    public void AddWarning(string source, string message) => Add(LogLevel.Warning, source, message);

    public void AddError(string source, string message) => Add(LogLevel.Error, source, message);

    private static string NormalizeSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return "app";
        }

        var trimmed = source.Trim();
        var separatorIndex = trimmed.LastIndexOf('.');
        return separatorIndex >= 0 && separatorIndex < trimmed.Length - 1
            ? trimmed[(separatorIndex + 1)..]
            : trimmed;
    }
}

internal sealed record PortalLogEntry(long SequenceId, DateTimeOffset TimestampUtc, string Level, string Source, string Message);

internal sealed class PortalLogLoggerProvider : ILoggerProvider
{
    private readonly PortalLogStore _logStore;

    public PortalLogLoggerProvider(PortalLogStore logStore)
    {
        _logStore = logStore;
    }

    public ILogger CreateLogger(string categoryName) => new PortalLogLogger(_logStore, categoryName);

    public void Dispose()
    {
    }
}

internal sealed class PortalLogLogger : ILogger
{
    private static readonly HashSet<string> IgnoredSources = new(StringComparer.OrdinalIgnoreCase)
    {
        "Diagnostics",
        "EndpointMiddleware",
        "OkObjectResult",
        "StaticFileMiddleware",
    };

    private readonly PortalLogStore _logStore;
    private readonly string _categoryName;

    public PortalLogLogger(PortalLogStore logStore, string categoryName)
    {
        _logStore = logStore;
        _categoryName = categoryName;
    }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var normalizedSource = NormalizeSource(_categoryName);
        if (IgnoredSources.Contains(normalizedSource))
        {
            return;
        }

        var message = formatter(state, exception);
        if (exception is not null)
        {
            message = string.IsNullOrWhiteSpace(message)
                ? exception.ToString()
                : $"{message}{Environment.NewLine}{exception}";
        }

        _logStore.Add(logLevel, normalizedSource, message);
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }

    private static string NormalizeSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return "app";
        }

        var trimmed = source.Trim();
        var separatorIndex = trimmed.LastIndexOf('.');
        return separatorIndex >= 0 && separatorIndex < trimmed.Length - 1
            ? trimmed[(separatorIndex + 1)..]
            : trimmed;
    }
}
