using Microsoft.Extensions.Logging;

namespace SalesCore.Integration.Tests;

/// <summary>テストの中で出たログを「レベル 文言」の形で集める(ログに何を残し、何を残さないかを確かめる)。</summary>
public sealed class CapturedLogs : ILoggerProvider
{
    private readonly List<(string Category, string Line)> _lines = [];

    /// <summary>どの部品のログも含めて全部(パスワードが出ていないかを見る)。</summary>
    public IReadOnlyList<string> All
    {
        get
        {
            lock (_lines) return _lines.Select(l => l.Line).ToList();
        }
    }

    public IReadOnlyList<string> Of<T>()
    {
        lock (_lines) return _lines.Where(l => l.Category == typeof(T).FullName).Select(l => l.Line).ToList();
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(CapturedLogs logs, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (logs._lines) logs._lines.Add((category, $"{logLevel} {formatter(state, exception)}"));
        }
    }
}
