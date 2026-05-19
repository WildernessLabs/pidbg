using System;
using Microsoft.Extensions.Logging;

namespace PiDbg.Infrastructure;

// Adapts IOutputWindowService to ILogger<T> so Core types can use MEL without VS SDK.
internal sealed class VsOutputWindowLogger<T> : ILogger<T>
{
    private readonly IOutputWindowService _output;

    public VsOutputWindowLogger(IOutputWindowService output) => _output = output;

    public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId,
        TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var msg = formatter(state, exception);
        if (exception != null) msg += $"\n{exception.Message}";

        switch (logLevel)
        {
            case LogLevel.Warning:
                _output.WriteWarning(OutputPane.PiDbg, msg);
                break;
            case LogLevel.Error:
            case LogLevel.Critical:
                _output.WriteError(OutputPane.PiDbg, msg);
                break;
            default:
                _output.WriteLine(OutputPane.PiDbg, msg);
                break;
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new NullScope();
        public void Dispose() { }
    }
}
