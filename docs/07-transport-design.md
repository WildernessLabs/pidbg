# PiDbg — Transport Design

---

## 1. Transport Stack Overview

```
┌──────────────────────────────────────────────────────┐
│  Application Layer                                   │
│  ┌──────────────────┐  ┌──────────────────────────┐ │
│  │  gRPC / HTTP2    │  │  vsdbg protocol (TCP)    │ │
│  │  (control plane) │  │  (debug plane, opaque)   │ │
│  └────────┬─────────┘  └─────────────┬────────────┘ │
└───────────┼─────────────────────────┼───────────────┘
            │                         │
┌───────────▼─────────────────────────▼───────────────┐
│  SSH Tunnel Layer (SSH.NET ForwardedPortLocal)       │
│                                                     │
│  ForwardedPortLocal(localPort=A, remoteHost=127.0.0.1, │
│                      remotePort=50051)  [gRPC]      │
│  ForwardedPortLocal(localPort=B, remoteHost=127.0.0.1, │
│                      remotePort=4024)   [vsdbg]     │
└───────────────────────────┬─────────────────────────┘
                            │
┌───────────────────────────▼─────────────────────────┐
│  SSH Session (SSH.NET SshClient)                    │
│  Authentication: public-key (RSA-4096 or Ed25519)  │
│  Keepalive: SSH server-side, every 30s              │
│  TCP: port 22 (configurable per device)             │
└─────────────────────────────────────────────────────┘
            │
            │ TCP/IP (port 22)
            │
┌───────────▼─────────────────────────────────────────┐
│  Raspberry Pi (OpenSSH daemon)                      │
│                                                     │
│  Port 50051: PiDbg.Agent gRPC (127.0.0.1 only)     │
│  Port 4024+: vsdbg TCP server (127.0.0.1 only)     │
└─────────────────────────────────────────────────────┘
```

---

## 2. SSH Session Management

### Connection Options

```csharp
public sealed record SshConnectionOptions
{
    public required string Host { get; init; }
    public required int Port { get; init; }       // default: 22
    public required string Username { get; init; }
    public required SshAuthMethod AuthMethod { get; init; }
    public string? PrivateKeyPath { get; init; }  // for key auth
    public string? PrivateKeyPassphrase { get; init; }
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(30);
}
```

### Session lifecycle
One `SshClient` per device. Shared by:
- All `ForwardedPortLocal` tunnels on this device
- `SftpClient` (SFTP uses a sub-channel of the same SSH session)

SSH.NET does not expose an async `ConnectAsync` directly on `SshClient`. Use:
```csharp
await Task.Run(() => _client.Connect(), cancellationToken);
```
This unblocks the caller while the TCP connection and SSH handshake complete.

### Keepalive strategy
Server-side keepalive is preferred (not client-side) because it avoids SSH.NET timer
complexity. The Pi's `/etc/ssh/sshd_config` should have:
```
ClientAliveInterval 30
ClientAliveCountMax 3
```
The provisioning script (`setup-pi.sh`) ensures this is set.

If the session drops unexpectedly, `SshConnectionManager` detects it via
`_client.IsConnected` polling every 10 seconds and emits `ConnectionStateChanged(Disconnected)`.
The VSIX UI shows a warning. Reconnect is manual (user clicks "Reconnect" in Device Manager)
or automatic on next F5 (configurable).

---

## 3. Port Forwarding

### Port Allocation
Local ports are allocated ephemerally:
```csharp
static int AllocateEphemeralPort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}
```
There is a TOCTOU window between releasing and binding. In practice this is not a problem
on a developer machine where port churn is low. If `GrpcChannel` fails to bind, the
allocator retries up to 5 times with fresh ports.

### gRPC tunnel
Created during `DeviceConnectionFactory.GetOrCreateConnectionAsync()`:
- Remote end: `127.0.0.1:50051` on Pi
- Local end: `127.0.0.1:<dynamic>` on developer machine
- Lifetime: same as `IDeviceConnection`

### vsdbg tunnel
Created during `DebugSessionOrchestrator.StartSessionAsync()`:
- Remote end: `127.0.0.1:<vsdbgPort>` on Pi (agent selects port)
- Local end: `127.0.0.1:<dynamic>` on developer machine
- Lifetime: debug session lifetime only (closed on session end)

### vsdbg port selection on Pi
The agent selects the vsdbg listen port:
```csharp
static int AllocateVsdbgPort()
{
    // Use port range 4024–4124 to stay within predictable range
    // Port 4024 is vsdbg's default — use it first if available
    foreach (int candidate in Enumerable.Range(4024, 100))
    {
        if (!IsPortInUse(candidate)) return candidate;
    }
    throw new InvalidOperationException("No vsdbg port available in range 4024-4124");
}
```
The allocated port is returned in `StartSessionResponse.VsdbgPort` so the VSIX knows
which remote port to tunnel.

---

## 4. gRPC Channel Configuration

### Channel setup (VSIX side)
```csharp
var channel = GrpcChannel.ForAddress(
    $"http://localhost:{localForwardedPort}",
    new GrpcChannelOptions
    {
        HttpHandler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests
        },
        MaxRetryAttempts = 0,         // Polly handles retries, not gRPC
        MaxReceiveMessageSize = null, // unlimited (deployment chunks)
        MaxSendMessageSize = null
    });
```

No TLS on the gRPC channel — the SSH tunnel provides transport security.
Using `http://` (not `https://`) is intentional. The channel trusts the SSH layer.

### Kestrel setup (Agent side)
```csharp
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenLocalhost(50051, listenOptions =>
    {
        listenOptions.Protocols = HttpProtocols.Http2;
        // No TLS — SSH tunnel provides security
    });
});
```

`ListenLocalhost` binds to `127.0.0.1` only. No external access possible.

---

## 5. SFTP Transfer Design

### Chunking strategy
SSH.NET's `SftpClient.UploadFile()` is synchronous. For large publish outputs (potentially
100+ MB with all .NET runtime files in self-contained mode), this blocks a thread.

Strategy: use SSH.NET's async-capable `BeginUploadFile/EndUploadFile` or wrap in
`Task.Run`. For .NET 10 published output (framework-dependent), typical size is 1–10 MB.

Buffer size: 65536 bytes (64 KB) per SFTP packet. This matches SSH.NET's default and
avoids excessive round-trips.

### Upload sequence
Files are uploaded individually (not as a tar.gz) for:
- Progress granularity
- Delta support (Phase 2: skip unchanged files)
- Partial failure recovery (can resume from last successful file)

Upload to `/opt/pidbg/apps/<deployment-id>/staging/<filename>`. Remote directory
structure is created with `SftpClient.CreateDirectory()` before upload.

### Integrity check
After all files uploaded, the VSIX calls `AgentClient.CommitDeploymentAsync()` with the
full manifest. The agent verifies every SHA-256 in the manifest against the uploaded files.
If any mismatch: deployment fails, staging is cleaned up, `DeploymentError` is returned.

---

## 6. Reconnection Policy

### SSH reconnection (Polly)
```csharp
_reconnectPolicy = Policy
    .Handle<SshConnectionException>()
    .Or<SocketException>()
    .WaitAndRetryAsync(
        retryCount: 5,
        sleepDurationProvider: attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)),
        onRetry: (ex, delay, attempt, ctx) =>
        {
            _logger.LogWarning("SSH reconnect attempt {Attempt}, delay {Delay}s: {Error}",
                attempt, delay.TotalSeconds, ex.Message);
        });
```
Max wait: 32 seconds between attempts. After 5 attempts, fails to caller.

### gRPC reconnection
gRPC over HTTP/2 has built-in reconnect semantics. The `GrpcChannel` attempts to
reconnect automatically. Combined with Polly on the VSIX call sites, transient
disconnects (Pi reboot, network blip) are handled transparently.

---

## 7. Protocol Buffers Design

### Chunk streaming for deployment
```protobuf
message DeploymentChunk {
  string deployment_id = 1;
  string relative_path = 2;    // e.g., "MyApp.dll"
  bytes data = 3;
  uint64 offset = 4;
  bool is_last_chunk = 5;
}
```
Each file is sent as a series of chunks (64 KB each). The `relative_path` resets on each
new file. The agent writes each chunk directly to disk using `offset` for random-access
writes (supports resume in Phase 2).

### Streaming log events
```protobuf
message LogEvent {
  google.protobuf.Timestamp timestamp = 1;
  LogLevel level = 2;
  string message = 3;
  string source = 4;        // "agent", "vsdbg", "process"
  map<string, string> properties = 5;
}
```
Log events are pushed from the agent's Serilog `LogEventChannel` sink (a
`Channel<LogEvent>` that the gRPC stream drains) to the VSIX Output window.
