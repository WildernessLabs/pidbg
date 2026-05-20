using System;
using Microsoft.Extensions.Logging;

namespace PiDbg.DebugAdapter;

internal static partial class ProgramLog
{
    [LoggerMessage(Level = LogLevel.Information,
        Message = "Proxying DAP: VS Code ↔ vsdbg at localhost:{Port}")]
    internal static partial void ProxyStarted(ILogger logger, int port);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Adapter crashed")]
    internal static partial void AdapterCrashed(ILogger logger, Exception ex);
}
