using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Fortiq.Application;

namespace Fortiq.Platform.Windows;

public enum FindingSeverity
{
    Info,
    Warning,
    Error
}

public sealed record InstallationFinding(
    FindingSeverity Severity,
    string Component,
    string Message,
    string? RemediationAction = null);

public sealed record ServiceComponentStatus(
    bool Registered,
    bool Running,
    string? ServiceAccount,
    Version? Version,
    string? BinaryPath);

public sealed record EngineComponentStatus(
    string Name,
    string RequiredVersion,
    string? InstalledVersion,
    bool HashVerified,
    string BinaryPath);

public sealed record HelperComponentStatus(
    bool Exists,
    bool AuthenticodeVerified,
    string BinaryPath);

public sealed record PlatformPrerequisitesStatus(
    bool TpmAvailable,
    bool HasBackupPrivileges,
    bool DotNetRuntimeValid,
    string DotNetVersion);

public sealed record SystemInstallationStatus(
    bool IsInstalled,
    string? InstallationPath,
    string ExecutablePath,
    Version CurrentVersion,
    ServiceComponentStatus Service,
    EngineComponentStatus Engine,
    HelperComponentStatus PasswordHelper,
    PlatformPrerequisitesStatus Platform,
    IReadOnlyList<InstallationFinding> Findings);

public interface IInstallationInspector
{
    Task<SystemInstallationStatus> InspectAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Discovers and evaluates whether Fortiq is installed in %ProgramFiles%\Fortiq, inspects
/// the background Windows Service, validates engine hashes, checks TPM readiness, and evaluates
/// platform prerequisites according to Spec 21.
/// </summary>
public sealed class InstallationInspector : IInstallationInspector
{
    private const string TpmProviderName = "Microsoft Platform Crypto Provider";

    public async Task<SystemInstallationStatus> InspectAsync(CancellationToken cancellationToken = default)
    {
        var findings = new List<InstallationFinding>();

        var execPath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Fortiq.Desktop.exe");
        var currentVersion = typeof(InstallationInspector).Assembly.GetName().Version ?? new Version(1, 0, 0);

        var defaultInstallPath = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Fortiq")
            : Path.Combine(AppContext.BaseDirectory, "installed");

        // 1. Service inspection
        ServiceComponentStatus serviceStatus;
        if (OperatingSystem.IsWindows())
        {
            var serviceInfo = WindowsServiceController.QueryStatus(WindowsServiceController.DefaultServiceName);
            Version? serviceVersion = null;
            if (serviceInfo.Exists && !string.IsNullOrWhiteSpace(serviceInfo.BinaryPath))
            {
                var cleanedPath = serviceInfo.BinaryPath.Trim('\"');
                if (File.Exists(cleanedPath))
                {
                    try
                    {
                        var fvi = FileVersionInfo.GetVersionInfo(cleanedPath);
                        serviceVersion = new Version(fvi.FileMajorPart, fvi.FileMinorPart, fvi.FileBuildPart, Math.Max(0, fvi.FilePrivatePart));
                    }
                    catch
                    {
                        // Ignore version parse failures
                    }
                }
            }

            serviceStatus = new ServiceComponentStatus(
                Registered: serviceInfo.Exists,
                Running: serviceInfo.Running,
                ServiceAccount: serviceInfo.AccountName,
                Version: serviceVersion,
                BinaryPath: serviceInfo.BinaryPath?.Trim('\"'));

            if (serviceStatus.Registered)
            {
                findings.Add(new(FindingSeverity.Info, "Service", $"Windows service '{WindowsServiceController.DefaultServiceName}' is registered (Running: {serviceStatus.Running})."));
            }
            else
            {
                findings.Add(new(FindingSeverity.Info, "Service", $"Windows service '{WindowsServiceController.DefaultServiceName}' is not registered.", "Install Fortiq service for automated scheduled protection."));
            }
        }
        else
        {
            serviceStatus = new ServiceComponentStatus(false, false, null, null, null);
        }

        // 2. Engine inspection
        var engineStatus = await InspectEngineAsync(findings, cancellationToken);

        // 3. Password Helper inspection
        var helperStatus = InspectPasswordHelper(findings);

        // 4. Platform Prerequisites
        var platformStatus = InspectPlatformPrerequisites(findings);

        // 5. Overall Installation Status
        var isRunningFromInstallPath = execPath.StartsWith(defaultInstallPath, StringComparison.OrdinalIgnoreCase);
        var isServiceInstalledInPath = serviceStatus.Registered
            && serviceStatus.BinaryPath is not null
            && serviceStatus.BinaryPath.StartsWith(defaultInstallPath, StringComparison.OrdinalIgnoreCase)
            && File.Exists(serviceStatus.BinaryPath);

        var isInstalled = (isRunningFromInstallPath || isServiceInstalledInPath) && serviceStatus.Registered;
        var resolvedInstallPath = isInstalled
            ? defaultInstallPath
            : (Directory.Exists(defaultInstallPath) ? defaultInstallPath : null);

        return new SystemInstallationStatus(
            IsInstalled: isInstalled,
            InstallationPath: resolvedInstallPath,
            ExecutablePath: execPath,
            CurrentVersion: currentVersion,
            Service: serviceStatus,
            Engine: engineStatus,
            PasswordHelper: helperStatus,
            Platform: platformStatus,
            Findings: findings);
    }

    private static async Task<EngineComponentStatus> InspectEngineAsync(List<InstallationFinding> findings, CancellationToken cancellationToken)
    {
        var engineRoot = ResolveEngineRoot();
        var manifestPath = Path.Combine(engineRoot, "manifest.json");

        string requiredVersion = "unknown";
        string relativePath = OperatingSystem.IsWindows() ? "restic.exe" : "restic";
        string? expectedSha256 = null;
        long expectedLength = 0;

        if (File.Exists(manifestPath))
        {
            try
            {
                var content = await File.ReadAllTextAsync(manifestPath, cancellationToken);
                using var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;
                if (root.TryGetProperty("engines", out var engines) && engines.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in engines.EnumerateArray())
                    {
                        var rid = entry.GetProperty("rid").GetString();
                        var name = entry.GetProperty("name").GetString();
                        if (name == "restic" && (rid == "win-x64" || (!OperatingSystem.IsWindows() && rid == "linux-x64")))
                        {
                            requiredVersion = entry.GetProperty("version").GetString() ?? "unknown";
                            relativePath = entry.GetProperty("relativePath").GetString() ?? relativePath;
                            expectedSha256 = entry.GetProperty("binarySha256").GetString();
                            expectedLength = entry.GetProperty("binaryLength").GetInt64();
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                findings.Add(new(FindingSeverity.Warning, "Engine", $"Error reading engine manifest: {ex.Message}"));
            }
        }

        var binaryPath = Path.Combine(engineRoot, relativePath);
        var hashVerified = false;
        string? installedVersion = null;

        if (File.Exists(binaryPath))
        {
            try
            {
                var fi = new FileInfo(binaryPath);
                if (expectedLength == 0 || fi.Length == expectedLength)
                {
                    var fileBytes = await File.ReadAllBytesAsync(binaryPath, cancellationToken);
                    var hashBytes = SHA256.HashData(fileBytes);
                    var computedHash = Convert.ToHexStringLower(hashBytes);

                    if (expectedSha256 is not null && string.Equals(computedHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        hashVerified = true;
                        installedVersion = requiredVersion;
                        findings.Add(new(FindingSeverity.Info, "Engine", $"Storage engine 'restic' v{requiredVersion} SHA-256 validated."));
                    }
                    else if (expectedSha256 is null)
                    {
                        hashVerified = true;
                        installedVersion = requiredVersion;
                        findings.Add(new(FindingSeverity.Info, "Engine", $"Storage engine 'restic' present at {binaryPath}."));
                    }
                    else
                    {
                        findings.Add(new(FindingSeverity.Error, "Engine", "Storage engine SHA-256 mismatch against manifest.", "Acquire matching pinned restic binary."));
                    }
                }
                else
                {
                    findings.Add(new(FindingSeverity.Error, "Engine", $"Storage engine length mismatch (expected {expectedLength}, got {fi.Length})."));
                }
            }
            catch (Exception ex)
            {
                findings.Add(new(FindingSeverity.Error, "Engine", $"Failed to hash storage engine: {ex.Message}"));
            }
        }
        else
        {
            findings.Add(new(FindingSeverity.Warning, "Engine", $"Storage engine not found at {binaryPath}.", "Place verified restic binary into engines/ directory."));
        }

        return new EngineComponentStatus(
            Name: "restic",
            RequiredVersion: requiredVersion,
            InstalledVersion: installedVersion,
            HashVerified: hashVerified,
            BinaryPath: binaryPath);
    }

    private static HelperComponentStatus InspectPasswordHelper(List<InstallationFinding> findings)
    {
        var helperPath = ResolveHelperPath();
        var exists = File.Exists(helperPath);
        var authenticodeVerified = false;

        if (exists)
        {
            if (OperatingSystem.IsWindows())
            {
                var status = AuthenticodeSignature.Verify(helperPath);
                authenticodeVerified = status == SignatureStatus.Trusted;
                if (authenticodeVerified)
                {
                    findings.Add(new(FindingSeverity.Info, "PasswordHelper", "Password helper Authenticode signature verified."));
                }
                else
                {
                    findings.Add(new(FindingSeverity.Info, "PasswordHelper", "Password helper is present (development/unsigned build)."));
                }
            }
            else
            {
                findings.Add(new(FindingSeverity.Info, "PasswordHelper", "Password helper binary exists."));
            }
        }
        else
        {
            findings.Add(new(FindingSeverity.Warning, "PasswordHelper", "Password helper executable not found.", "Ensure Fortiq.PasswordHelper.exe is present in the application directory."));
        }

        return new HelperComponentStatus(exists, authenticodeVerified, helperPath);
    }

    private static PlatformPrerequisitesStatus InspectPlatformPrerequisites(List<InstallationFinding> findings)
    {
        var tpmAvailable = CheckTpmAvailability();
        if (tpmAvailable)
        {
            findings.Add(new(FindingSeverity.Info, "TPM", "Hardware TPM 2.0 silicon provider is available."));
        }
        else
        {
            findings.Add(new(FindingSeverity.Warning, "TPM", "Hardware TPM 2.0 silicon was not detected or inaccessible.", "Software key envelopes and BIP-39 recovery mnemonic will be used for repository security."));
        }

        var hasBackupPrivileges = OperatingSystem.IsWindows() && WindowsPrivilegeChecker.HasPrivilege("SeBackupPrivilege");
        if (hasBackupPrivileges)
        {
            findings.Add(new(FindingSeverity.Info, "VSS", "VSS snapshot privileges (SeBackupPrivilege) are present."));
        }
        else
        {
            findings.Add(new(FindingSeverity.Info, "VSS", "SeBackupPrivilege not held in interactive desktop token (elevated background service handles snapshot protection)."));
        }

        var dotnetVersion = Environment.Version.ToString();
        var dotnetValid = Environment.Version.Major >= 10;
        if (dotnetValid)
        {
            findings.Add(new(FindingSeverity.Info, "Runtime", $".NET 10 LTS Runtime detected ({dotnetVersion})."));
        }
        else
        {
            findings.Add(new(FindingSeverity.Error, "Runtime", $".NET 10.0 or later is required (detected {dotnetVersion})."));
        }

        return new PlatformPrerequisitesStatus(tpmAvailable, hasBackupPrivileges, dotnetValid, dotnetVersion);
    }

    private static bool CheckTpmAvailability()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var probeName = "fortiq-probe-" + Guid.NewGuid().ToString("N");
            var parameters = new CngKeyCreationParameters
            {
                Provider = new CngProvider(TpmProviderName),
                ExportPolicy = CngExportPolicies.None,
                KeyCreationOptions = CngKeyCreationOptions.None
            };
            parameters.Parameters.Add(new CngProperty("Length", BitConverter.GetBytes(2048), CngPropertyOptions.None));
            using var key = CngKey.Create(CngAlgorithm.Rsa, probeName, parameters);
            key.Delete();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveEngineRoot()
    {
        if (Environment.GetEnvironmentVariable("FORTIQ_ENGINE_ROOT") is { Length: > 0 } configured && Directory.Exists(configured))
        {
            return configured;
        }

        var candidate = Path.Combine(AppContext.BaseDirectory, "engines");
        if (File.Exists(Path.Combine(candidate, "manifest.json")))
        {
            return candidate;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var enginesPath = Path.Combine(directory.FullName, "engines");
            if (File.Exists(Path.Combine(enginesPath, "manifest.json")))
            {
                return enginesPath;
            }
            directory = directory.Parent;
        }

        if (OperatingSystem.IsWindows())
        {
            var programFilesEngines = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Fortiq", "engines");
            if (File.Exists(Path.Combine(programFilesEngines, "manifest.json")))
            {
                return programFilesEngines;
            }
        }

        return candidate;
    }

    private static string ResolveHelperPath()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "Fortiq.PasswordHelper.exe");
        if (File.Exists(local))
        {
            return local;
        }

        if (OperatingSystem.IsWindows())
        {
            var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Fortiq", "Fortiq.PasswordHelper.exe");
            if (File.Exists(installed))
            {
                return installed;
            }
        }

        return local;
    }
}
