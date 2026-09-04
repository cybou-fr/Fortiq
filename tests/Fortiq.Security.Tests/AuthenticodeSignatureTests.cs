using System.Runtime.Versioning;
using Fortiq.Application;
using Fortiq.Infrastructure.Keys;
using Fortiq.Platform.Windows;

namespace Fortiq.Security.Tests;

/// <summary>
/// Signature verification, and the broker option that turns it into a requirement. Fortiq's own
/// binaries are not signed yet, which is exactly why the check has to report that honestly.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AuthenticodeSignatureTests
{
    [SkippableFact]
    public void AWindowsBinaryIsReportedAsTrusted()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Authenticode is a Windows notion.");
        var system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "kernel32.dll");

        Assert.Equal(SignatureStatus.Trusted, AuthenticodeSignature.Verify(system));
    }

    [SkippableFact]
    public void AFileWithNoSignatureIsReportedAsUnsignedRatherThanTrusted()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Authenticode is a Windows notion.");
        var path = Path.Combine(Path.GetTempPath(), "fortiq-unsigned-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllBytes(path, "MZ not really an executable"u8.ToArray());
        try
        {
            Assert.Equal(SignatureStatus.NotSigned, AuthenticodeSignature.Verify(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [SkippableFact]
    public void ATamperedSignedBinaryIsNeverTrusted()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Authenticode is a Windows notion.");

        var copy = Path.Combine(Path.GetTempPath(), "fortiq-tampered-" + Guid.NewGuid().ToString("N") + ".dll");
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "kernel32.dll"), copy);
        try
        {
            Skip.IfNot(
                AuthenticodeSignature.Verify(copy) == SignatureStatus.Trusted,
                "This machine has no trusted binary to tamper with.");

            using (var stream = new FileStream(copy, FileMode.Open, FileAccess.Write))
            {
                stream.Position = stream.Length / 2;
                stream.WriteByte(0x00);
            }

            // Whether the result is "broken" or "absent" depends on how the file was signed: a
            // catalog entry is matched by hash, so changing the bytes makes the file unknown rather
            // than badly signed. What matters, and what is asserted, is that it is not trusted.
            Assert.NotEqual(SignatureStatus.Trusted, AuthenticodeSignature.Verify(copy));
        }
        finally
        {
            File.Delete(copy);
        }
    }

    [SkippableFact]
    public async Task TheBrokerRefusesAnUnsignedHelperWhenSignaturesAreRequired()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Authenticode is a Windows notion.");
        var helper = Path.Combine(AppContext.BaseDirectory, "Fortiq.PasswordHelper.exe");
        Skip.IfNot(File.Exists(helper), "The password helper was not built next to the tests.");
        Skip.If(AuthenticodeSignature.Verify(helper) == SignatureStatus.Trusted, "This build of the helper is signed.");

        using var lease = new BufferKeyLease([.. Enumerable.Range(0, 32).Select(index => (byte)index)]);
        using var provider = new PasswordPipeCredentialProvider(
            new PasswordBrokerOptions(helper, RequireSignedHelper: true),
            lease);

        await using var session = await provider.BeginAsync(Guid.NewGuid(), CancellationToken.None);

        // The refusal happens before a pipe exists, so the helper never gets as far as connecting.
        var failure = await Assert.ThrowsAsync<UnlockFailedException>(() => session.CompleteAsync(CancellationToken.None));
        Assert.Equal("UnlockFailed", failure.Message);
    }

    [SkippableFact]
    public async Task AnUnsignedHelperIsAcceptedWhileSignaturesAreNotRequired()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Authenticode is a Windows notion.");
        var helper = Path.Combine(AppContext.BaseDirectory, "Fortiq.PasswordHelper.exe");
        Skip.IfNot(File.Exists(helper), "The password helper was not built next to the tests.");

        using var lease = new BufferKeyLease([.. Enumerable.Range(0, 32).Select(index => (byte)index)]);
        using var provider = new PasswordPipeCredentialProvider(new PasswordBrokerOptions(helper), lease);

        // The default remains permissive because Fortiq's own binaries are not signed yet; the
        // session opens and waits for the helper as before.
        await using var session = await provider.BeginAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.Equal("--password-command", session.EngineArguments[0]);
    }
}
