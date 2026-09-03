using Fortiq.Application;
using Fortiq.Infrastructure.Restic;

namespace Fortiq.Restic.ContractTests;

public sealed class ResticRepositoryEngineTests
{
    [Fact]
    public async Task InitializeMapsRepositoryIdentityAndUsesTypedOperation()
    {
        var runner = new RecordingRunner(new ResticProcessResult(
            0,
            "{\"message_type\":\"initialized\",\"id\":\"b35df32937248eef97b3fdde9738c5004b463bef27e6798bd164f0c995ca8f2c\",\"repository\":\"fixture\"}",
            string.Empty));
        var adapter = CreateAdapter(runner);

        var repository = await adapter.InitializeAsync(new InitializeRepository("repository"), CancellationToken.None);

        Assert.Equal(64, repository.Id.ToString().Length);
        Assert.Equal(ResticOperation.Initialize, runner.LastRequest!.Operation);
        Assert.Contains("--insecure-no-password", runner.LastRequest.Arguments);
    }

    [Fact]
    public async Task FailedEngineExitDoesNotCreateReceipt()
    {
        var runner = new RecordingRunner(new ResticProcessResult(1, string.Empty, "fatal"));
        var adapter = CreateAdapter(runner);
        var repository = new Fortiq.Domain.RepositoryDescriptor(Fortiq.Domain.RepositoryId.Create(), Path.GetFullPath("repository"));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => adapter.CheckAsync(new CheckRepository(repository), CancellationToken.None));
    }

    private static ResticRepositoryEngine CreateAdapter(IResticProcessRunner runner) =>
        new(
            new VerifiedEngine("restic", "0.19.1", "win-x64", Path.GetFullPath("restic.exe"), new string('0', 64)),
            runner,
            Directory.GetCurrentDirectory());

    private sealed class RecordingRunner(ResticProcessResult result) : IResticProcessRunner
    {
        public ResticProcessRequest? LastRequest { get; private set; }

        public Task<ResticProcessResult> RunAsync(VerifiedEngine engine, ResticProcessRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(result);
        }
    }
}
