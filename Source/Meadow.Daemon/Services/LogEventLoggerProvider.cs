using Microsoft.Extensions.Logging;
using Meadow.Daemon.Contracts.V1;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Meadow.Daemon.Services;

[ProviderAlias("LogEventChannel")]
public sealed class LogEventLoggerProvider : ILoggerProvider
{
    private readonly LogEventChannel _channel;
    public LogEventLoggerProvider(LogEventChannel channel) => _channel = channel;

    public ILogger CreateLogger(string categoryName)
        => new LogEventLogger(categoryName, _channel);

    public void Dispose() { }
}

internal sealed class LogEventLogger : ILogger
{
    private readonly string _category;
    private readonly LogEventChannel _channel;

    public LogEventLogger(string category, LogEventChannel channel)
    { 
        _category = category; 
        _channel = channel; 
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    
    public bool IsEnabled(LogLevel level) => level >= LogLevel.Debug;

    public void Log<TState>(LogLevel level, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var evt = new LogEvent
        {
            TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Level       = MapLevel(level),
            SourceContext = _category,
            Message     = formatter(state, exception)
        };
        
        if (exception != null)
            evt.Properties["exception"] = exception.ToString();
            
        _channel.TryWrite(evt);
    }

    private static Meadow.Daemon.Contracts.V1.LogLevel MapLevel(LogLevel l) => l switch
    {
        LogLevel.Trace       => Meadow.Daemon.Contracts.V1.LogLevel.Trace,
        LogLevel.Debug       => Meadow.Daemon.Contracts.V1.LogLevel.Debug,
        LogLevel.Information => Meadow.Daemon.Contracts.V1.LogLevel.Info,
        LogLevel.Warning     => Meadow.Daemon.Contracts.V1.LogLevel.Warn,
        LogLevel.Error       => Meadow.Daemon.Contracts.V1.LogLevel.Error,
        LogLevel.Critical    => Meadow.Daemon.Contracts.V1.LogLevel.Fatal,
        _                    => Meadow.Daemon.Contracts.V1.LogLevel.Info
    };
}
