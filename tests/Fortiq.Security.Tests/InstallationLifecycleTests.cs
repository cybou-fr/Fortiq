using System.Security.Cryptography;
using System.Text;
using Fortiq.Desktop;

namespace Fortiq.Security.Tests;

/// <summary>
/// What installing, upgrading and removing are allowed to claim.
/// </summary>
public sealed class InstallationLifecycleTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "fortiq-install-tests", Guid.NewGuid().ToString("N"));

    private string Bundle => Path.Combine(_root, "bundle");
    private string Target => Path.Combine(_root, "target");

    public InstallationLifecycleTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task AnInstallThatCopiedEverythingSucceeds()
    {
        WriteBundle();

        await InstallationManager.InstallAsync(new InstallOptions(
            Target, InstallService: false, AddToPath: false, SourceDirectory: Bundle,
            ProvisionAcls: false, AutoStartOnLogon: false, CreateStartMenuShortcut: false));

        Assert.True(File.Exists(Path.Combine(Target, "Fortiq.Desktop.exe")));
        Assert.True(File.Exists(Path.Combine(Target, "Fortiq.Operations.dll")));
    }

    [Fact]
    public async Task TheRecoveryGuideIsInstalledBesideTheApplication()
    {
        // It is needed on the machine that has backups, not in the download folder that was deleted.
        WriteBundle();

        await InstallationManager.InstallAsync(new InstallOptions(
            Target, InstallService: false, AddToPath: false, SourceDirectory: Bundle,
            ProvisionAcls: false, AutoStartOnLogon: false, CreateStartMenuShortcut: false));

        Assert.True(File.Exists(Path.Combine(Target, "RECOVERY-GUIDE.md")));
        Assert.True(File.Exists(Path.Combine(Target, "LICENSE")));
    }

    [Fact]
    public async Task AnUpgradeReplacesTheBinariesAndLeavesEverythingElseAlone()
    {
        WriteBundle();
        await InstallationManager.InstallAsync(Options());

        // Something the person put there, and the state an upgrade must not touch.
        var stray = Path.Combine(Target, "my-notes.txt");
        File.WriteAllText(stray, "still mine");

        WriteBundle(desktopContent: "the second version");
        await InstallationManager.InstallAsync(Options());

        Assert.Equal("the second version", File.ReadAllText(Path.Combine(Target, "Fortiq.Desktop.exe")));
        Assert.Equal("still mine", File.ReadAllText(stray));
    }

    [Fact]
    public async Task AnIncompleteBundleIsRefusedBeforeAnythingIsCopied()
    {
        WriteBundle();
        File.Delete(Path.Combine(Bundle, "service", "Fortiq.Operations.dll"));

        var error = await Assert.ThrowsAnyAsync<Exception>(() => InstallationManager.InstallAsync(Options()));

        Assert.True(error is FileNotFoundException or InvalidDataException, error.GetType().Name);
        Assert.False(File.Exists(Path.Combine(Target, "Fortiq.Desktop.exe")),
            "a refused bundle must not leave half an installation behind");
    }

    [Fact]
    public async Task RemovingSomethingThatIsNotAnInstallationIsRefused()
    {
        var innocent = Path.Combine(_root, "someones-documents");
        Directory.CreateDirectory(innocent);
        File.WriteAllText(Path.Combine(innocent, "thesis.docx"), "years of work");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => InstallationManager.UninstallAsync(new UninstallOptions(TargetDirectory: innocent)));

        Assert.Contains("does not look like a Fortiq installation", error.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(innocent, "thesis.docx")));
    }

    private InstallOptions Options() => new(
        Target, InstallService: false, AddToPath: false, SourceDirectory: Bundle,
        ProvisionAcls: false, AutoStartOnLogon: false, CreateStartMenuShortcut: false);

    private void WriteBundle(string desktopContent = "the desktop")
    {
        if (Directory.Exists(Bundle))
        {
            Directory.Delete(Bundle, recursive: true);
        }

        var payload = new (string Path, string Content)[]
        {
            ("desktop/Fortiq.Desktop.exe", desktopContent),
            ("desktop/Fortiq.PasswordHelper.exe", "the helper"),
            ("service/Fortiq.Service.exe", "the service"),
            ("service/Fortiq.Operations.dll", "the library the service loads"),
            ("recover/Fortiq.Recover.exe", "the recovery tool"),
            ("RECOVERY-GUIDE.md", "how to get your files back"),
            ("LICENSE", "Apache License 2.0"),
            ("SECURITY.md", "how to report a problem")
        };

        var entries = new List<string>();
        foreach (var (relative, content) in payload)
        {
            var path = Path.Combine(Bundle, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = Encoding.UTF8.GetBytes(content);
            File.WriteAllBytes(path, bytes);
            entries.Add($$"""
                {"path":"{{relative}}","length":{{bytes.Length}},"sha256":"{{Convert.ToHexStringLower(SHA256.HashData(bytes))}}"}
                """);
        }

        string Hash(string relative) => Convert.ToHexStringLower(SHA256.HashData(
            File.ReadAllBytes(Path.Combine(Bundle, relative.Replace('/', Path.DirectorySeparatorChar)))));

        File.WriteAllText(Path.Combine(Bundle, "bundle-manifest.json"), $$"""
        {
          "schema": "fortiq.bundle-manifest",
          "version": "1.0",
          "runtime": "win-x64",
          "productVersion": "0.1.0-test",
          "components": [
            {"name":"desktop","folder":"desktop","mainExecutable":"desktop/Fortiq.Desktop.exe","required":true,"sha256":"{{Hash("desktop/Fortiq.Desktop.exe")}}"},
            {"name":"service","folder":"service","mainExecutable":"service/Fortiq.Service.exe","required":true,"sha256":"{{Hash("service/Fortiq.Service.exe")}}"},
            {"name":"recover","folder":"recover","mainExecutable":"recover/Fortiq.Recover.exe","required":true,"sha256":"{{Hash("recover/Fortiq.Recover.exe")}}"},
            {"name":"passwordHelper","folder":"desktop","mainExecutable":"desktop/Fortiq.PasswordHelper.exe","required":true,"sha256":"{{Hash("desktop/Fortiq.PasswordHelper.exe")}}"}
          ],
          "files": [{{string.Join(",", entries)}}]
        }
        """);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }
    }
}
