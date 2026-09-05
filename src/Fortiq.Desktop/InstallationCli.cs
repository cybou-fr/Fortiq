using System.Text;
using System.Text.Json;
using Fortiq.Application;
using Fortiq.Infrastructure.Receipts;
using Fortiq.Platform.Windows;

namespace Fortiq.Desktop;

/// <summary>
/// Headless CLI parser and executor for automated installation, status inspection,
/// and uninstallation according to Spec 21 and ADR-014.
/// </summary>
public static class InstallationCli
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static bool IsCliInvocation(string[] args)
    {
        if (args.Length == 0) return false;

        var first = args[0];
        return first is "--status" or "-status" or "/status"
            or "--install" or "-install" or "/install"
            or "--uninstall" or "-uninstall" or "/uninstall"
            or "--verify-ledger" or "-verify-ledger" or "/verify-ledger"
            or "--worker-install" or "--worker-uninstall"
            or "--help" or "-h" or "/?" or "-?";
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    public static async Task<int> RunAsync(string[] args)
    {
        if (OperatingSystem.IsWindows() && !Console.IsOutputRedirected && AttachConsole(0xFFFFFFFF))
        {
            var stdOut = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(stdOut);
            var stdErr = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
            Console.SetError(stdErr);
        }

        if (args.Length == 0 || args[0] is "--help" or "-h" or "/?" or "-?")
        {
            PrintHelp();
            return 0;
        }

        var command = args[0].TrimStart('-', '/').ToLowerInvariant();

        try
        {
            return command switch
            {
                "status" => await RunStatusAsync(args),
                "install" => await RunInstallAsync(args),
                "uninstall" => await RunUninstallAsync(args),
                "verify-ledger" => await RunVerifyLedgerAsync(args),
                "worker-install" => await RunWorkerInstallAsync(args),
                "worker-uninstall" => await RunWorkerUninstallAsync(args),
                _ => PrintSyntaxError($"Unknown command '{args[0]}'.")
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 70;
        }
        finally
        {
            Console.Out.Flush();
            Console.Error.Flush();
        }
    }

    private static async Task<int> RunStatusAsync(string[] args)
    {
        var jsonOutput = args.Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase));
        var inspector = new InstallationInspector();
        var status = await inspector.InspectAsync();

        if (jsonOutput)
        {
            Console.WriteLine(JsonSerializer.Serialize(status, JsonOptions));
            return 0;
        }

        Console.WriteLine("==================================================");
        Console.WriteLine(" Fortiq System & Component Status");
        Console.WriteLine("==================================================");
        Console.WriteLine($"Installed:        {(status.IsInstalled ? "Yes" : "No (Portable / Standalone)")}");
        Console.WriteLine($"Install Path:     {status.InstallationPath ?? "[None]"}");
        Console.WriteLine($"Executable:       {status.ExecutablePath}");
        Console.WriteLine($"Version:          {status.CurrentVersion}");
        Console.WriteLine();

        Console.WriteLine("[Windows Service]");
        Console.WriteLine($"Registered:       {(status.Service.Registered ? "Yes" : "No")}");
        Console.WriteLine($"Running:          {(status.Service.Running ? "Yes" : "No")}");
        Console.WriteLine($"Account:          {status.Service.ServiceAccount ?? "LocalSystem"}");
        Console.WriteLine($"Binary:           {status.Service.BinaryPath ?? "[None]"}");
        Console.WriteLine();

        Console.WriteLine("[Storage Engine]");
        Console.WriteLine($"Name:             {status.Engine.Name}");
        Console.WriteLine($"Required Version: {status.Engine.RequiredVersion}");
        Console.WriteLine($"Hash Verified:    {(status.Engine.HashVerified ? "Yes" : "No")}");
        Console.WriteLine($"Binary Path:      {status.Engine.BinaryPath}");
        Console.WriteLine();

        Console.WriteLine("[Password Helper]");
        Console.WriteLine($"Exists:           {(status.PasswordHelper.Exists ? "Yes" : "No")}");
        Console.WriteLine($"Authenticode:     {(status.PasswordHelper.AuthenticodeVerified ? "Verified" : "Unverified (Development)")}");
        Console.WriteLine($"Binary Path:      {status.PasswordHelper.BinaryPath}");
        Console.WriteLine();

        Console.WriteLine("[Platform Prerequisites]");
        Console.WriteLine($"TPM 2.0 Silicon:  {(status.Platform.TpmAvailable ? "Ready" : "Unavailable")}");
        Console.WriteLine($"VSS Privileges:   {(status.Platform.HasBackupPrivileges ? "Available" : "Standard Process Token")}");
        Console.WriteLine($".NET Runtime:     {status.Platform.DotNetVersion} (Valid: {status.Platform.DotNetRuntimeValid})");
        Console.WriteLine();

        Console.WriteLine("[Diagnostic Findings]");
        foreach (var finding in status.Findings)
        {
            var badge = finding.Severity switch
            {
                FindingSeverity.Error => "[ERROR]",
                FindingSeverity.Warning => "[WARN] ",
                _ => "[INFO] "
            };
            Console.WriteLine($"{badge} [{finding.Component}] {finding.Message}");
            if (!string.IsNullOrWhiteSpace(finding.RemediationAction))
            {
                Console.WriteLine($"       -> Action: {finding.RemediationAction}");
            }
        }
        Console.WriteLine("==================================================");

        return 0;
    }

    private static async Task<int> RunInstallAsync(string[] args)
    {
        var targetDir = InstallationManager.DefaultInstallPath;
        var installService = true;
        var addToPath = true;
        var provisionAcls = true;
        var silent = false;
        string? sourceDir = null;

        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if ((arg.Equals("--dir", StringComparison.OrdinalIgnoreCase) || arg.Equals("-d", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
            {
                targetDir = args[++i];
            }
            else if ((arg.Equals("--source", StringComparison.OrdinalIgnoreCase) || arg.Equals("--bundle", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
            {
                sourceDir = args[++i];
            }
            else if (arg.Equals("--no-service", StringComparison.OrdinalIgnoreCase))
            {
                installService = false;
            }
            else if (arg.Equals("--no-path", StringComparison.OrdinalIgnoreCase))
            {
                addToPath = false;
            }
            else if (arg.Equals("--no-acls", StringComparison.OrdinalIgnoreCase))
            {
                provisionAcls = false;
            }
            else if (arg.Equals("--silent", StringComparison.OrdinalIgnoreCase) || arg.Equals("-s", StringComparison.OrdinalIgnoreCase))
            {
                silent = true;
            }
        }

        var options = new InstallOptions(targetDir, installService, addToPath, sourceDir, provisionAcls);

        // Check if already elevated or unprivileged install requested
        var requiresElevation = OperatingSystem.IsWindows() && (installService || provisionAcls || addToPath);
        if (!requiresElevation || (OperatingSystem.IsWindows() && WindowsPrivilegeChecker.IsElevated()))
        {
            var progress = silent ? null : new Progress<InstallProgressReport>(p =>
            {
                Console.WriteLine($"[{p.Percent:F0}%] {p.Message}");
            });

            await InstallationManager.InstallAsync(options, progress);
            if (!silent)
            {
                Console.WriteLine("Fortiq installed successfully.");
            }
            return 0;
        }

        // Must elevate via UAC
        var json = JsonSerializer.Serialize(options);
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        var workerArgs = $"--worker-install {base64}";

        if (!silent)
        {
            Console.WriteLine("Requesting administrative elevation to install Fortiq...");
        }

        var exitCode = await InstallationManager.ElevateAndExecuteAsync(workerArgs);
        if (exitCode == 66)
        {
            Console.Error.WriteLine("Elevation was rejected by the user or prohibited by system policy.");
        }
        else if (exitCode == 0 && !silent)
        {
            Console.WriteLine("Fortiq installed successfully.");
        }

        return exitCode;
    }

    private static async Task<int> RunUninstallAsync(string[] args)
    {
        var purgeData = false;
        var silent = false;

        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.Equals("--purge-data", StringComparison.OrdinalIgnoreCase))
            {
                purgeData = true;
            }
            else if (arg.Equals("--silent", StringComparison.OrdinalIgnoreCase) || arg.Equals("-s", StringComparison.OrdinalIgnoreCase))
            {
                silent = true;
            }
        }

        var options = new UninstallOptions(purgeData);

        if (!OperatingSystem.IsWindows() || WindowsPrivilegeChecker.IsElevated())
        {
            var progress = silent ? null : new Progress<InstallProgressReport>(p =>
            {
                Console.WriteLine($"[{p.Percent:F0}%] {p.Message}");
            });

            await InstallationManager.UninstallAsync(options, progress);
            if (!silent)
            {
                Console.WriteLine("Fortiq uninstalled successfully.");
            }
            return 0;
        }

        var json = JsonSerializer.Serialize(options);
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        var workerArgs = $"--worker-uninstall {base64}";

        if (!silent)
        {
            Console.WriteLine("Requesting administrative elevation to uninstall Fortiq...");
        }

        var exitCode = await InstallationManager.ElevateAndExecuteAsync(workerArgs);
        if (exitCode == 66)
        {
            Console.Error.WriteLine("Elevation was rejected by the user.");
        }
        else if (exitCode == 0 && !silent)
        {
            Console.WriteLine("Fortiq uninstalled successfully.");
        }

        return exitCode;
    }

    private static async Task<int> RunWorkerInstallAsync(string[] args)
    {
        if (args.Length < 2) return 64;

        var json = Encoding.UTF8.GetString(Convert.FromBase64String(args[1]));
        var options = JsonSerializer.Deserialize<InstallOptions>(json)
            ?? throw new InvalidOperationException("Invalid install options payload.");

        await InstallationManager.InstallAsync(options);
        return 0;
    }

    private static async Task<int> RunWorkerUninstallAsync(string[] args)
    {
        if (args.Length < 2) return 64;

        var json = Encoding.UTF8.GetString(Convert.FromBase64String(args[1]));
        var options = JsonSerializer.Deserialize<UninstallOptions>(json)
            ?? throw new InvalidOperationException("Invalid uninstall options payload.");

        await InstallationManager.UninstallAsync(options);
        return 0;
    }

    private static async Task<int> RunVerifyLedgerAsync(string[] args)
    {
        var jsonOutput = args.Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase));
        string? targetDir = null;
        string? repoId = null;

        for (var i = 1; i < args.Length; i++)
        {
            if ((args[i].Equals("--dir", StringComparison.OrdinalIgnoreCase) ||
                 args[i].Equals("--receipts-dir", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < args.Length)
            {
                targetDir = args[++i];
            }
            else if ((args[i].Equals("--repository", StringComparison.OrdinalIgnoreCase) ||
                      args[i].Equals("-r", StringComparison.OrdinalIgnoreCase)) &&
                     i + 1 < args.Length)
            {
                repoId = args[++i];
            }
        }

        if (string.IsNullOrWhiteSpace(targetDir))
        {
            var defaultPath = FortiqStatePaths.Resolve().Receipts;
            if (Directory.Exists(defaultPath))
            {
                targetDir = defaultPath;
            }
            else
            {
                var fallbackCommon = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Fortiq", "work", "receipts");
                var localReceipts = Path.Combine(AppContext.BaseDirectory, "receipts");
                targetDir = Directory.Exists(fallbackCommon) ? fallbackCommon : (Directory.Exists(localReceipts) ? localReceipts : defaultPath);
            }
        }

        var result = await AuditLedgerVerifier.VerifyLedgerAsync(targetDir, repoId);

        if (jsonOutput)
        {
            Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            return result.IsValid ? 0 : 1;
        }

        Console.WriteLine("==================================================");
        Console.WriteLine(" Fortiq Cryptographic Audit Ledger Verification");
        Console.WriteLine("==================================================");
        Console.WriteLine($"Audit Directory:  {targetDir}");
        Console.WriteLine($"Filter Repo ID:   {repoId ?? "[All Repositories]"}");
        Console.WriteLine($"Verdict:          {(result.IsValid ? "VERIFIED (Cryptographic SHA-256 Hash-Chain Intact)" : "TAMPERING / ANOMALIES DETECTED")}");
        Console.WriteLine($"Total Receipts:   {result.TotalReceiptsVerified}");
        Console.WriteLine($"Repositories:     {result.Repositories.Count}");
        Console.WriteLine();

        foreach (var repo in result.Repositories)
        {
            Console.WriteLine($"[Repository: {repo.RepositoryId}]");
            Console.WriteLine($"  Status:         {(repo.IsValid ? "Valid (Intact)" : "INVALID")}");
            Console.WriteLine($"  Receipts Count: {repo.TotalReceipts}");
            Console.WriteLine($"  Sequence Range: {repo.FirstSequenceNumber} .. {repo.LastSequenceNumber}");
            Console.WriteLine($"  Genesis Hash:   {repo.GenesisHash}");
            Console.WriteLine($"  Head Hash:      {repo.HeadHash}");
            if (repo.Anomalies.Count > 0)
            {
                Console.WriteLine("  Anomalies:");
                foreach (var anomaly in repo.Anomalies)
                {
                    Console.WriteLine($"    - [{anomaly.AnomalyType}] Seq {anomaly.SequenceNumber}: {anomaly.Description}");
                }
            }
            Console.WriteLine();
        }

        if (result.AllAnomalies.Count > 0)
        {
            Console.Error.WriteLine($"FAILED: {result.AllAnomalies.Count} security anomaly(ies) found during audit chain verification.");
            return 1;
        }

        Console.WriteLine("SUCCESS: All cryptographic receipts and ledger hash chains verified without gaps or alterations.");
        return 0;
    }

    private static int PrintSyntaxError(string message)
    {
        Console.Error.WriteLine($"Syntax Error: {message}");
        PrintHelp();
        return 64;
    }

    private static void PrintHelp()
    {
        Console.WriteLine(@"Fortiq Desktop & Automation CLI
Usage:
  Fortiq.Desktop.exe [command] [options]

Commands:
  --status [--json]                        Inspect and report installation and component health.
  --install [--dir <path>] [--silent]      Install Fortiq, register Windows service, set ACLs.
  --uninstall [--purge-data] [--silent]    Remove Fortiq service and binaries. Preserves state
                                           unless --purge-data is specified.
  --verify-ledger [--dir <path>] [--repository <id>] [--json]
                                           Cryptographically verify the SHA-256 hash-chained audit
                                           ledger for tampering, deletion, or sequence gaps.

Options:
  --dir <path>             Custom destination folder (default: %ProgramFiles%\Fortiq).
  --source, --bundle <dir> Source directory or deployment bundle containing binaries.
  --no-service             Do not install or start the background Windows Service.
  --no-path                Do not modify the system PATH environment variable.
  --no-acls                Skip ACL provisioning (for unprivileged/test environments).
  --purge-data             Destructive: remove %ProgramData%\Fortiq state and receipts audit trail.
  --silent, -s             Suppress informational console output.
  --json                   Format output as JSON.
");
    }
}
