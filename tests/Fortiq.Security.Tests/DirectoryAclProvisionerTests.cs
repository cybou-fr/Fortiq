using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Fortiq.Application;
using Fortiq.Platform.Windows;

namespace Fortiq.Security.Tests;

[SupportedOSPlatform("windows")]
public sealed class DirectoryAclProvisionerTests
{
    [SkippableFact]
    public void ComputeServiceSidMatchesExpectedWindowsFormula()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "SecurityIdentifier requires Windows.");

        var sid = WindowsServiceController.ComputeServiceSid("Fortiq");
        // Verify against the Windows sc showsid Fortiq reference value:
        // S-1-5-80-3215921855-2530886538-4125144460-862951989-2054379364
        Assert.Equal("S-1-5-80-3215921855-2530886538-4125144460-862951989-2054379364", sid.Value);
    }

    [SkippableFact]
    public void StateDirectoryAclProvisioningProtectsCredentialsFromStandardUsers()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "NTFS ACL provisioning requires Windows.");

        var tempRoot = Path.Combine(Path.GetTempPath(), "fortiq-test-acl-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = FortiqStatePaths.Resolve(tempRoot);
            DirectoryAclProvisioner.ProvisionStateDirectoryAcls(paths);

            var credsDir = Path.Combine(paths.Root, "credentials");
            Assert.True(Directory.Exists(credsDir));

            var credsSecurity = new DirectoryInfo(credsDir).GetAccessControl();
            var authUsersSid = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
            var usersSid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

            var rules = credsSecurity.GetAccessRules(true, false, typeof(SecurityIdentifier));
            foreach (FileSystemAccessRule rule in rules)
            {
                Assert.False(rule.IdentityReference.Equals(authUsersSid), "Credentials directory must never admit Authenticated Users.");
                Assert.False(rule.IdentityReference.Equals(usersSid), "Credentials directory must never admit Builtin Users.");
            }

            var verified = DirectoryAclProvisioner.VerifyAcls(paths, out var issues);
            Assert.True(verified, string.Join("; ", issues));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                try
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
                catch
                {
                    // Cleaned on OS reboot if locked
                }
            }
        }
    }

    [SkippableFact]
    public void StateDirectoryAclProvisioningGrantsOperatorsAccessToSchedulesAndRuns()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "NTFS ACL provisioning requires Windows.");

        var tempRoot = Path.Combine(Path.GetTempPath(), "fortiq-test-acl-ops-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = FortiqStatePaths.Resolve(tempRoot);
            DirectoryAclProvisioner.ProvisionStateDirectoryAcls(paths);

            var schedulesDir = Path.Combine(paths.Root, "schedules");
            var runsDir = paths.Runs;

            Assert.True(Directory.Exists(schedulesDir));
            Assert.True(Directory.Exists(runsDir));

            var authUsersSid = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);

            var schedSecurity = new DirectoryInfo(schedulesDir).GetAccessControl();
            var schedRules = schedSecurity.GetAccessRules(true, false, typeof(SecurityIdentifier));
            var authUsersCanReadSchedules = false;
            var authUsersCanWriteSchedules = false;
            foreach (FileSystemAccessRule rule in schedRules)
            {
                if (!rule.IdentityReference.Equals(authUsersSid))
                {
                    continue;
                }

                if ((rule.FileSystemRights & FileSystemRights.ReadData) != 0)
                {
                    authUsersCanReadSchedules = true;
                }

                // The individual write bits, not Modify or FullControl. Those are composite values
                // that include the read bits, so masking against them reports a plain read grant as
                // a write - which is how this assertion first passed against an ACL that was correct.
                const FileSystemRights WriteBits =
                    FileSystemRights.WriteData |
                    FileSystemRights.AppendData |
                    FileSystemRights.WriteExtendedAttributes |
                    FileSystemRights.WriteAttributes |
                    FileSystemRights.Delete |
                    FileSystemRights.DeleteSubdirectoriesAndFiles |
                    FileSystemRights.ChangePermissions |
                    FileSystemRights.TakeOwnership;

                if ((rule.FileSystemRights & WriteBits) != 0)
                {
                    authUsersCanWriteSchedules = true;
                }
            }

            Assert.True(authUsersCanReadSchedules, "The desktop shows schedules, so users must be able to read them.");
            Assert.False(
                authUsersCanWriteSchedules,
                "A writable schedules directory lets any user hand the LocalSystem service a source path " +
                "and a repository of their choosing, which is the privilege boundary the IPC check holds.");

            var runsSecurity = new DirectoryInfo(runsDir).GetAccessControl();
            var runsRules = runsSecurity.GetAccessRules(true, false, typeof(SecurityIdentifier));
            var authUsersHasModifyToRuns = false;
            foreach (FileSystemAccessRule rule in runsRules)
            {
                if (rule.IdentityReference.Equals(authUsersSid) && (rule.FileSystemRights & FileSystemRights.Modify) != 0)
                {
                    authUsersHasModifyToRuns = true;
                    break;
                }
            }
            Assert.True(authUsersHasModifyToRuns, "Anti-lockout invariant: Authenticated Users must have modify access to runs\\.");
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                try
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
                catch
                {
                    // Cleaned on OS reboot if locked
                }
            }
        }
    }
}
