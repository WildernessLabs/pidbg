using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Meadow.Daemon.Models;

namespace Meadow.Daemon.Services;

public sealed class StateStore
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly DaemonOptions _options;
    private readonly ILogger<StateStore> _logger;

    public StateStore(IOptions<DaemonOptions> options, ILogger<StateStore> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<AppsState> LoadAppsAsync(CancellationToken ct = default)
    {
        var path = DaemonPaths.AppsStatePath(_options);
        if (!File.Exists(path)) return new AppsState();
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync(stream, DaemonJsonContext.Default.AppsState, ct)
                   ?? new AppsState();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Apps state file {Path} is corrupt; resetting to empty", path);
            return new AppsState();
        }
    }

    public async Task SaveAppsAsync(AppsState state, CancellationToken ct = default)
    {
        var path = DaemonPaths.AppsStatePath(_options);
        var sem = _locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            var tmp = path + ".tmp";
            await using (var stream = File.Create(tmp))
                await JsonSerializer.SerializeAsync(stream, state, DaemonJsonContext.Default.AppsState, ct);
            
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            sem.Release();
        }
    }

    public async Task<SessionsState> LoadSessionsAsync(CancellationToken ct = default)
    {
        var path = DaemonPaths.SessionsStatePath(_options);
        if (!File.Exists(path)) return new SessionsState();
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync(stream, DaemonJsonContext.Default.SessionsState, ct)
                   ?? new SessionsState();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Sessions state file {Path} is corrupt; resetting to empty", path);
            return new SessionsState();
        }
    }

    public async Task SaveSessionsAsync(SessionsState state, CancellationToken ct = default)
    {
        var path = DaemonPaths.SessionsStatePath(_options);
        var sem = _locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            var tmp = path + ".tmp";
            await using (var stream = File.Create(tmp))
                await JsonSerializer.SerializeAsync(stream, state, DaemonJsonContext.Default.SessionsState, ct);
            
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            sem.Release();
        }
    }
}
