using System.Diagnostics;
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
    bool ProvisionAcls = true);

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
        IProgress<(string Message, double Percent)> progress,
        CancellationToken cancellationToken)
    {
        var options = new InstallOptions(targetDir, installService, addToPath);

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
        }

        progress?.Report(new("Installation completed successfully.", 100));
        await Task.CompletedTask;
    }

    public static async Task UninstallAsync(
        UninstallOptions options,
        IProgress<InstallProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var targetDir = Path.GetFullPath(options.TargetDirectory ?? DefaultInstallPath);

        progress?.Report(new("Stopping and removing Windows Service...", 20));
        if (OperatingSystem.IsWindows())
        {
            try
            {
                WindowsServiceController.StopService(WindowsServiceController.DefaultServiceName, TimeSpan.FromSeconds(10));
                WindowsServiceController.DeleteService(WindowsServiceController.DefaultServiceName);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Warning stopping/deleting service: " + ex.Message);
            }

            progress?.Report(new("Removing directory from PATH...", 40));
            TryRemoveDirectoryFromPath(targetDir);
        }

        progress?.Report(new("Removing program files...", 60));
        if (Directory.Exists(targetDir))
        {
            try
            {
                Directory.Delete(targetDir, recursive: true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Warning deleting program files: " + ex.Message);
            }
        }

        if (options.PurgeData)
        {
            progress?.Report(new("Purging state data directories...", 80));
            var statePaths = FortiqStatePaths.Resolve();
            if (Directory.Exists(statePaths.Root))
            {
                try
                {
                    Directory.Delete(statePaths.Root, recursive: true);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Warning purging state directory: " + ex.Message);
                }
            }
        }

        progress?.Report(new("Uninstallation complete.", 100));
        await Task.CompletedTask;
    }

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

    public sealed record BundleManifest(
        string Schema,
        string Version,
        string Rid,
        string? Created,
        IReadOnlyList<BundleComponentManifest> Components);

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
                using var stream = File.OpenRead(exePath);
                var hashBytes = System.Security.Cryptography.SHA256.HashData(stream);
                var computedHash = Convert.ToHexStringLower(hashBytes);
                if (!string.Equals(computedHash, component.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Deployment bundle integrity error: component '{component.Name}' at '{exePath}' failed SHA-256 validation (expected: {component.Sha256}, actual: {computedHash}). Installation cannot proceed.");
                }
            }
        }
    }

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

        var securityDoc = Path.Combine(bundleRoot, "SECURITY.md");
        if (File.Exists(securityDoc))
        {
            File.Copy(securityDoc, Path.Combine(targetDir, "SECURITY.md"), overwrite: true);
        }
    }

    private static void CopyLocalSource(string sourceDir, string targetDir)
    {
        var sourceFiles = Directory.EnumerateFiles(sourceDir, "*.*", SearchOption.TopDirectoryOnly);
        foreach (var file in sourceFiles)
        {
            var ext = Path.GetExtension(file);
            if (BinaryExtensions.Any(valid => string.Equals(ext, valid, StringComparison.OrdinalIgnoreCase)))
            {
                var destFile = Path.Combine(targetDir, Path.GetFileName(file));
                try
                {
                    File.Copy(file, destFile, overwrite: true);
                }
                catch (IOException)
                {
                }
            }
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
