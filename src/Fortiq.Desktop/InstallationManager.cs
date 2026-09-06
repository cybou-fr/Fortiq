using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Fortiq.Application;
using Fortiq.Desktop.ViewModels;
using Fortiq.Platform.Windows;

namespace Fortiq.Desktop;

public sealed record InstallOptions(
    string TargetDirectory,
    bool InstallService = true,
    bool AddToPath = true,
    string? SourceDirectory = null,
    bool ProvisionAcls = true,
    bool AutoStartOnLogon = true,
    bool CreateStartMenuShortcut = true);

public sealed record UninstallOptions(
    bool PurgeData = false,
    string? TargetDirectory = null);

public sealed record InstallProgressReport(string Message, double Percent);

/// <summary>
/// Orchestrates application installation, file copying, Windows service provisioning,
/// and ACL enforcement according to Spec 21 and ADR-014.
/// </summary>
public sealed class InstallationManager : IInstallationOperations
{
    private static readonly string[] BinaryExtensions =
    {
        ".exe", ".dll", ".json", ".deps.json", ".runtimeconfig.json", ".ico", ".png", ".pdb"
    };

    public static string DefaultInstallPath => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Fortiq")
        : Path.Combine(AppContext.BaseDirectory, "installed");

    public async Task<int> ExecuteInstallAsync(
        string targetDir,
        bool installService,
        bool addToPath,
        bool autoStartOnLogon,
        IProgress<(string Message, double Percent)> progress,
        CancellationToken cancellationToken)
    {
        var options = new InstallOptions(targetDir, installService, addToPath, AutoStartOnLogon: autoStartOnLogon);

        // If running as Administrator, execute installation directly in-process
        if (!OperatingSystem.IsWindows() || WindowsPrivilegeChecker.IsElevated())
        {
            var internalProgress = new Progress<InstallProgressReport>(p => progress.Report((p.Message, p.Percent)));
            await InstallAsync(options, internalProgress, cancellationToken);
            return 0;
        }

        // Standard user token: prompt for UAC elevation
        progress.Report(("Prompting for administrative elevation...", 15));
        var json = JsonSerializer.Serialize(options);
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        var workerArgs = $"--worker-install {base64}";

        return await ElevateAndExecuteAsync(workerArgs, cancellationToken);
    }

    public static async Task InstallAsync(
        InstallOptions options,
        IProgress<InstallProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var targetDir = Path.GetFullPath(options.TargetDirectory);
        var sourceDir = Path.GetFullPath(options.SourceDirectory ?? AppContext.BaseDirectory);

        progress?.Report(new("Preparing installation directories...", 10));
        Directory.CreateDirectory(targetDir);

        // Before a single file is copied. The service holds its own executable open while it runs, so
        // installing over an existing installation failed on the copy - the first install worked
        // because nothing was there, and every upgrade after it did not. Stopping first is also the
        // only way the replacement is atomic from the service's point of view: it never sees half of
        // one version and half of another.
        var serviceWasRunning = StopServiceForReplacement(targetDir, progress);

        var (bundleRoot, manifest) = DiscoverManifest(sourceDir);
        if (manifest is not null && bundleRoot is not null)
        {
            progress?.Report(new("Verifying and copying deployment bundle components...", 25));
            CopyBundle(bundleRoot, manifest, targetDir);
        }
        else
        {
            progress?.Report(new("Copying application binaries...", 25));
            CopyLocalSource(sourceDir, targetDir);
        }

        if (OperatingSystem.IsWindows() && options.ProvisionAcls)
        {
            progress?.Report(new("Configuring secure directory ACLs...", 60));
            try
            {
                DirectoryAclProvisioner.ProvisionProgramFilesAcls(targetDir);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Security provisioning failed: could not set Program Files ACLs on '{targetDir}': {ex.Message}", ex);
            }

            var statePaths = FortiqStatePaths.Resolve();
            try
            {
                DirectoryAclProvisioner.ProvisionStateDirectoryAcls(statePaths);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Security provisioning failed: could not set state directory ACLs on '{statePaths.Root}': {ex.Message}", ex);
            }

            // Created here because creating a local group needs administrative rights, and this is the
            // one moment Fortiq has them. It is created empty: nobody may run anything through it
            // until an administrator puts an account in, which is what makes it a delegation rather
            // than a widening. A machine that refuses it is left exactly as it was - administrators
            // only - so the installation carries on and says so rather than failing.
            progress?.Report(new($"Creating the '{FortiqOperatorsGroup.Name}' group...", 70));
            if (!FortiqOperatorsGroup.TryCreate(out var groupFailure) && groupFailure is not null)
            {
                progress?.Report(new(groupFailure, 70));
            }

            if (options.InstallService)
            {
                progress?.Report(new("Registering Windows Service 'Fortiq'...", 75));
                var serviceExePath = Path.Combine(targetDir, "Fortiq.Service.exe");
                if (!File.Exists(serviceExePath))
                {
                    throw new FileNotFoundException($"Cannot install Windows Service: '{serviceExePath}' is missing from target directory. Deployment is incomplete.");
                }

                try
                {
                    var status = WindowsServiceController.QueryStatus(WindowsServiceController.DefaultServiceName);
                    if (status.Running)
                    {
                        WindowsServiceController.StopService(WindowsServiceController.DefaultServiceName, TimeSpan.FromSeconds(5));
                    }

                    if (status.Exists)
                    {
                        WindowsServiceController.DeleteService(WindowsServiceController.DefaultServiceName);
                        Thread.Sleep(500); // Give SCM time to finalize deletion
                    }

                    WindowsServiceController.CreateAndConfigureService(
                        WindowsServiceController.DefaultServiceName,
                        WindowsServiceController.DefaultDisplayName,
                        serviceExePath,
                        WindowsServiceController.DefaultDescription,
                        WindowsServiceController.ServiceAutoStart,
                        setServiceSidUnrestricted: true);

                    progress?.Report(new("Starting Windows Service 'Fortiq'...", 85));
                    WindowsServiceController.StartService(WindowsServiceController.DefaultServiceName, TimeSpan.FromSeconds(10));
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Failed to register or start Windows Service: {ex.Message}", ex);
                }
            }

            if (options.AddToPath)
            {
                progress?.Report(new("Configuring system PATH...", 95));
                TryAddDirectoryToPath(targetDir);
            }

            if (options.AutoStartOnLogon)
            {
                progress?.Report(new("Configuring Windows autostart on logon...", 98));
                var installedDesktopExe = Path.Combine(targetDir, "Fortiq.Desktop.exe");
                WindowsAutostartController.SetAutostartEnabled(true, installedDesktopExe);
            }

            if (options.CreateStartMenuShortcut)
            {
                progress?.Report(new("Adding Fortiq to the Start menu...", 99));
                try
                {
                    StartMenuShortcut.Create(Path.Combine(targetDir, "Fortiq.Desktop.exe"));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or COMException or FileNotFoundException)
                {
                    // A missing shortcut is an inconvenience; a failed installation over it would be
                    // worse. The service is registered and the files are in place either way.
                    progress?.Report(new(
                        $"Installed, but the Start menu entry could not be created ({error.Message}). " +
                        $"Fortiq is at {Path.Combine(targetDir, "Fortiq.Desktop.exe")}.", 99));
                }
            }
        }

        // Put back what was stopped. When InstallService was asked for, the block above has already
        // registered and started it; this covers the upgrade that keeps the existing registration.
        if (serviceWasRunning && OperatingSystem.IsWindows() && !options.InstallService)
        {
            progress?.Report(new("Starting the Fortiq service...", 95));
            WindowsServiceController.StartService(WindowsServiceController.DefaultServiceName, TimeSpan.FromSeconds(30));
        }

        progress?.Report(new("Installation completed successfully.", 100));
        await Task.CompletedTask;
    }

    /// <summary>
    /// Stops the Fortiq service if it is running, so its files can be replaced. Returns whether it
    /// was running, which is what decides if it should be started again afterwards.
    /// </summary>
    /// <param name="targetDir">Where files are about to be written.</param>
    /// <remarks>
    /// The service is stopped only when the files being replaced are its own. Asking "is the machine's
    /// Fortiq service running" and stopping it regardless made an install into a temporary directory
    /// reach out and stop the real one - which is what a test installing to a temp path immediately
    /// did. The reason to stop it is that its binary is about to be overwritten, so that is the
    /// condition.
    /// </remarks>
    private static bool StopServiceForReplacement(string targetDir, IProgress<InstallProgressReport>? progress)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var status = WindowsServiceController.QueryStatus(WindowsServiceController.DefaultServiceName);
            if (!status.Exists || !status.Running)
            {
                return false;
            }

            var binary = status.BinaryPath?.Trim('"');
            if (string.IsNullOrWhiteSpace(binary))
            {
                return false;
            }

            var boundary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDir)) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(binary).StartsWith(boundary, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            progress?.Report(new("Stopping the Fortiq service so its files can be replaced...", 15));
            WindowsServiceController.StopService(WindowsServiceController.DefaultServiceName, TimeSpan.FromSeconds(30));
            return true;
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Reported rather than swallowed: if the service could not be stopped the copy below will
            // fail, and the operator needs the first reason rather than the second.
            throw new InvalidOperationException(
                "The Fortiq service is running and could not be stopped, so its files cannot be replaced. " +
                $"Stop the 'Fortiq' service and run the installer again. ({error.Message})",
                error);
        }
    }

    /// <summary>
    /// Removes the service, the files, the Start menu entry and the PATH entry.
    /// </summary>
    /// <remarks>
    /// Every step here used to write its failure to <c>Debug.WriteLine</c> - which goes nowhere in a
    /// release build - and then report "Uninstallation complete. 100%". A registered service and a
    /// full Program Files directory could survive an uninstall that told the person it had worked,
    /// and the next thing they would do is install a new version on top of a service they believed
    /// was gone. Failures are collected and raised at the end, naming what is still on the machine.
    /// </remarks>
    public static async Task UninstallAsync(
        UninstallOptions options,
        IProgress<InstallProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var targetDir = Path.GetFullPath(options.TargetDirectory ?? DefaultInstallPath);

        // A recursive delete of whatever arrives in TargetDirectory is too much power to hand a
        // string. The installer only ever puts Fortiq in a directory it created and named.
        if (!LooksLikeAnInstallation(targetDir))
        {
            throw new InvalidOperationException(
                $"'{targetDir}' does not look like a Fortiq installation - it holds no Fortiq.Desktop.exe. " +
                "Refusing to delete it.");
        }

        var leftovers = new List<string>();

        if (OperatingSystem.IsWindows())
        {
            WindowsAutostartController.SetAutostartEnabled(false);

            progress?.Report(new("Stopping and removing the Fortiq service...", 20));
            try
            {
                var status = WindowsServiceController.QueryStatus(WindowsServiceController.DefaultServiceName);
                if (status.Exists)
                {
                    if (status.Running)
                    {
                        WindowsServiceController.StopService(WindowsServiceController.DefaultServiceName, TimeSpan.FromSeconds(30));
                    }

                    WindowsServiceController.DeleteService(WindowsServiceController.DefaultServiceName);
                }
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                leftovers.Add($"the Windows service 'Fortiq' is still registered ({error.Message})");
            }

            progress?.Report(new("Removing the Start menu entry...", 35));
            if (!StartMenuShortcut.Remove())
            {
                leftovers.Add($"the Start menu entry at {StartMenuShortcut.DefaultPath}");
            }

            progress?.Report(new("Removing the directory from PATH...", 45));
            TryRemoveDirectoryFromPath(targetDir);
        }

        progress?.Report(new("Removing program files...", 60));
        try
        {
            if (Directory.Exists(targetDir))
            {
                Directory.Delete(targetDir, recursive: true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            leftovers.Add($"the program files in {targetDir} ({error.Message})");
        }

        if (options.PurgeData)
        {
            progress?.Report(new("Removing state and evidence...", 80));
            var statePaths = FortiqStatePaths.Resolve();
            try
            {
                if (Directory.Exists(statePaths.Root))
                {
                    Directory.Delete(statePaths.Root, recursive: true);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                leftovers.Add($"the state directory {statePaths.Root} ({error.Message})");
            }
        }

        if (leftovers.Count > 0)
        {
            throw new InvalidOperationException(
                "Fortiq was only partly removed. Still on this machine: " + string.Join("; ", leftovers) +
                ". Close any running Fortiq window and try again.");
        }

        progress?.Report(new("Fortiq has been removed.", 100));
        await Task.CompletedTask;
    }

    /// <summary>Whether a directory holds an installation rather than something else entirely.</summary>
    private static bool LooksLikeAnInstallation(string targetDir) =>
        Directory.Exists(targetDir) && File.Exists(Path.Combine(targetDir, "Fortiq.Desktop.exe"));

    public static async Task<int> ElevateAndExecuteAsync(string arguments, CancellationToken cancellationToken = default)
    {
        var exePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Fortiq.Desktop.exe");

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = arguments,
            Verb = "runas",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal
        };

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return 66; // UAC elevation rejected
            }

            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED: user clicked 'No' or dismissed the UAC elevation prompt
            return 66;
        }
    }

    public sealed record BundleComponentManifest(
        string Name,
        string Folder,
        string MainExecutable,
        bool Required,
        string Sha256);

    /// <summary>One file the bundle installs, and exactly what it must be.</summary>
    public sealed record BundleFileManifest(string Path, long Length, string Sha256);

    public sealed record BundleManifest(
        string Schema,
        string Version,
        string Rid,
        /// <summary>Which Fortiq this bundle holds, as opposed to which manifest format it uses.</summary>
        string? ProductVersion,
        string? Created,
        IReadOnlyList<BundleComponentManifest> Components,
        IReadOnlyList<BundleFileManifest>? Files = null);

    public static (string? BundleRoot, BundleManifest? Manifest) DiscoverManifest(string sourceDir)
    {
        var directPath = Path.Combine(sourceDir, "bundle-manifest.json");
        if (File.Exists(directPath))
        {
            var manifest = LoadManifest(directPath);
            if (manifest is not null)
            {
                if (AreComponentsInDirectory(sourceDir, manifest))
                {
                    return (sourceDir, manifest);
                }

                var parent = Directory.GetParent(sourceDir);
                if (parent is not null && AreComponentsInDirectory(parent.FullName, manifest))
                {
                    return (parent.FullName, manifest);
                }

                return (sourceDir, manifest);
            }
        }

        var parentDir = Directory.GetParent(sourceDir);
        if (parentDir is not null)
        {
            var parentPath = Path.Combine(parentDir.FullName, "bundle-manifest.json");
            if (File.Exists(parentPath))
            {
                var manifest = LoadManifest(parentPath);
                if (manifest is not null) return (parentDir.FullName, manifest);
            }
        }

        return (null, null);
    }

    private static bool AreComponentsInDirectory(string root, BundleManifest manifest)
    {
        if (manifest.Components.Count == 0) return true;
        return manifest.Components.Where(c => c.Required).All(c => File.Exists(Path.Combine(root, c.MainExecutable)));
    }

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static BundleManifest? LoadManifest(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var doc = JsonSerializer.Deserialize<BundleManifest>(json, ManifestJsonOptions);
            return doc is not null && doc.Schema == "fortiq.bundle-manifest" ? doc : null;
        }
        catch
        {
            return null;
        }
    }

    public static void ValidateBundle(string bundleRoot, BundleManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleRoot);

        foreach (var component in manifest.Components)
        {
            var exePath = Path.Combine(bundleRoot, component.MainExecutable);
            if (!File.Exists(exePath))
            {
                if (component.Required)
                {
                    throw new FileNotFoundException($"Deployment bundle integrity error: required component '{component.Name}' is missing at '{exePath}'. Installation cannot proceed.");
                }
                continue;
            }

            if (!string.IsNullOrWhiteSpace(component.Sha256))
            {
                RequireHash(exePath, component.Sha256, $"component '{component.Name}'");
            }
        }

        ValidatePayload(bundleRoot, manifest);
    }

    /// <summary>
    /// Checks every file the bundle will install, not only the executable each component is named for.
    /// </summary>
    /// <remarks>
    /// The component hashes above cover four executables. A self-contained publish is mostly DLLs, and
    /// the installer copies those too - so a bundle whose Fortiq.Service.exe hashed correctly and whose
    /// Fortiq.Operations.dll had been replaced used to pass, and the service would then load the
    /// replaced library. For security software the boundary has to be every installed byte.
    ///
    /// The check runs both ways. A file listed and missing or altered is refused, and so is a file
    /// present and not listed: an attacker who cannot change a hash can still add a DLL beside one,
    /// and .NET assembly probing is happy to find it.
    /// </remarks>
    private static void ValidatePayload(string bundleRoot, BundleManifest manifest)
    {
        if (manifest.Files is not { Count: > 0 })
        {
            // A bundle produced before the manifest carried a file list. Refused rather than accepted
            // on the old terms: this validation is the reason to trust a bundle at all, and a bundle
            // that opts out of it by being old is one an attacker can also produce.
            throw new InvalidDataException(
                "Deployment bundle integrity error: the manifest carries no file list, so the bundle's " +
                "libraries cannot be verified. Rebuild it with scripts/New-DeploymentBundle.ps1.");
        }

        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in manifest.Files)
        {
            var relative = Normalize(file.Path);
            listed.Add(relative);

            var path = Path.Combine(bundleRoot, relative);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"Deployment bundle integrity error: '{relative}' is named by the manifest and is not in the bundle. Installation cannot proceed.");
            }

            var length = new FileInfo(path).Length;
            if (length != file.Length)
            {
                throw new InvalidDataException(
                    $"Deployment bundle integrity error: '{relative}' is {length} byte(s); the manifest says {file.Length}. Installation cannot proceed.");
            }

            RequireHash(path, file.Sha256, $"'{relative}'");
        }

        foreach (var path in Directory.EnumerateFiles(bundleRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Normalize(Path.GetRelativePath(bundleRoot, path));
            var name = Path.GetFileName(relative);

            // The manifest cannot list itself, and SHA256SUMS is a convenience for a person reading
            // the bundle rather than something the installer copies.
            if (string.Equals(name, "bundle-manifest.json", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "SHA256SUMS", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!listed.Contains(relative))
            {
                throw new InvalidDataException(
                    $"Deployment bundle integrity error: '{relative}' is in the bundle and not named by the manifest. Installation cannot proceed.");
            }
        }
    }

    private static void RequireHash(string path, string expected, string described)
    {
        using var stream = File.OpenRead(path);
        var computed = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));

        if (!string.Equals(computed, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Deployment bundle integrity error: {described} at '{path}' failed SHA-256 validation (expected: {expected}, actual: {computed}). Installation cannot proceed.");
        }
    }

    private static string Normalize(string relativePath) =>
        relativePath.Replace('\\', '/').TrimStart('/');

    private static void CopyBundle(string bundleRoot, BundleManifest manifest, string targetDir)
    {
        ValidateBundle(bundleRoot, manifest);

        foreach (var component in manifest.Components)
        {
            var componentFolder = Path.Combine(bundleRoot, component.Folder);
            if (Directory.Exists(componentFolder))
            {
                foreach (var file in Directory.EnumerateFiles(componentFolder, "*.*", SearchOption.TopDirectoryOnly))
                {
                    var ext = Path.GetExtension(file);
                    if (BinaryExtensions.Any(valid => string.Equals(ext, valid, StringComparison.OrdinalIgnoreCase)))
                    {
                        var dest = Path.Combine(targetDir, Path.GetFileName(file));
                        File.Copy(file, dest, overwrite: true);
                    }
                }
            }
        }

        var candidates = new[]
        {
            Path.Combine(bundleRoot, "desktop", "engines"),
            Path.Combine(bundleRoot, "service", "engines"),
            Path.Combine(bundleRoot, "engines")
        };
        var enginesDir = candidates.FirstOrDefault(Directory.Exists);
        if (enginesDir is not null)
        {
            var targetEngines = Path.Combine(targetDir, "engines");
            Directory.CreateDirectory(targetEngines);
            CopyDirectoryRecursive(enginesDir, targetEngines);
        }

        var manifestPath = Path.Combine(bundleRoot, "bundle-manifest.json");
        if (File.Exists(manifestPath))
        {
            File.Copy(manifestPath, Path.Combine(targetDir, "bundle-manifest.json"), overwrite: true);
        }

        // The recovery guide belongs on the machine that has something to recover, not only in the
        // download folder the person deleted after installing.
        foreach (var document in new[] { "SECURITY.md", "RECOVERY-GUIDE.md", "LICENSE" })
        {
            var source = Path.Combine(bundleRoot, document);
            if (File.Exists(source))
            {
                File.Copy(source, Path.Combine(targetDir, document), overwrite: true);
            }
        }

        VerifyInstalledPayload(targetDir, manifest);
    }

    /// <summary>
    /// Checks that what is now in <paramref name="targetDir"/> is what the manifest described.
    /// </summary>
    /// <remarks>
    /// The bundle is verified before the first file is copied, which catches a tampered download but
    /// says nothing about a copy that stopped halfway - a disk filling up, a file locked by something
    /// still running. That leaves half of one version beside half of another, and the installer used
    /// to call it success. An upgrade either lands completely or is reported as not having landed.
    /// </remarks>
    private static void VerifyInstalledPayload(string targetDir, BundleManifest manifest)
    {
        if (manifest.Files is not { Count: > 0 } files)
        {
            return;
        }

        // Only what CopyBundle flattens into the target: a file sitting directly in a component
        // folder. Anything deeper - the engines subtree - keeps its own layout, and the documents at
        // the bundle root are not part of the installed payload.
        var componentFolders = manifest.Components
            .Select(component => component.Folder)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var problems = new List<string>();
        foreach (var file in files)
        {
            var segments = file.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length != 2 || !componentFolders.Contains(segments[0]))
            {
                continue;
            }

            var extension = Path.GetExtension(file.Path);
            if (!BinaryExtensions.Any(valid => string.Equals(extension, valid, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var installed = Path.Combine(targetDir, Path.GetFileName(file.Path));
            if (!File.Exists(installed))
            {
                problems.Add(Path.GetFileName(file.Path) + " (missing)");
            }
            else if (new FileInfo(installed).Length != file.Length)
            {
                problems.Add(Path.GetFileName(file.Path) + " (wrong size)");
            }

            if (problems.Count >= 10)
            {
                break;
            }
        }

        if (problems.Count > 0)
        {
            throw new InvalidDataException(
                $"The installation in '{targetDir}' is incomplete: {string.Join(", ", problems)}. " +
                "Nothing was started from it. Free some disk space, close any running Fortiq, and install again.");
        }
    }

    /// <summary>
    /// Installs from a plain folder, for a source that carries no deployment manifest.
    /// </summary>
    /// <remarks>
    /// Every copy used to be attempted and every <see cref="IOException"/> swallowed, and nothing on
    /// this path checked what had arrived - <see cref="VerifyInstalledPayload"/> is reached only from
    /// the bundle path. A file locked by a running process, or a disk that filled up halfway, produced
    /// an installation missing one library and an installer that reported success. What failed next
    /// was the service failing to start, or the desktop failing to load an assembly, with nothing
    /// pointing back at the install that caused it.
    ///
    /// The copies are still attempted individually - stopping at the first failure would leave a
    /// half-installed directory and name only one of possibly several problems - but every failure is
    /// kept, the result is checked against what was on the source, and the whole thing is refused if
    /// anything is missing or the wrong size.
    /// </remarks>
    private static void CopyLocalSource(string sourceDir, string targetDir)
    {
        var problems = new List<string>();
        var expected = new List<FileInfo>();

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*.*", SearchOption.TopDirectoryOnly))
        {
            var ext = Path.GetExtension(file);
            if (!BinaryExtensions.Any(valid => string.Equals(ext, valid, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            expected.Add(new FileInfo(file));
            var destFile = Path.Combine(targetDir, Path.GetFileName(file));
            try
            {
                File.Copy(file, destFile, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{Path.GetFileName(file)} ({error.Message})");
            }
        }

        // Checked against the source rather than trusted from the copy loop: a copy that returned
        // without throwing and produced a short file is exactly the case a per-call catch cannot see.
        foreach (var source in expected)
        {
            var installed = new FileInfo(Path.Combine(targetDir, source.Name));
            if (!installed.Exists)
            {
                problems.Add(source.Name + " (missing)");
            }
            else if (installed.Length != source.Length)
            {
                problems.Add(source.Name + " (wrong size)");
            }

            if (problems.Count >= 10)
            {
                break;
            }
        }

        if (problems.Count > 0)
        {
            throw new InvalidDataException(
                $"The installation in '{targetDir}' is incomplete: {string.Join(", ", problems)}. " +
                "Nothing was started from it. Free some disk space, close any running Fortiq, and install again.");
        }

        CopyEngines(sourceDir, targetDir);
    }

    private static void CopyEngines(string sourceDir, string targetDir)
    {
        var sourceEngines = Path.Combine(sourceDir, "engines");
        var targetEngines = Path.Combine(targetDir, "engines");

        if (!Directory.Exists(sourceEngines))
        {
            var parent = new DirectoryInfo(sourceDir).Parent;
            while (parent is not null)
            {
                var candidate = Path.Combine(parent.FullName, "engines");
                if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "manifest.json")))
                {
                    sourceEngines = candidate;
                    break;
                }
                parent = parent.Parent;
            }
        }

        if (Directory.Exists(sourceEngines))
        {
            CopyDirectoryRecursive(sourceEngines, targetEngines);
        }
    }

    private static void CopyDirectoryRecursive(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source))
        {
            var destFile = Path.Combine(destination, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
        }

        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            var destDir = Path.Combine(destination, Path.GetFileName(dir));
            CopyDirectoryRecursive(dir, destDir);
        }
    }

    private static void TryAddDirectoryToPath(string directory)
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            var currentPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? string.Empty;
            var parts = currentPath.Split(';', StringSplitOptions.RemoveEmptyEntries);
            if (!parts.Any(p => string.Equals(p.Trim(), directory, StringComparison.OrdinalIgnoreCase)))
            {
                var newPath = string.IsNullOrWhiteSpace(currentPath) ? directory : $"{currentPath};{directory}";
                Environment.SetEnvironmentVariable("PATH", newPath, EnvironmentVariableTarget.Machine);
            }
        }
        catch
        {
            try
            {
                var userPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? string.Empty;
                var parts = userPath.Split(';', StringSplitOptions.RemoveEmptyEntries);
                if (!parts.Any(p => string.Equals(p.Trim(), directory, StringComparison.OrdinalIgnoreCase)))
                {
                    var newPath = string.IsNullOrWhiteSpace(userPath) ? directory : $"{userPath};{directory}";
                    Environment.SetEnvironmentVariable("PATH", newPath, EnvironmentVariableTarget.User);
                }
            }
            catch
            {
                // Non-fatal if PATH modification is denied
            }
        }
    }

    private static void TryRemoveDirectoryFromPath(string directory)
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            var currentPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? string.Empty;
            var parts = currentPath.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Where(p => !string.Equals(p.Trim(), directory, StringComparison.OrdinalIgnoreCase));
            Environment.SetEnvironmentVariable("PATH", string.Join(';', parts), EnvironmentVariableTarget.Machine);
        }
        catch
        {
            try
            {
                var userPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? string.Empty;
                var parts = userPath.Split(';', StringSplitOptions.RemoveEmptyEntries)
                    .Where(p => !string.Equals(p.Trim(), directory, StringComparison.OrdinalIgnoreCase));
                Environment.SetEnvironmentVariable("PATH", string.Join(';', parts), EnvironmentVariableTarget.User);
            }
            catch
            {
                // Non-fatal
            }
        }
    }
}
