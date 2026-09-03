using Fortiq.Application;

namespace Fortiq.Infrastructure.Restic;

/// <summary>
/// The P0 test seam: it runs the engine on a repository that has no password at all. It exists so
/// the adapter can be exercised without a key provider and must never reach the Service or the
/// recovery CLI.
/// </summary>
internal sealed class InsecureNoPasswordCredentialProvider : IEngineCredentialProvider
{
    public Task<IEngineCredentialSession> BeginAsync(Guid operationId, CancellationToken cancellationToken) =>
        Task.FromResult<IEngineCredentialSession>(new Session());

    private sealed class Session : IEngineCredentialSession
    {
        public IReadOnlyList<string> EngineArguments { get; } = ["--insecure-no-password"];

        public Task CompleteAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
