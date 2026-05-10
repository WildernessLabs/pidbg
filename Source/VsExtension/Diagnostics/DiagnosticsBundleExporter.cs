using Meadow.Daemon.Contracts.V1;
using PiDbg.Infrastructure;

namespace PiDbg.Diagnostics;

// Exports a ZIP bundle containing daemon logs, vsdbg info, host info,
// SSH connectivity result, extension log, and system journal.
// Implemented in Phase 7 (P7.8).
internal sealed class DiagnosticsBundleExporter
{
    private readonly MeadowDaemonService.MeadowDaemonServiceClient _grpc;
    private readonly SshConnectionManager _ssh;

    public DiagnosticsBundleExporter(
        MeadowDaemonService.MeadowDaemonServiceClient grpc,
        SshConnectionManager ssh)
    {
        _grpc = grpc;
        _ssh = ssh;
    }

    public Task<string> ExportAsync(string outputPath, CancellationToken ct)
        => throw new NotImplementedException("Implemented in Phase 7");
}
