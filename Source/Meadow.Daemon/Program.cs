using Meadow.Daemon.GrpcService;
using Meadow.Daemon.Services;
using Meadow.Daemon.Contracts.V1;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using System.Net;

var builder = WebApplication.CreateBuilder(args);

// ── Configuration ─────────────────────────────────────────────────────────────

builder.Configuration
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true)
    .AddJsonFile("/etc/meadow/daemon.conf", optional: true)
    .AddEnvironmentVariables("MEADOW_");

var daemonOpts = builder.Configuration
    .GetSection("Meadow")
    .Get<DaemonOptions>() ?? new DaemonOptions();

builder.Services.AddSingleton(daemonOpts);

// ── Kestrel ──────────────────────────────────────────────────────────────────

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.Listen(IPAddress.Loopback, daemonOpts.GrpcPort, o =>
        o.Protocols = HttpProtocols.Http2);

    kestrel.Listen(IPAddress.Loopback, daemonOpts.RestPort, o =>
        o.Protocols = HttpProtocols.Http1);
});

// ── systemd ───────────────────────────────────────────────────────────────────

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
        opts.MaxReceiveMessageSize = 64 * 1024 * 1024;
        opts.MaxSendMessageSize    = 64 * 1024 * 1024;
    });

builder.Services.AddGrpcHealthChecks();
builder.Services.AddControllers();

// Infrastructure
builder.Services.AddSingleton<LogEventChannel>();
builder.Services.AddSingleton<StateStore>();

// Deployment pipeline (Phase 3)
builder.Services.AddSingleton<VersionStore>();
builder.Services.AddSingleton<StagingController>();
builder.Services.AddSingleton<ManifestVerifier>();
builder.Services.AddSingleton<DeploymentManager>();

// Process management (Phase 5)
builder.Services.AddSingleton<ProcessManager>();

// vsdbg management (Phase 5)
builder.Services.AddSingleton<VsdbgInstaller>();
builder.Services.AddSingleton<VsdbgManager>();
builder.Services.AddSingleton<VsdbgLauncher>();
builder.Services.AddSingleton<DebugSessionManager>();

// Background services
builder.Services.AddHostedService<ProcessMonitorService>();
builder.Services.AddHostedService<HealthReporterService>();
builder.Services.AddHostedService<OtaUpdateService>();

// ── Build ─────────────────────────────────────────────────────────────────────

var app = builder.Build();

app.MapGrpcService<MeadowDaemonGrpcService>();
app.MapGrpcHealthChecksService();
app.MapControllers();

app.Run();
