using Meadow.Daemon.Contracts.V1;
using Meadow.Daemon.Models;

namespace Meadow.Daemon.Services;

public interface IDebugSessionManager
{
    Task<DebugSessionRecord> StartDebugSessionAsync(
        string appName, SessionMode mode, string correlationId, CancellationToken ct);
    
    Task StopDebugSessionAsync(string sessionId, CancellationToken ct);
    
    Task<DebugSessionRecord?> GetSessionStatusAsync(string sessionId, CancellationToken ct);
    
    Task<IReadOnlyList<DebugSessionRecord>> ListSessionsAsync(CancellationToken ct);
    
    Task TouchSessionAsync(string sessionId, CancellationToken ct);
}
