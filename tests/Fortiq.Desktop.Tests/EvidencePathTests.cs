using Fortiq.Application;

namespace Fortiq.Desktop.Tests;

/// <summary>
/// Where evidence lives. Every Fortiq process on a machine has to mean the same directory by it.
/// </summary>
/// <remarks>
/// These exist because of a real defect. The desktop and the Windows service each composed their own
/// receipt path and disagreed: the service wrote backup, check and drill receipts under
/// <c>work\receipts</c> while the desktop wrote restore receipts under <c>receipts</c>. Both then
/// published the same health report from different evidence, so a restore an operator had just
/// proven was absent from the report the service published on its next pass, and the verdict flipped
/// depending on which process wrote last. Nothing failed and nothing was logged.
/// </remarks>
public sealed class EvidencePathTests
{
    [Fact]
    public void EveryProcessResolvesTheSameEvidenceDirectory()
    {
        var service = FortiqStatePaths.Resolve(@"C:\ProgramData\Fortiq");
        var desktop = FortiqStatePaths.Resolve(@"C:\ProgramData\Fortiq\");

        Assert.Equal(service.Receipts, desktop.Receipts);
        Assert.Equal(service.HealthReport, desktop.HealthReport);
        Assert.Equal(service.Runs, desktop.Runs);
    }

    [Fact]
    public void ReceiptsLiveUnderTheWorkingDirectory()
    {
        var paths = FortiqStatePaths.Resolve(@"C:\ProgramData\Fortiq");

        // Not a preference: this is the path the service has always used, and the report is built
        // from whatever is here. Moving it would orphan every receipt already written.
        Assert.Equal(Path.Combine(paths.Working, "receipts"), paths.Receipts);
        Assert.StartsWith(paths.Root, paths.Receipts, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStateDirectoryIsMachineWideAndOverridable()
    {
        // A service running as one identity and a desktop running as another must arrive at the same
        // directory; a per-user default would hand them two.
        Assert.Equal(@"C:\somewhere\else", FortiqStatePaths.Resolve(@"C:\somewhere\else").Root);
        Assert.Contains("Fortiq", FortiqStatePaths.Resolve().Root, StringComparison.Ordinal);
    }

    [Fact]
    public void NoProcessInventsItsOwnEvidencePath()
    {
        var source = RepositoryRoot();
        var offenders = new List<string>();

        // Only composition roots are scanned, because that is where the defect was and where it can
        // recur: each process decided for itself what "receipts" meant. Inside the operations the
        // directory is derived from a working directory the caller supplies, which is a different
        // thing and not a place two processes can disagree.
        foreach (var file in Directory.EnumerateFiles(Path.Combine(source, "src"), "Program.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            // Comment lines are prose about the rule, not a breach of it.
            var code = File.ReadLines(file)
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal));

            if (code.Any(line =>
                line.Contains("\"receipts\"", StringComparison.Ordinal)
                || line.Contains("\"health.json\"", StringComparison.Ordinal)
                || line.Contains("\"fortiq.prom\"", StringComparison.Ordinal)))
            {
                offenders.Add(Path.GetRelativePath(source, file));
            }
        }

        Assert.True(
            offenders.Count == 0,
            "These composition roots spell out an evidence path instead of asking FortiqStatePaths: "
            + string.Join(", ", offenders));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Fortiq.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Fortiq.sln was not found above the test output.");
    }
}
