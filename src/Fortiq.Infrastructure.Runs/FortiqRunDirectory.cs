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
}
