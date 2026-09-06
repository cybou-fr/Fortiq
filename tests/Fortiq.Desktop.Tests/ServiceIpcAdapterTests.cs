using System.Runtime.Versioning;
using Fortiq.Application;
using Fortiq.Desktop;
using Fortiq.Desktop.ViewModels;
using Fortiq.Infrastructure.Keys;
using Fortiq.Operations;
using Fortiq.Provisioning;
using Fortiq.Scheduling;

namespace Fortiq.Desktop.Tests;

[SupportedOSPlatform("windows")]
public sealed class ServiceIpcAdapterTests
{
    [Fact]
    public async Task ProtectAdapterDelegatesToServiceIpcWhenServiceIsAvailable()
    {
        var temp = Path.Combine(Path.GetTempPath(), "fortiq-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var paths = FortiqStatePaths.Resolve(temp);
            var mockClient = new StubServiceIpcClient
            {
                IsAvailable = true,
                ProvisionResult = new ServiceIpcProtocol.ProvisionResponse(
                    "repo-123",
                    "alpha bravo charlie delta echo foxtrot golf hotel india juliet kilo lima mike november oscar papa quebec romeo sierra tango uniform victor whiskey xray",
                    DeviceUnlockAvailable: true,
                    BackupScheduled: true)
            };

            var dummyProvisioner = new RepositoryProvisioner(temp);
            var adapter = new ProtectRepositoryAdapter(dummyProvisioner, paths, serviceClient: mockClient);

            var request = new ProtectRepositoryRequest(
                Path.Combine(temp, "repo"),
                Path.Combine(temp, "kit"),
                Path.Combine(temp, "source"));

            var result = await adapter.CreateAsync(request, CancellationToken.None);

            Assert.True(mockClient.ProvisionCalled);
            Assert.Equal("repo-123", result.RepositoryId);
            Assert.True(result.DeviceUnlockAvailable);
            Assert.True(result.BackupScheduled);
            Assert.Equal(mockClient.ProvisionResult.Mnemonic, result.RecoveryMnemonic);
        }
        finally
        {
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ProveAdapterDelegatesToServiceIpcWhenServiceIsAvailable()
    {
        var temp = Path.Combine(Path.GetTempPath(), "fortiq-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var paths = FortiqStatePaths.Resolve(temp);
            var mockClient = new StubServiceIpcClient
            {
                IsAvailable = true,
                ProveResult = true
            };

            var schedules = new FileSystemScheduleStore(paths.Schedules);
            var dummyRestore = new ProvenRestore(temp, paths.Working, paths.Runs, paths.Receipts);
            var dummyHealth = new HealthPublisher(schedules, paths.Receipts, paths.HealthReport, paths.HealthMetrics);

            var adapter = new ProveRecoveryAdapter(schedules, dummyRestore, dummyHealth, serviceClient: mockClient);

            var proven = await adapter.ProveAsync("repo-456", CancellationToken.None);

            Assert.True(mockClient.ProveCalled);
            Assert.Equal("repo-456", mockClient.LastRepositoryId);
            Assert.True(proven);
        }
        finally
        {
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, recursive: true);
            }
        }
    }

    [Fact]
    public async Task UnavailableInstalledServiceNeverFallsBackToLocalRestore()
    {
        var root = Path.Combine(Path.GetTempPath(), "fortiq-missing-" + Guid.NewGuid().ToString("N"));
        var paths = FortiqStatePaths.Resolve(root);
        var schedules = new FileSystemScheduleStore(paths.Schedules);
        var client = new StubServiceIpcClient { IsAvailable = false };
        var adapter = new ProveRecoveryAdapter(schedules,
            new ProvenRestore(root, paths.Working, paths.Runs, paths.Receipts),
            new HealthPublisher(schedules, paths.Receipts, paths.HealthReport, paths.HealthMetrics), client);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.ProveAsync("repo", CancellationToken.None));
        Assert.Contains("service is unavailable", error.Message, StringComparison.Ordinal);
        Assert.False(client.ProveCalled);
        Assert.False(Directory.Exists(root));
    }

    private sealed class StubServiceIpcClient : IServiceIpcClient
    {
        public bool IsAvailable { get; set; } = true;
        public bool ProvisionCalled { get; private set; }
        public bool ProveCalled { get; private set; }
        public string? LastRepositoryId { get; private set; }
        public ServiceIpcProtocol.ProvisionResponse? ProvisionResult { get; set; }
        public bool ProveResult { get; set; } = true;
        public bool BackupCalled { get; private set; }
        public ServiceIpcProtocol.BackupResponse? BackupResult { get; set; }
        public SourceSettings? UpdatedSchedule { get; private set; }
        public string? RemovedSchedule { get; private set; }

        public Task<bool> IsServiceAvailableAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(IsAvailable);

        public Task<ServiceIpcProtocol.ProvisionResponse> ProvisionAsync(
            string repositoryLocation,
            string kitDirectory,
            string sourcePath,
            CancellationToken cancellationToken = default)
        {
            ProvisionCalled = true;
            return Task.FromResult(ProvisionResult ?? new ServiceIpcProtocol.ProvisionResponse(
                "mock-id", "mock-mnemonic", true, true));
        }

        public Task<bool> ProveRecoveryAsync(string repositoryId, CancellationToken cancellationToken = default)
        {
            ProveCalled = true;
            LastRepositoryId = repositoryId;
            return Task.FromResult(ProveResult);
        }

        public Task<ServiceIpcProtocol.BackupResponse> BackupAsync(string repositoryId, CancellationToken cancellationToken = default)
        {
            BackupCalled = true;
            LastRepositoryId = repositoryId;
            return Task.FromResult(BackupResult ?? new ServiceIpcProtocol.BackupResponse(true, "snapshot-1"));
        }

        public Task UpdateScheduleAsync(string repositoryId, SourceSettings settings, CancellationToken cancellationToken = default)
        {
            LastRepositoryId = repositoryId;
            UpdatedSchedule = settings;
            return Task.CompletedTask;
        }

        public Task RemoveScheduleAsync(string repositoryId, CancellationToken cancellationToken = default)
        {
            LastRepositoryId = repositoryId;
            RemovedSchedule = repositoryId;
            return Task.CompletedTask;
        }
    }
}