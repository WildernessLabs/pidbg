using Grpc.Core;
using Meadow.Daemon.Contracts.V1;
using Meadow.Daemon.GrpcService;
using Meadow.Daemon.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;
using FluentAssertions;

namespace Meadow.Daemon.Tests;

public class GrpcServiceTests
{
    private readonly Mock<IOptions<DaemonOptions>> _optionsMock;
    private readonly Mock<ILogger<MeadowDaemonGrpcService>> _loggerMock;
    private readonly Mock<IHostApplicationLifetime> _lifetimeMock;
    private readonly Mock<IDeploymentManager> _deploymentManagerMock;
    private readonly Mock<ProcessManager> _processManagerMock;
    private readonly Mock<VsdbgManager> _vsdbgManagerMock;
    private readonly Mock<DebugSessionManager> _debugSessionManagerMock;
    private readonly LogEventChannel _logChannel;
    private readonly StateStore _stateStore;
    private readonly MeadowDaemonGrpcService _service;

    public GrpcServiceTests()
    {
        _optionsMock = new Mock<IOptions<DaemonOptions>>();
        _optionsMock.Setup(o => o.Value).Returns(new DaemonOptions());
        _loggerMock = new Mock<ILogger<MeadowDaemonGrpcService>>();
        _lifetimeMock = new Mock<IHostApplicationLifetime>();
        
        var stateStoreLoggerMock = new Mock<ILogger<StateStore>>();
        _stateStore = new StateStore(_optionsMock.Object, stateStoreLoggerMock.Object);
        _logChannel = new LogEventChannel();

        _deploymentManagerMock = new Mock<IDeploymentManager>();

        var vsdbgInstallerMock = new Mock<VsdbgInstaller>(_optionsMock.Object, new Mock<ILogger<VsdbgInstaller>>().Object);
        
        var vsdbgManagerMock = new Mock<VsdbgManager>(
            vsdbgInstallerMock.Object,
            _optionsMock.Object,
            new Mock<ILogger<VsdbgManager>>().Object);

        var vsdbgLauncherMock = new Mock<VsdbgLauncher>(
            vsdbgManagerMock.Object,
            _optionsMock.Object,
            new Mock<ILogger<VsdbgLauncher>>().Object);

        _processManagerMock = new Mock<ProcessManager>(
            _deploymentManagerMock.Object,
            _optionsMock.Object, 
            new Mock<ILogger<ProcessManager>>().Object);

        _vsdbgManagerMock = vsdbgManagerMock;

        _debugSessionManagerMock = new Mock<DebugSessionManager>(
            vsdbgLauncherMock.Object,
            _processManagerMock.Object,
            _stateStore,
            _optionsMock.Object,
            new Mock<ILogger<DebugSessionManager>>().Object);

        _service = new MeadowDaemonGrpcService(
            _optionsMock.Object,
            _stateStore,
            _logChannel,
            _loggerMock.Object,
            _lifetimeMock.Object,
            _deploymentManagerMock.Object,
            _processManagerMock.Object,
            _vsdbgManagerMock.Object,
            _debugSessionManagerMock.Object);
    }

    [Fact]
    public async Task Ping_ReturnsVersion()
    {
        var response = await _service.Ping(new PingRequest(), null!);
        response.Version.Should().NotBeNull();
        response.Version.ProtocolVersion.Should().Be(1);
    }

    [Fact]
    public async Task GetDeviceInfo_ReturnsHostInfo()
    {
        var response = await _service.GetDeviceInfo(new GetDeviceInfoRequest(), null!);
        response.Info.Should().NotBeNull();
        response.Info.Hostname.Should().NotBeNullOrEmpty();
        response.Info.Architecture.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task BeginDeployment_ReturnsResult()
    {
        var request = new BeginDeploymentRequest
        {
            AppName = "test-app",
            Slot = DeploymentSlot.Debug,
            Manifest = new DeploymentManifest()
        };

        _deploymentManagerMock.Setup(d => d.BeginDeploymentAsync(
            It.IsAny<string>(), It.IsAny<DeploymentManifest>(), It.IsAny<DeploymentSlot>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BeginDeploymentResult("deploy-123", "/tmp/staging", new List<string> { "file1.dll" }));

        var response = await _service.BeginDeployment(request, Mock.Of<ServerCallContext>());

        response.DeploymentId.Should().Be("deploy-123");
        response.StagingDir.Should().Be("/tmp/staging");
        response.FilesNeeded.Should().Contain("file1.dll");
    }
}
