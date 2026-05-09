using Meadow.Daemon.GrpcService;
using Meadow.Daemon.Services;
using Meadow.Daemon.Contracts.V1;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using System.Net;

var builder = WebApplication.CreateBuilder(args);

// ── Configuration ─────────────────────────────────────────────────────────────

builder.Configuration
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("/etc/meadow/daemon.conf", optional: true)
    .AddEnvironmentVariables("MEADOW_");

var daemonOpts = builder.Configuration
    .GetSection("Meadow")
    .Get<DaemonOptions>() ?? new DaemonOptions();

builder.Services.AddSingleton(daemonOpts);

// ── Kestrel ──────────────────────────────────────────────────────────────────

builder.WebHost.ConfigureKestrel(kestrel =>
{
    // gRPC — HTTP/2 only
    kestrel.Listen(IPAddress.Loopback, daemonOpts.GrpcPort, o =>
        o.Protocols = HttpProtocols.Http2);

    // REST compat — HTTP/1.1
    kestrel.Listen(IPAddress.Loopback, daemonOpts.RestPort, o =>
        o.Protocols = HttpProtocols.Http1);
});

// ── systemd integration ───────────────────────────────────────────────────────

builder.Host.UseSystemd();

// ── Logging ───────────────────────────────────────────────────────────────────

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(opts =>
{
    opts.IncludeScopes = true;
    opts.TimestampFormat = "O";
    opts.UseUtcTimestamp = true;
    opts.JsonWriterOptions = new System.Text.Json.JsonWriterOptions { Indented = false };
});

// ── Services ─────────────────────────────────────────────────────────────────

builder.Services
    .AddGrpc()
    .AddServiceOptions<MeadowDaemonGrpcService>(opts =>
    {
        opts.MaxReceiveMessageSize = 64 * 1024 * 1024; // 64 MB (for file chunk streaming)
        opts.MaxSendMessageSize    = 64 * 1024 * 1024;
    });

builder.Services.AddGrpcHealthChecks();
builder.Services.AddControllers(); // REST compat

// Domain services
builder.Services.AddSingleton<StateStore>();
builder.Services.AddSingleton<VersionStore>();
builder.Services.AddSingleton<StagingController>();
builder.Services.AddSingleton<ManifestVerifier>();
builder.Services.AddSingleton<DeploymentManager>();
builder.Services.AddSingleton<ProcessManager>();
builder.Services.AddSingleton<VsdbgInstaller>();
builder.Services.AddSingleton<VsdbgManager>();
builder.Services.AddSingleton<VsdbgLauncher>();
builder.Services.AddSingleton<DebugSessionManager>();

// Background services
builder.Services.AddHostedService<ProcessMonitorService>();
builder.Services.AddHostedService<HealthReporterService>();
builder.Services.AddHostedService<OtaUpdateService>();

// gRPC log channel (shared between logger provider and StreamLogs handler)
builder.Services.AddSingleton<LogEventChannel>();

// ── Build ─────────────────────────────────────────────────────────────────────

var app = builder.Build();

app.MapGrpcService<MeadowDaemonGrpcService>();
app.MapGrpcHealthChecksService();
app.MapControllers();

app.Run();
