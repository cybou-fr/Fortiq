using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Fortiq.Application;
using Fortiq.Desktop;
using Fortiq.Platform.Windows;

namespace Fortiq.Recovery.IntegrationTests;

/// <summary>
/// The boundaries a pilot machine actually has, exercised against a real installation.
/// </summary>
/// <remarks>
/// <see cref="PilotCoreWorkflowTests"/> proves the workflow on a hosted runner with no service, no
/// ACLs and a user-scoped key. Everything that separates that from a deployed machine lives here:
/// elevated installation, a service registered with the SCM, and the access control that stands
/// between an ordinary user and a process running as LocalSystem.
///
/// The lane needs an elevated session, so it skips where it cannot run. That skip is the dangerous
/// part: a test that quietly does nothing reports the same green as one that passed. CI must fail
/// when this lane skips — <c>scripts/Test-InstalledPilot.ps1</c> reads the result file and refuses a
/// run in which nothing executed.
///
/// What this lane still does not cover, and no automated lane can:
/// <list type="bullet">
///   <item>a reboot, and the service coming back by itself afterwards;</item>
///   <item>IPC refused to a genuinely unelevated caller — the runner is elevated, so the refusal is
///   covered by <c>ServiceIpcAuthorizationTests</c> against the policy rather than across a pipe;</item>
///   <item>a machine that has never had Fortiq on it, which only a fresh runner provides.</item>
/// </list>
/// </remarks>
[SupportedOSPlatform("windows")]
[Collection("installed-pilot")]
public sealed class InstalledWindowsPilotTests
{
    private const string ServiceName = "FortiqPilotTest";

    private static readonly System.Text.Json.JsonSerializerOptions ManifestJson = new() { WriteIndented = true };

    [SkippableFact]
    public async Task AnElevatedInstallDeploysTheBundleItVerified()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Installed-mode deployment is a Windows concern.");
        Skip.IfNot(WindowsPrivilegeChecker.IsElevated(), "Applying installation ACLs needs an elevated session.");

        using var workspace = await RecoveryWorkspace.CreateAsync("installed-pilot-install", CancellationToken.None);

        var bundle = BuildBundle(workspace);
        var target = workspace.EnsureDirectory("program-files");

        // ProvisionAcls stays false here, and the state directory is covered by the next test with an
        // explicit path. Going through InstallAsync would make it resolve the machine's state
        // directory from FORTIQ_STATE_DIRECTORY, and setting a process-wide variable in a test that
        // xUnit may run beside others is how one test quietly redirects another's paths.
        await InstallationManager.InstallAsync(new InstallOptions(
            target,
            InstallService: false,
            AddToPath: false,
            SourceDirectory: bundle,
            ProvisionAcls: false));

        // The bundle passed validation and its contents arrived. A deployment that verified a
        // manifest and then copied something else would satisfy every hash check and still install
        // the wrong binaries.
        Assert.True(File.Exists(Path.Combine(target, "Fortiq.Service.exe")));
        Assert.True(File.Exists(Path.Combine(target, "Fortiq.Desktop.exe")));
        Assert.True(File.Exists(Path.Combine(target, "Fortiq.PasswordHelper.exe")));
        Assert.Equal("the service", File.ReadAllText(Path.Combine(target, "Fortiq.Service.exe")));
    }

    [SkippableFact]
    public async Task StateDirectoryProvisioningClosesEveryPrivilegedDirectoryToOrdinaryUsers()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "NTFS ACL provisioning is a Windows concern.");
        Skip.IfNot(WindowsPrivilegeChecker.IsElevated(), "Setting these ACLs needs an elevated session.");

        using var workspace = await RecoveryWorkspace.CreateAsync("installed-pilot-acls", CancellationToken.None);

        // An explicit root, so nothing about this test depends on - or disturbs - the machine's own
        // state directory.
        var paths = FortiqStatePaths.Resolve(workspace.EnsureDirectory("state"));
        DirectoryAclProvisioner.ProvisionStateDirectoryAcls(paths);

        AssertUsersCannotWrite(
            Path.Combine(paths.Root, "schedules"),
            "A writable schedules directory hands any user a source path and a repository that " +
            "LocalSystem will act on, which is the boundary the IPC check holds.");

        AssertUsersCannotWrite(
            paths.AuditAnchors,
            "An anchor that whoever rewrites the receipts can also rewrite attests to nothing.");

        AssertUsersCannotWrite(
            Path.Combine(paths.Root, "credentials"),
            "Storage credentials must not be writable by every account on the machine.");

        AssertUsersCannotWrite(
            paths.Receipts,
            "Receipts are the evidence monitoring reads; a user who can edit them can manufacture proof.");
    }

    [SkippableFact]
    public void AServiceCanBeRegisteredAndRemovedAndItsSidComputed()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The service control manager is a Windows concern.");
        Skip.IfNot(WindowsPrivilegeChecker.IsElevated(), "Registering a service needs an elevated session.");

        // Registered under a name of its own, so a developer's real Fortiq installation is never
        // stopped or deleted by a test run.
        Cleanup();

        try
        {
            WindowsServiceController.CreateAndConfigureService(
                ServiceName,
                "Fortiq pilot test service",
                Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                "Registered by InstalledWindowsPilotTests; safe to delete.",
                WindowsServiceController.ServiceAutoStart,
                setServiceSidUnrestricted: true);

            var status = WindowsServiceController.QueryStatus(ServiceName);
            Assert.True(status.Exists, "The service manager did not report the service this test just created.");

            // The service SID is what the state directory ACLs grant to. If it cannot be computed the
            // installer's grants name a principal that does not exist, and the service would be locked
            // out of the directories it has to write.
            var sid = WindowsServiceController.ComputeServiceSid(ServiceName);
            Assert.StartsWith("S-1-5-80-", sid.Value, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup();
        }
    }

    private static void Cleanup()
    {
        try
        {
            var status = WindowsServiceController.QueryStatus(ServiceName);
            if (status.Running)
            {
                WindowsServiceController.StopService(ServiceName, TimeSpan.FromSeconds(10));
            }

            if (status.Exists)
            {
                WindowsServiceController.DeleteService(ServiceName);
            }
        }
        catch (InvalidOperationException)
        {
            // Nothing registered under that name, which is the state this wants to reach anyway.
        }
    }

    private static void AssertUsersCannotWrite(string directory, string because)
    {
        Assert.True(Directory.Exists(directory), $"'{directory}' was not created by the installer.");

        var authenticatedUsers = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
        var rules = new DirectoryInfo(directory).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier));

        // The individual write bits. Modify and FullControl are composite values that include the read
        // bits, so masking against them reports a plain read grant as a write.
        const FileSystemRights WriteBits =
            FileSystemRights.WriteData |
            FileSystemRights.AppendData |
            FileSystemRights.WriteExtendedAttributes |
            FileSystemRights.WriteAttributes |
            FileSystemRights.Delete |
            FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.ChangePermissions |
            FileSystemRights.TakeOwnership;

        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow ||
                !rule.IdentityReference.Equals(authenticatedUsers))
            {
                continue;
            }

            Assert.True(
                (rule.FileSystemRights & WriteBits) == 0,
                $"Authenticated Users may write '{directory}'. {because}");
        }
    }

    /// <summary>Writes a bundle the installer will accept, and returns its root.</summary>
    private static string BuildBundle(RecoveryWorkspace workspace)
    {
        var root = workspace.EnsureDirectory("bundle");

        var payload = new (string Path, string Content)[]
        {
            ("desktop/Fortiq.Desktop.exe", "the desktop"),
            ("desktop/Fortiq.PasswordHelper.exe", "the helper"),
            ("service/Fortiq.Service.exe", "the service"),
            ("recover/Fortiq.Recover.exe", "the recovery tool")
        };

        var files = new List<InstallationManager.BundleFileManifest>();
        foreach (var (relative, content) in payload)
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = System.Text.Encoding.UTF8.GetBytes(content);
            File.WriteAllBytes(path, bytes);
            files.Add(new(relative, bytes.Length, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))));
        }

        string HashOf(string relative) =>
            files.First(file => string.Equals(file.Path, relative, StringComparison.Ordinal)).Sha256;

        var manifest = new InstallationManager.BundleManifest(
            "fortiq.bundle-manifest",
            "1.0",
            "win-x64",
            "0.1.0-test",
            DateTimeOffset.UtcNow.ToString("O"),
            [
                new("desktop", "desktop", "desktop/Fortiq.Desktop.exe", true, HashOf("desktop/Fortiq.Desktop.exe")),
                new("service", "service", "service/Fortiq.Service.exe", true, HashOf("service/Fortiq.Service.exe")),
                new("recover", "recover", "recover/Fortiq.Recover.exe", true, HashOf("recover/Fortiq.Recover.exe")),
                new("passwordHelper", "desktop", "desktop/Fortiq.PasswordHelper.exe", true, HashOf("desktop/Fortiq.PasswordHelper.exe"))
            ],
            files);

        File.WriteAllText(
            Path.Combine(root, "bundle-manifest.json"),
            System.Text.Json.JsonSerializer.Serialize(manifest, ManifestJson));

        return root;
    }
}
