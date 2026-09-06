using Fortiq.Application;
using Fortiq.Platform.Windows;

namespace Fortiq.Infrastructure.Keys;

/// <summary>
/// Hands the engine a one-shot <c>--password-command</c> that starts the Fortiq password helper.
/// The secret travels over a single-use CurrentUserOnly pipe; the command line carries only the
/// pinned helper path and a non-secret operation ID.
/// </summary>
/// <remarks>
/// Before the password is written the broker resolves the connected client's process, requires its
/// image to be the very helper file this provider pinned open, and requires it to run as the
/// expected account. An installer-defined SDDL can be supplied to describe who may open the pipe at
/// all; without one the operating system restricts it to the current user.
/// </remarks>
public sealed class PasswordPipeCredentialProvider : IEngineCredentialProvider, IDisposable
{
    private readonly PasswordBrokerOptions _options;
    private readonly PinnedFile _helper;
    private readonly IKeyLease _lease;
    private readonly TimeSpan _handoverTimeout;

    public PasswordPipeCredentialProvider(string helperPath, IKeyLease lease, TimeSpan? handoverTimeout = null)
        : this(new PasswordBrokerOptions(helperPath), lease, handoverTimeout)
    {
    }

    public PasswordPipeCredentialProvider(PasswordBrokerOptions options, IKeyLease lease, TimeSpan? handoverTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.HelperPath);

        var helperPath = Path.GetFullPath(options.HelperPath);
        if (!File.Exists(helperPath))
        {
            throw new FileNotFoundException("Password helper is missing.", helperPath);
        }

        // The helper is pinned for the lifetime of the provider: the file that is approved to
        // receive the password cannot be replaced, and every connecting client is compared against
        // this exact file rather than against a path.
        _helper = PinnedFile.Open(helperPath);
        _options = options with { HelperPath = helperPath };
        _lease = lease;
        _handoverTimeout = handoverTimeout ?? TimeSpan.FromSeconds(30);
    }

    public void Dispose() => _helper.Dispose();

    public Task<IEngineCredentialSession> BeginAsync(Guid operationId, CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("A credential session requires the operation ID it belongs to.", nameof(operationId));
        }

        var server = new PasswordPipeServer(operationId, _lease, _helper, _options);
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var served = server.ServeOnceAsync(lifetime.Token);
        return Task.FromResult<IEngineCredentialSession>(
            new Session(_options.HelperPath, operationId, served, lifetime, _handoverTimeout));
    }

    private sealed class Session : IEngineCredentialSession
    {
        private readonly Task _served;
        private readonly CancellationTokenSource _lifetime;
        private readonly TimeSpan _handoverTimeout;
        private readonly string _helperPath;

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
            _helperPath = helperPath;
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
                throw new CredentialHandoverException(
                    $"The password helper did not collect the secret within {_handoverTimeout.TotalSeconds:0} seconds. "
                    + $"The engine ran '{_helperPath}' as this process's own account; if that account cannot start it, "
                    + "or the run was blocked, the repository is never asked for.");
            }

            try
            {
                await _served;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // Named rather than unified: this is the handover failing, not a secret being wrong.
                throw new CredentialHandoverException(
                    $"The secret could not be handed to the engine: {error.Message}", error);
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
