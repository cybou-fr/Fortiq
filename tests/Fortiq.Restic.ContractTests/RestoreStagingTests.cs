using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Fortiq.Application;
using Fortiq.Infrastructure.Restic;

namespace Fortiq.Restic.ContractTests;

/// <summary>
/// The staging area a restore writes into: private, unpredictable, never reused, validated at the
/// place the caller will actually read from, and cleaned up without reaching outside itself.
/// </summary>
public sealed class RestoreStagingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fortiq-staging-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void StagingDirectoriesAreUnpredictableAndNeverTheSameTwice()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            using var staging = RestoreStagingArea.Create(Target($"target-{attempt}"));
            Assert.True(names.Add(Path.GetFileName(staging.Path)));
            Assert.StartsWith(".fortiq-restore-", Path.GetFileName(staging.Path), StringComparison.Ordinal);

            // Long enough that guessing it is not a strategy, and unrelated to any operation ID that
            // travels through receipts and command lines.
            Assert.Equal(".fortiq-restore-".Length + 32, Path.GetFileName(staging.Path).Length);
        }
    }

    [SupportedOSPlatform("windows")]
    [SkippableFact]
    public void TheStagingDirectoryAdmitsOnlyTheAccountFortiqRunsAs()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Access control lists are checked on Windows.");
        using var staging = RestoreStagingArea.Create(Target("acl"));

        var security = new DirectoryInfo(staging.Path).GetAccessControl();
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));

        Assert.True(security.AreAccessRulesProtected, "The staging directory inherited its parent's access rules.");
        using var identity = WindowsIdentity.GetCurrent();
        var rule = Assert.Single(rules.Cast<FileSystemAccessRule>());
        Assert.Equal(identity.User, rule.IdentityReference);
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
    }

    [Fact]
    public void AValidatedTreeIsPromotedIntoTheTarget()
    {
        var target = Target("promote");
        using var staging = RestoreStagingArea.Create(target);
        File.WriteAllText(Path.Combine(staging.Path, "restored.txt"), "content");
        Directory.CreateDirectory(Path.Combine(staging.Path, "nested"));
        File.WriteAllText(Path.Combine(staging.Path, "nested", "deep.txt"), "more");

        staging.Promote();

        Assert.Equal("content", File.ReadAllText(Path.Combine(target, "restored.txt")));
        Assert.Equal("more", File.ReadAllText(Path.Combine(target, "nested", "deep.txt")));
        Assert.False(Directory.Exists(staging.Path), "The staging directory outlived the promotion.");
    }

    [SkippableFact]
    public void ATreeWithAReparsePointIsRefusedAndTheTargetIsLeftWithNothing()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "untouched.txt"), "outside");

        var target = Target("rejected");
        using var staging = RestoreStagingArea.Create(target);
        File.WriteAllText(Path.Combine(staging.Path, "restored.txt"), "content");
        Skip.IfNot(TryCreateJunction(Path.Combine(staging.Path, "escape"), outside), "Creating a junction is not permitted here.");

        Assert.Throws<RestoreRejectedException>(staging.Promote);

        // Neither a partial tree at the target nor a touched file outside it.
        Assert.False(Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any());
        Assert.Equal("outside", File.ReadAllText(Path.Combine(outside, "untouched.txt")));
    }

    [Fact]
    public void ValidationRunsAtTheLocationTheCallerWillRead()
    {
        var target = Target("post-validation");
        using var staging = RestoreStagingArea.Create(target);
        File.WriteAllText(Path.Combine(staging.Path, "restored.txt"), "content");
        staging.Promote();

        // The same rule that guarded the staging directory holds for the promoted tree, which is the
        // one the caller actually reads.
        RestoreStagingArea.Validate(target);
    }

    [SkippableFact]
    public void CleanupUnlinksAReparsePointInsteadOfWalkingThroughIt()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside-cleanup")).FullName;
        File.WriteAllText(Path.Combine(outside, "untouched.txt"), "outside");

        string stagingPath;
        using (var staging = RestoreStagingArea.Create(Target("cleanup")))
        {
            stagingPath = staging.Path;
            Skip.IfNot(TryCreateJunction(Path.Combine(staging.Path, "escape"), outside), "Creating a junction is not permitted here.");

            var readOnly = Path.Combine(staging.Path, "read-only.txt");
            File.WriteAllText(readOnly, "content");
            new FileInfo(readOnly).IsReadOnly = true;
        }

        Assert.False(Directory.Exists(stagingPath), "A staging directory that was never promoted survived.");
        Assert.Equal("outside", File.ReadAllText(Path.Combine(outside, "untouched.txt")));
        Assert.True(Directory.Exists(outside), "Cleanup followed the junction out of the staging directory.");
    }

    [Fact]
    public void ATargetThatAlreadyHasContentIsRefusedBeforeAnythingIsStaged()
    {
        var target = Target("occupied");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "existing.txt"), "existing");

        Assert.Throws<RestoreRejectedException>(() => RestoreStagingArea.Create(target));
        Assert.Empty(Directory.EnumerateDirectories(_root, ".fortiq-restore-*"));
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories))
        {
            if (new DirectoryInfo(directory).LinkTarget is not null)
            {
                Directory.Delete(directory);
            }
        }

        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            if (info.IsReadOnly)
            {
                info.IsReadOnly = false;
            }
        }

        Directory.Delete(_root, recursive: true);
    }

    private string Target(string name)
    {
        Directory.CreateDirectory(_root);
        return Path.Combine(_root, name);
    }

    private static bool TryCreateJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "/c", "mklink", "/J", link, target }
        });

        if (process is null)
        {
            return false;
        }

        process.WaitForExit();
        return process.ExitCode == 0 && Directory.Exists(link);
    }
}
