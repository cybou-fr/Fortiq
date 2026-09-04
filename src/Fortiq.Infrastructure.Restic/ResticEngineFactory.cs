using Fortiq.Application;

namespace Fortiq.Infrastructure.Restic;

/// <summary>
/// Composes the restic adapter. The adapter itself stays internal so no caller can construct one
/// around an unverified binary or bypass the credential port.
/// </summary>
public static class ResticEngineFactory
{
    public static IRepositoryEngine Create(
        VerifiedEngine engine,
        IEngineCredentialProvider credentials,
        string workingDirectory) =>
        new ResticRepositoryEngine(engine, new ResticProcessRunner(), credentials, workingDirectory);
}
