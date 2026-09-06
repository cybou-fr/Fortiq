namespace Fortiq.Infrastructure.Runs;

/// <summary>
/// Where run records live. They are coordination between concurrent Fortiq processes on one machine,
/// never something a recovery depends on: a machine with no Fortiq state simply starts an empty one.
/// </summary>
public static class FortiqRunDirectory
{
    /// <summary>
    /// The machine-wide location when it is writable - a service and a tool started by hand have to
    /// see the same runs - and the per-user one otherwise, which is the case for an unelevated tool.
    /// </summary>
    public static string Default()
    {
        var machineWide = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Fortiq",
            "runs");

        try
        {
            Directory.CreateDirectory(machineWide);
            return machineWide;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            var perUser = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Fortiq",
                "runs");

            Directory.CreateDirectory(perUser);
            return perUser;
        }
    }

    /// <summary>
    /// Where a tool that is only visiting should keep its runs.
    /// </summary>
    /// <param name="temporaryRoot">
    /// A directory the caller owns and will delete. Used when this machine has no Fortiq state.
    /// </param>
    /// <remarks>
    /// <see cref="Fortiq.Recover"/> exists for a machine that has never had Fortiq on it - somebody
    /// else's computer, borrowed on the worst day of the year - and it was calling <see cref="Default"/>,
    /// which creates a machine-wide runs directory under %ProgramData% wherever it is run. A tool whose
    /// whole claim is that it installs nothing was making a machine-wide directory on a stranger's PC,
    /// and leaving it there: nothing ever removes a run file. On the machine this was found on there
    /// were 367 of them for zero protected sources.
    ///
    /// Where Fortiq <em>is</em> installed the machine-wide directory is still the right answer, and for
    /// a real reason rather than tidiness: run files are how concurrent Fortiq processes avoid working
    /// on one repository at once, and a visiting tool that kept its own private set would coordinate
    /// with nobody. So the rule is to join the machine's runs when they already exist, and to leave
    /// nothing behind when they do not.
    /// </remarks>
    public static string ForVisitingTool(string temporaryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryRoot);

        var machineWide = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Fortiq",
            "runs");

        // Existence, not writability: a machine with Fortiq installed has this directory, and whether
        // this particular account may write to it is the registry's problem to report rather than a
        // reason to go and create state somewhere else.
        if (Directory.Exists(machineWide))
        {
            return machineWide;
        }

        var visiting = Path.Combine(Path.GetFullPath(temporaryRoot), "runs");
        Directory.CreateDirectory(visiting);
        return visiting;
    }
}
