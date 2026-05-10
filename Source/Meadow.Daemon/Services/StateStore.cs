using System.Text.Json;

namespace Meadow.Daemon.Services;

internal sealed class StateStore
{
    private readonly string _root;
    private readonly ILogger<StateStore> _log;

    public StateStore(DaemonOptions opts, ILogger<StateStore> log)
    {
        _root = opts.StateRoot;
        _log = log;
        Directory.CreateDirectory(_root);
    }

    public async Task<T?> ReadAsync<T>(string key, CancellationToken ct = default) where T : class
    {
        var path = FilePath(key);
        if (!File.Exists(path)) return null;
        try
        {
            await using var fs = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(fs, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to read state key {Key}", key);
            return null;
        }
    }

    public async Task WriteAsync<T>(string key, T value, CancellationToken ct = default) where T : class
    {
        var path = FilePath(key);
        var tmp = path + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using (var fs = File.Create(tmp))
            await JsonSerializer.SerializeAsync(fs, value, cancellationToken: ct);
        File.Move(tmp, path, overwrite: true); // atomic rename(2) on Linux
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        File.Delete(FilePath(key));
        return Task.CompletedTask;
    }

    private string FilePath(string key) => Path.Combine(_root, key + ".json");
}
