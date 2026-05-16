using System.Reflection;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using Meadow.Daemon;
using Meadow.Daemon.GrpcService;
using Meadow.Daemon.Services;
using Meadow.Daemon.Models;

var builder = WebApplication.CreateBuilder(args);

// ── Configuration ─────────────────────────────────────────────────────────────

builder.Configuration
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true)
    .AddJsonFile("/etc/meadow/daemon.conf", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables(prefix: "MEADOW_");

// ── Options ──────────────────────────────────────────────────────────────────

builder.Services
    .AddOptions<DaemonOptions>()
    .BindConfiguration(DaemonOptions.Section)
    .ValidateDataAnnotations()
    .ValidateOnStart();

// ── Logging ───────────────────────────────────────────────────────────────────

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o =>
{
    o.IncludeScopes = true;
    o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
    o.UseUtcTimestamp = true;
});

builder.Services.AddSingleton<LogEventChannel>();
builder.Logging.Services.AddSingleton<ILoggerProvider, LogEventLoggerProvider>();

// ── systemd ───────────────────────────────────────────────────────────────────

builder.Host.UseSystemd();

// ── Services ─────────────────────────────────────────────────────────────────

// Domain services (some are placeholders for later phases)
builder.Services.AddSingleton<StateStore>();
builder.Services.AddSingleton<VersionStore>();
builder.Services.AddSingleton<StagingController>();
builder.Services.AddSingleton<ManifestVerifier>();
builder.Services.AddSingleton<IDeploymentManager, DeploymentManager>();
builder.Services.AddSingleton<IProcessManager, ProcessManager>();
builder.Services.AddSingleton<IVsdbgInstaller, VsdbgInstaller>();
builder.Services.AddSingleton<VsdbgManager>();
builder.Services.AddSingleton<VsdbgLauncher>();
builder.Services.AddSingleton<IDebugSessionManager, DebugSessionManager>();

// Background services
builder.Services.AddHostedService<ProcessMonitorService>();
builder.Services.AddHostedService<HealthReporterService>();
builder.Services.AddHostedService<OtaUpdateService>();

// gRPC
builder.Services.AddGrpc(o =>
{
    o.EnableDetailedErrors = builder.Environment.IsDevelopment();
    o.MaxReceiveMessageSize = 64 * 1024 * 1024;  // 64 MB for binary uploads
    o.MaxSendMessageSize    = 64 * 1024 * 1024;
});
builder.Services.AddGrpcReflection();
builder.Services.AddGrpcHealthChecks();
builder.Services.AddHealthChecks()
    .AddCheck<DaemonHealthCheck>("daemon");

builder.Services.Configure<Grpc.AspNetCore.HealthChecks.GrpcHealthChecksOptions>(o =>
{
    o.Services.Map("meadow.daemon.v1.MeadowDaemonService", r => r.Name == "daemon");
});

// REST (compat)
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.TypeInfoResolverChain.Insert(0, DaemonJsonContext.Default);
    });

// ── Kestrel ──────────────────────────────────────────────────────────────────

builder.WebHost.ConfigureKestrel((ctx, opts) =>
{
    var daemon = ctx.Configuration.GetSection(DaemonOptions.Section);
    var grpcPort = daemon.GetValue<int>("GrpcPort", 50051);
    var restPort = daemon.GetValue<int>("RestPort", 5000);

    // gRPC endpoint: HTTP/2 only, no TLS (SSH tunnel provides encryption)
    opts.ListenLocalhost(grpcPort, lo =>
    {
        lo.Protocols = HttpProtocols.Http2;
    });

    // REST compat endpoint: HTTP/1.1 only
    opts.ListenLocalhost(restPort, lo =>
    {
        lo.Protocols = HttpProtocols.Http1;
    });
});

var app = builder.Build();

// Ensure directories exist before any service starts
var daemonOptions = app.Services.GetRequiredService<IOptions<DaemonOptions>>().Value;
DaemonPaths.EnsureDirectories(daemonOptions);

// ── Middleware ────────────────────────────────────────────────────────────────

app.MapGrpcService<MeadowDaemonGrpcService>();
app.MapGrpcHealthChecksService();
app.MapHealthChecks("/health");

if (app.Environment.IsDevelopment())
{
    app.MapGrpcReflectionService();
}

app.MapControllers();

var logger = app.Services.GetRequiredService<ILogger<Program>>();
var version = Assembly.GetExecutingAssembly()
                      .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                      ?.InformationalVersion ?? "1.0.0";

logger.LogInformation("Meadow Daemon {Version} starting on gRPC:{GrpcPort} REST:{RestPort}",
    version, daemonOptions.GrpcPort, daemonOptions.RestPort);

app.Run();
