namespace Fortiq.Application;

/// <summary>
/// Where a Fortiq machine keeps its state. One place decides these paths, and every process that
/// runs on the machine asks it rather than composing its own.
/// </summary>
/// <remarks>
/// This type exists because of a real defect rather than a preference for tidiness. The desktop and
/// the Windows service each built their own receipt path, and the two disagreed: the service wrote
/// backup, check and drill receipts under <c>work\receipts</c> while the desktop wrote its restore
/// receipts under <c>receipts</c>. Both then published the same <c>health.json</c> from different
/// evidence, so a restore the operator had just proven disappeared from the report on the service's
/// next pass, and the verdict flipped back and forth depending on which process wrote last.
/// <para>
/// Evidence only works if every process means the same directory by it. Composing these paths by
/// hand made that a matter of remembering, which is why it is no longer possible to do.
/// </para>
/// </remarks>
public sealed class FortiqStatePaths
{
    private FortiqStatePaths(string root) => Root = root;

    /// <summary>The state directory for this machine.</summary>
    public string Root { get; }

    /// <summary>
    /// The root a schedule store is opened on. It holds the schedules a person edits and the state
    /// Fortiq writes, in separate directories underneath.
    /// </summary>
    public string Schedules => Root;

    /// <summary>Scratch space for engine runs. Contents are disposable; nothing here is evidence.</summary>
    public string Working => Path.Combine(Root, "work");

    /// <summary>
    /// Operation receipts: what actually happened, and the only thing monitoring reads to decide
    /// whether a repository is recoverable. Every process writes here.
    /// </summary>
    public string Receipts => Path.Combine(Working, "receipts");

    /// <summary>The run registry, which keeps two processes off the same repository at once.</summary>
    public string Runs => Path.Combine(Root, "runs");

    /// <summary>The published health report, read by the desktop and by monitoring alike.</summary>
    public string HealthReport => Path.Combine(Root, "health", "health.json");

    /// <summary>The same report in the Prometheus textfile format.</summary>
    public string HealthMetrics => Path.Combine(Root, "health", "fortiq.prom");

    /// <summary>
    /// Resolves the state directory: the <c>FORTIQ_STATE_DIRECTORY</c> environment variable when it
    /// is set, and otherwise <c>%ProgramData%\Fortiq</c>.
    /// </summary>
    /// <remarks>
    /// Machine-wide by default, not per-user. A service running as one identity and a desktop
    /// running as another have to arrive at the same directory, and a per-user default would give
    /// them two.
    /// </remarks>
    public static FortiqStatePaths Resolve(string? explicitRoot = null)
    {
        var root = explicitRoot
            ?? Environment.GetEnvironmentVariable("FORTIQ_STATE_DIRECTORY")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Fortiq");

        return new FortiqStatePaths(Path.GetFullPath(root));
    }
}
