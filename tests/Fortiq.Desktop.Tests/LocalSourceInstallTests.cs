using Fortiq.Desktop;

namespace Fortiq.Desktop.Tests;

/// <summary>
/// Installing from a plain folder - a source carrying no deployment manifest.
/// </summary>
/// <remarks>
/// This path copied every file, swallowed every <see cref="IOException"/> one at a time, and checked
/// nothing afterwards: the completeness verification belongs to the bundle path and is never reached
/// from here. A file locked by a running process, or a disk that filled halfway through, produced an
/// installation missing a library and an installer that reported success - and what failed next was
/// the service failing to start, with nothing pointing back at the install that caused it.
/// </remarks>
public sealed class LocalSourceInstallTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fortiq-local-install-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AFolderOfBinariesArrivesWhole()
    {
        var source = Source();
        await File.WriteAllTextAsync(Path.Combine(source, "Fortiq.Desktop.exe"), "the desktop");
        await File.WriteAllTextAsync(Path.Combine(source, "Fortiq.Service.exe"), "the service");
        await File.WriteAllTextAsync(Path.Combine(source, "notes.txt"), "not a binary, not copied");

        var target = Target();
        await InstallationManager.InstallAsync(new InstallOptions(
            target,
            InstallService: false,
            AddToPath: false,
            SourceDirectory: source,
            ProvisionAcls: false));

        Assert.Equal("the desktop", await File.ReadAllTextAsync(Path.Combine(target, "Fortiq.Desktop.exe")));
        Assert.Equal("the service", await File.ReadAllTextAsync(Path.Combine(target, "Fortiq.Service.exe")));
    }

    [Fact]
    public async Task AFileThatCouldNotBeCopiedFailsTheInstallRatherThanBeingLeftOut()
    {
        var source = Source();
        await File.WriteAllTextAsync(Path.Combine(source, "Fortiq.Desktop.exe"), "the desktop");
        var locked = Path.Combine(source, "Fortiq.Service.exe");
        await File.WriteAllTextAsync(locked, "the service");

        var target = Target();

        // A file already at the destination and held open is what a running Fortiq looks like to an
        // installer, and it is the case that produced a silently incomplete installation.
        await File.WriteAllTextAsync(Path.Combine(target, "Fortiq.Service.exe"), "the old service");
        using var hold = new FileStream(
            Path.Combine(target, "Fortiq.Service.exe"),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => InstallationManager.InstallAsync(new InstallOptions(
            target,
            InstallService: false,
            AddToPath: false,
            SourceDirectory: source,
            ProvisionAcls: false)));

        Assert.Contains("incomplete", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Fortiq.Service.exe", failure.Message, StringComparison.Ordinal);
        // And it says what to do, because "incomplete" on its own is not something anybody can act on.
        Assert.Contains("install again", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnInstallationShortOfAFileIsRefusedEvenWhenNothingThrew()
    {
        // The check reads the destination rather than trusting that a copy which did not throw wrote
        // everything. Here the destination file is replaced with a shorter one after the copy would
        // have run, which is the shape a disk filling up leaves behind.
        var source = Source();
        await File.WriteAllTextAsync(Path.Combine(source, "Fortiq.Desktop.exe"), "the desktop, at full length");

        var target = Target();
        await InstallationManager.InstallAsync(new InstallOptions(
            target,
            InstallService: false,
            AddToPath: false,
            SourceDirectory: source,
            ProvisionAcls: false));

        await File.WriteAllTextAsync(Path.Combine(target, "Fortiq.Desktop.exe"), "short");
        using var hold = new FileStream(
            Path.Combine(target, "Fortiq.Desktop.exe"),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => InstallationManager.InstallAsync(new InstallOptions(
            target,
            InstallService: false,
            AddToPath: false,
            SourceDirectory: source,
            ProvisionAcls: false)));

        Assert.Contains("Fortiq.Desktop.exe", failure.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // A handle this test held open may outlive it by a moment; the temporary directory is
                // not worth failing a passing test over.
            }
        }
    }

    private string Source()
    {
        var path = Path.Combine(_root, "source");
        Directory.CreateDirectory(path);
        return path;
    }

    private string Target()
    {
        var path = Path.Combine(_root, "target");
        Directory.CreateDirectory(path);
        return path;
    }
}
