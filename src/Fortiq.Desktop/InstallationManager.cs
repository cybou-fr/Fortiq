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
    string? SourceDirectory = null);

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

        progress?.Report(new("Copying application binaries...", 25));
        CopyBinaries(sourceDir, targetDir);

        progress?.Report(new("Copying storage engines and manifests...", 45));
        CopyEngines(sourceDir, targetDir);

        if (OperatingSystem.IsWindows())
        {
            progress?.Report(new("Configuring secure directory ACLs...", 60));
            try
            {
                DirectoryAclProvisioner.ProvisionProgramFilesAcls(targetDir);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Warning setting program files ACL: " + ex.Message);
            }

            try
            {
                var statePaths = FortiqStatePaths.Resolve();
                DirectoryAclProvisioner.ProvisionStateDirectoryAcls(statePaths);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Warning setting state ACL: " + ex.Message);
            }

            if (options.InstallService)
            {
                progress?.Report(new("Registering Windows Service 'Fortiq'...", 75));
                var serviceExePath = Path.Combine(targetDir, "Fortiq.Service.exe");
                if (File.Exists(serviceExePath))
                {
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
                        try
                        {
                            WindowsServiceController.StartService(WindowsServiceController.DefaultServiceName, TimeSpan.FromSeconds(10));
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine("Warning starting service during install: " + ex.Message);
                        }
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"Failed to register Windows Service: {ex.Message}", ex);
                    }
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

    private static void CopyBinaries(string sourceDir, string targetDir)
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
                    // If target file is locked, ignore or retry
                }
            }
        }

        // If Fortiq.Service or Fortiq.Recover are missing from sourceDir (e.g. running from project bin/ in dev),
        // resolve and copy them from sibling build directories so the installed environment is complete.
        CopySiblingComponentIfMissing("Fortiq.Service", sourceDir, targetDir);
        CopySiblingComponentIfMissing("Fortiq.Recover", sourceDir, targetDir);
    }

    private static void CopySiblingComponentIfMissing(string componentName, string sourceDir, string targetDir)
    {
        var targetExe = Path.Combine(targetDir, $"{componentName}.exe");
        if (File.Exists(targetExe)) return;

        var parent = new DirectoryInfo(sourceDir).Parent;
        while (parent is not null)
        {
            var siblingDir = Path.Combine(parent.FullName, componentName);
            if (Directory.Exists(siblingDir))
            {
                var candidateFiles = Directory.EnumerateFiles(siblingDir, $"{componentName}.*", SearchOption.AllDirectories)
                    .Where(f => BinaryExtensions.Any(ext => string.Equals(Path.GetExtension(f), ext, StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                var releaseCandidate = candidateFiles.FirstOrDefault(f => f.Contains("Release", StringComparison.OrdinalIgnoreCase) && Path.GetFileName(f).Equals($"{componentName}.exe", StringComparison.OrdinalIgnoreCase));
                var exeToUse = releaseCandidate ?? candidateFiles.FirstOrDefault(f => Path.GetFileName(f).Equals($"{componentName}.exe", StringComparison.OrdinalIgnoreCase));

                if (exeToUse is not null)
                {
                    var componentDir = Path.GetDirectoryName(exeToUse)!;
                    foreach (var file in Directory.EnumerateFiles(componentDir, "*.*", SearchOption.TopDirectoryOnly))
                    {
                        var ext = Path.GetExtension(file);
                        if (BinaryExtensions.Any(valid => string.Equals(ext, valid, StringComparison.OrdinalIgnoreCase)))
                        {
                            var destFile = Path.Combine(targetDir, Path.GetFileName(file));
                            try
                            {
                                File.Copy(file, destFile, overwrite: true);
                            }
                            catch (IOException) { }
                        }
                    }
                    break;
                }
            }
            parent = parent.Parent;
        }
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
