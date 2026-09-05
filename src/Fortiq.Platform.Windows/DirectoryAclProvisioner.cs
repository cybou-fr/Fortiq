using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Fortiq.Application;

namespace Fortiq.Platform.Windows;

/// <summary>
/// Configures granular NTFS Discretionary Access Control Lists (DACLs) for Fortiq's installation
/// and state roots according to Spec 21 (Anti-Lockout Invariant).
/// </summary>
/// <remarks>
/// Prevents the common failure mode where a blanket ACL on %ProgramData%\Fortiq for SYSTEM and
/// NT SERVICE\Fortiq locks the interactive desktop operator out of writing schedules, reading
/// health reports, and registering desktop restore drill locks.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class DirectoryAclProvisioner
{
    private const InheritanceFlags FullInheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    /// <summary>
    /// Provisions granular ACLs across %ProgramData%\Fortiq and all subdirectories.
    /// </summary>
    public static void ProvisionStateDirectoryAcls(FortiqStatePaths paths, SecurityIdentifier? explicitServiceSid = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var adminsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var usersSid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var authUsersSid = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
        var serviceSid = explicitServiceSid ?? WindowsServiceController.ComputeServiceSid(WindowsServiceController.DefaultServiceName);

        var canMapServiceSid = CanMapSid(serviceSid);

        var schedulesPath = Path.Combine(paths.Root, "schedules");
        var statePath = Path.Combine(paths.Root, "state");
        var workingPath = paths.Working;
        var receiptsPath = paths.Receipts;
        var runsPath = paths.Runs;
        var healthPath = Path.GetDirectoryName(paths.HealthReport)!;
        var credentialsPath = Path.Combine(paths.Root, "credentials");
        var updatesStagingPath = Path.Combine(paths.Root, "updates", "staging");

        // Pre-create all directories before applying restrictive ACLs
        Directory.CreateDirectory(paths.Root);
        Directory.CreateDirectory(schedulesPath);
        Directory.CreateDirectory(statePath);
        Directory.CreateDirectory(workingPath);
        Directory.CreateDirectory(receiptsPath);
        Directory.CreateDirectory(runsPath);
        Directory.CreateDirectory(healthPath);
        Directory.CreateDirectory(credentialsPath);
        Directory.CreateDirectory(updatesStagingPath);

        // 1. Root state directory (%ProgramData%\Fortiq)
        ApplyAcl(paths.Root, sec =>
        {
            sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            sec.AddAccessRule(new(systemSid, FileSystemRights.FullControl, FullInheritance, PropagationFlags.None, AccessControlType.Allow));
            sec.AddAccessRule(new(adminsSid, FileSystemRights.FullControl, FullInheritance, PropagationFlags.None, AccessControlType.Allow));
            if (canMapServiceSid)
            {
                sec.AddAccessRule(new(serviceSid, FileSystemRights.FullControl, FullInheritance, PropagationFlags.None, AccessControlType.Allow));
            }
            sec.AddAccessRule(new(usersSid, FileSystemRights.ReadAndExecute | FileSystemRights.Traverse, FullInheritance, PropagationFlags.None, AccessControlType.Allow));
        });

        // 2. schedules\ — The protection wizard writes here
        ApplyAcl(schedulesPath, sec =>
        {
            sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            sec.AddAccessRule(new(systemSid, FileSystemRights.FullControl, FullInheritance, PropagationFlags.None, AccessControlType.Allow));
            sec.AddAccessRule(new(adminsSid, FileSystemRights.FullControl, FullInheritance, PropagationFlags.None, AccessControlType.Allow));
            if (canMapServiceSid)
            {
                sec.AddAccessRule(new(serviceSid, FileSystemRights.ReadAndExecute, FullInheritance, PropagationFlags.None, AccessControlType.Allow));
            }
            sec.AddAccessRule(new(authUsersSid, FileSystemRights.ReadAndExecute | FileSystemRights.Write, FullInheritance, PropagationFlags.None, AccessControlType.Allow));
        });

        // 3. state\ — Internal daemon state; operators read
        ApplyAcl(statePath, sec =>
        {
            sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            sec.AddAccessRule(new(systemSid, FileSystemRights.FullControl, FullInheritance, PropagationFlags.None, AccessControlType.Allow));
            sec.AddAccessRule(new(adminsSid, FileSystemRights.FullControl, FullInheritance, PropagationFlags.None, AccessControlType.Allow));
            if (canMapServiceSid)
            {
                sec.AddAccessRule(new(serviceSid, FileSystemRights.FullControl, FullInheritance, PropagationFlags.None, AccessControlType.Allow));
            }
            sec.AddAccessRule(new(authUsersSid, FileSystemRights.ReadAndExecute, FullInheritance, PropagationFlags.None, AccessControlType.Allow));
        });

        // 4. work\ — Scratch space for daemon runs
        ApplyAcl(workingPath, sec =>
        {
            sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            sec.AddAccessRule(new(systemSid, FileSystemRights.FullControl, FullInheritance, PropagationFlags.None, AccessControlType.Allow));
            sec.AddAccessRule(new(adminsSid, FileSystemRights.FullControl, FullInheritance, PropagationFlags.None, AccessControlType.Allow));
            if (canMapServiceSid)
            {
                sec.AddAccessRule(new(serviceSid, FileSystemRights.FullControl, FullInheritance, PropagationFlags.None, AccessControlType.Allow));
            }
        });

        // 5. work\receipts\ — The audit trail; evidence read by desktop and monitoring
        ApplyAcl(receiptsPath, sec =>
        {
            sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            sec.AddAccessRule(new(systemSid, FileSystemRights.FullControl, FullInheritance, PropagationFlags.None, AccessControlType.Allow));
            sec.AddAccessRule(new(adminsSid, FileSystemRights.FullControl, FullInheritance, PropagationFlags.None, AccessControlType.Allow));
            if (canMapServiceSid)
            {
                sec.AddAccessRule(new(serviceSid, FileSystemRights.FullControl, FullInheritance, PropagationFlags.None, AccessControlType.Allow));
            }
            sec.AddAccessRule(new(authUsersSid, FileSystemRights.ReadAndExecute, FullInheritance, PropagationFlags.None, AccessControlType.Allow));
        });

        // 6. runs\ — The run registry; desktop-initiated restore proofs write runs too
        ApplyAcl(runsPath, sec =>
        {
            sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            sec.AddAccessRule(new(systemSid, FileSystemRights.FullControl, FullInheritance, PropagationFlags.None, AccessControlType.Allow));
            sec.AddAccessRule(new(adminsSid, FileSystemRights.FullControl, FullInheritance, PropagationFlags.None, AccessControlType.Allow));
            if (canMapServiceSid)
            {
                sec.AddAccessRule(new(serviceSid, FileSystemRights.FullControl, FullInheritance, PropagationFlags.None, AccessControlType.Allow));
            }
            sec.AddAccessRule(new(authUsersSid, FileSystemRights.Modify, FullInheritance, PropagationFlags.None, AccessControlType.Allow));
        });

        // 7. health\ — Published health reports; desktop and monitoring read
        ApplyAcl(healthPath, sec =>
        {
            sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            sec.AddAccessRule(new(systemSid, FileSystemRights.FullControl, FullInheritance, PropagationFlags.None, AccessControlType.Allow));
            sec.AddAccessRule(new(adminsSid, FileSystemRights.FullControl, FullInheritance, PropagationFlags.None, AccessControlType.Allow));
            if (canMapServiceSid)
            {
                sec.AddAccessRule(new(serviceSid, FileSystemRights.FullControl, FullInheritance, PropagationFlags.None, AccessControlType.Allow));
            }
            sec.AddAccessRule(new(authUsersSid, FileSystemRights.ReadAndExecute, FullInheritance, PropagationFlags.None, AccessControlType.Allow));
        });

        // 8. credentials\ — Machine-scoped secrets; operators KEPT OUT!
        ApplyAcl(credentialsPath, sec =>
        {
            sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            sec.AddAccessRule(new(systemSid, FileSystemRights.FullControl, FullInheritance, PropagationFlags.None, AccessControlType.Allow));
            sec.AddAccessRule(new(adminsSid, FileSystemRights.FullControl, FullInheritance, PropagationFlags.None, AccessControlType.Allow));
            if (canMapServiceSid)
            {
                sec.AddAccessRule(new(serviceSid, FileSystemRights.FullControl, FullInheritance, PropagationFlags.None, AccessControlType.Allow));
            }
            // Explicitly no operator access.
        });

        // 9. updates\staging\ — TUF update staging
        ApplyAcl(updatesStagingPath, sec =>
        {
            sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            sec.AddAccessRule(new(systemSid, FileSystemRights.FullControl, FullInheritance, PropagationFlags.None, AccessControlType.Allow));
            sec.AddAccessRule(new(adminsSid, FileSystemRights.FullControl, FullInheritance, PropagationFlags.None, AccessControlType.Allow));
            if (canMapServiceSid)
            {
                sec.AddAccessRule(new(serviceSid, FileSystemRights.FullControl, FullInheritance, PropagationFlags.None, AccessControlType.Allow));
            }
        });
    }

    /// <summary>
    /// Provisions restrictive ACLs on the %ProgramFiles%\Fortiq installation directory.
    /// </summary>
    public static void ProvisionProgramFilesAcls(string installationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installationPath);

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var adminsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var usersSid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

        Directory.CreateDirectory(installationPath);

        ApplyAcl(installationPath, sec =>
        {
            sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            sec.AddAccessRule(new(systemSid, FileSystemRights.FullControl, FullInheritance, PropagationFlags.None, AccessControlType.Allow));
            sec.AddAccessRule(new(adminsSid, FileSystemRights.FullControl, FullInheritance, PropagationFlags.None, AccessControlType.Allow));
            sec.AddAccessRule(new(usersSid, FileSystemRights.ReadAndExecute, FullInheritance, PropagationFlags.None, AccessControlType.Allow));
        });
    }

    /// <summary>
    /// Verifies that state directory permissions conform to Spec 21 requirements.
    /// </summary>
    public static bool VerifyAcls(FortiqStatePaths paths, out IReadOnlyList<string> issues)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var problems = new List<string>();

        if (!OperatingSystem.IsWindows())
        {
            issues = problems;
            return true;
        }

        var authUsersSid = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
        var usersSid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

        // Check credentials is not readable by standard users
        var credsDir = Path.Combine(paths.Root, "credentials");
        if (Directory.Exists(credsDir))
        {
            try
            {
                var sec = new DirectoryInfo(credsDir).GetAccessControl(AccessControlSections.Access);
                foreach (FileSystemAccessRule rule in sec.GetAccessRules(true, false, typeof(SecurityIdentifier)))
                {
                    if (rule.IdentityReference.Equals(authUsersSid) || rule.IdentityReference.Equals(usersSid))
                    {
                        problems.Add("credentials directory admits interactive users; secrets are vulnerable to unprivileged reads.");
                    }
                }
            }
            catch (Exception ex)
            {
                problems.Add("Failed to inspect credentials DACL: " + ex.Message);
            }
        }

        issues = problems;
        return problems.Count == 0;
    }

    private static bool CanMapSid(SecurityIdentifier sid)
    {
        try
        {
            _ = sid.Translate(typeof(NTAccount));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ApplyAcl(string path, Action<DirectorySecurity> configure)
    {
        var dirInfo = new DirectoryInfo(path);
        var sec = dirInfo.GetAccessControl(AccessControlSections.Access);
        configure(sec);
        dirInfo.SetAccessControl(sec);
    }
}
