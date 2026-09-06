using Fortiq.Infrastructure.Runs;

namespace Fortiq.Recover;

public static class Program
{
    /// <summary>
    /// Runs the recovery tool, and leaves nothing on a machine that had no Fortiq.
    /// </summary>
    /// <remarks>
    /// This tool exists for a computer that has never had Fortiq installed - somebody else's, very
    /// possibly. It was letting the run registry fall back to its machine-wide default, which creates
    /// <c>%ProgramData%\Fortiq\runs</c> wherever it runs and never removes what it puts there. A tool
    /// whose entire claim is that it installs nothing should not be the exception to that.
    ///
    /// Where Fortiq is installed the machine-wide directory is still used, because run files are how
    /// concurrent Fortiq processes keep off one repository at a time and a private set would
    /// coordinate with nobody.
    /// </remarks>
    public static async Task<int> Main(string[] args)
    {
        var visiting = Path.Combine(Path.GetTempPath(), "fortiq-recover-" + Guid.NewGuid().ToString("N"));

        try
        {
            return await RecoveryCli.RunAsync(
                args,
                new RecoveryCommandExecutor(
                    storage: new Fortiq.Application.EnvironmentObjectStorageCredentialProvider(),
                    runDirectory: FortiqRunDirectory.ForVisitingTool(visiting)),
                new ConsoleRecoveryMaterialReader(Console.In, Console.Error),
                Console.Out,
                Console.Error,
                CancellationToken.None);
        }
        finally
        {
            // Only ever this run's own directory: when the machine has Fortiq, nothing was created
            // here and there is nothing to remove. A cleanup that cannot happen is not worth reporting
            // over the result of a recovery.
            try
            {
                if (Directory.Exists(visiting))
                {
                    Directory.Delete(visiting, recursive: true);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
