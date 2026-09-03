using Fortiq.Application;

namespace Fortiq.Infrastructure.Keys;

/// <summary>
/// Hands the engine a one-shot <c>--password-command</c> that starts the Fortiq password helper.
/// The secret travels over a single-use CurrentUserOnly pipe; the command line carries only the
/// pinned helper path and a non-secret operation ID.
/// </summary>
/// <remarks>
/// Known limitation, tracked as the production password broker gate: the pipe is restricted to the
/// current user and the handover is a one-shot challenge-response, but the server does not yet
/// verify the connecting client's PID and service identity, and the installer-defined SDDL is not
/// applied. Until that lands, the handover is only as strong as the isolation between processes
/// running as the same user.
/// </remarks>
public sealed class PasswordPipeCredentialProvider : IEngineCredentialProvider
{
    private readonly string _helperPath;
    private readonly IKeyLease _lease;
    private readonly TimeSpan _handoverTimeout;

    public PasswordPipeCredentialProvider(string helperPath, IKeyLease lease, TimeSpan? handoverTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(helperPath);
        ArgumentNullException.ThrowIfNull(lease);

        _helperPath = Path.GetFullPath(helperPath);
        if (!File.Exists(_helperPath))
        {
            throw new FileNotFoundException("Password helper is missing.", _helperPath);
        }

        _lease = lease;
        _handoverTimeout = handoverTimeout ?? TimeSpan.FromSeconds(30);
    }

    public Task<IEngineCredentialSession> BeginAsync(Guid operationId, CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("A credential session requires the operation ID it belongs to.", nameof(operationId));
        }

        var server = new PasswordPipeServer(operationId, _lease);
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var served = server.ServeOnceAsync(lifetime.Token);
        return Task.FromResult<IEngineCredentialSession>(
            new Session(_helperPath, operationId, served, lifetime, _handoverTimeout));
    }

    private sealed class Session : IEngineCredentialSession
    {
        private readonly Task _served;
        private readonly CancellationTokenSource _lifetime;
        private readonly TimeSpan _handoverTimeout;

        internal Session(
            string helperPath,
            Guid operationId,
            Task served,
            CancellationTokenSource lifetime,
            TimeSpan handoverTimeout)
        {
            _served = served;
            _lifetime = lifetime;
            _handoverTimeout = handoverTimeout;
            EngineArguments = ["--password-command", $"\"{helperPath}\" {operationId:D}"];
        }

        public IReadOnlyList<string> EngineArguments { get; }

        public async Task CompleteAsync(CancellationToken cancellationToken)
        {
            // The engine has already exited, so the helper either completed the handover or never
            // asked for the password. Both outcomes have to be visible to the caller.
            var completed = await Task.WhenAny(_served, Task.Delay(_handoverTimeout, cancellationToken));
            if (!ReferenceEquals(completed, _served))
            {
                throw new UnlockFailedException();
            }

            try
            {
                await _served;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                throw new UnlockFailedException(error);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _lifetime.CancelAsync();
            try
            {
                await _served;
            }
            catch
            {
                // A pipe that was never used, or was torn down with the operation, is not an error
                // by itself; CompleteAsync is what reports a failed handover.
            }

            _lifetime.Dispose();
        }
    }
}
