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
    public void ASignedBinaryThisMachineTrustsIsReportedAsTrusted()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Authenticode is a Windows notion.");

        // Which files come back trusted depends on how the installation signs them - a catalog entry
        // is not the same as an embedded signature, and Windows editions differ - so this asserts
        // that a trusted file is recognised where one exists rather than that any given file is.
        var trusted = TrustedSample();
        Skip.If(trusted is null, "This machine exposes no file this check reports as trusted.");

        Assert.Equal(SignatureStatus.Trusted, AuthenticodeSignature.Verify(trusted!));
    }

    [SkippableFact]
    public void AFileWithNoSignatureIsNeverTrusted()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Authenticode is a Windows notion.");
        var path = Path.Combine(Path.GetTempPath(), "fortiq-unsigned-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllBytes(path, "MZ not really an executable"u8.ToArray());
        try
        {
            // Windows reports "no signature" for some inputs and "cannot make sense of this file"
            // for others, and which one it picks varies by edition. The decision Fortiq makes is the
            // same either way, and that is what is asserted.
            Assert.NotEqual(SignatureStatus.Trusted, AuthenticodeSignature.Verify(path));
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

        var original = TrustedSample();
        Skip.If(original is null, "This machine has no trusted binary to tamper with.");

        var copy = Path.Combine(Path.GetTempPath(), "fortiq-tampered-" + Guid.NewGuid().ToString("N") + Path.GetExtension(original!));
        File.Copy(original!, copy);
        try
        {
            Skip.IfNot(
                AuthenticodeSignature.Verify(copy) == SignatureStatus.Trusted,
                "The signature of this sample does not travel with a copy of it.");

            // Flip the byte rather than write a fixed value: writing a zero over a byte that was
            // already zero changes nothing, and a file that was never modified stays trusted.
            var before = Hash(copy);
            using (var stream = new FileStream(copy, FileMode.Open, FileAccess.ReadWrite))
            {
                stream.Position = stream.Length / 2;
                var existing = stream.ReadByte();
                stream.Position = stream.Length / 2;
                stream.WriteByte((byte)(existing ^ 0xFF));
            }

            Assert.NotEqual(before, Hash(copy));

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

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
    }

    /// <summary>A file this machine reports as trusted, if it has one.</summary>
    private static string? TrustedSample()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "kernel32.dll"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
            Environment.ProcessPath ?? string.Empty
        };

        return candidates.FirstOrDefault(candidate =>
            !string.IsNullOrEmpty(candidate)
            && File.Exists(candidate)
            && AuthenticodeSignature.Verify(candidate) == SignatureStatus.Trusted);
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
        // It is reported as what it is - the helper on this machine is not signed - rather than as the
        // bare word a wrong recovery phrase produces. The state of the caller's own binary is not a
        // secret, and no secret was reached, let alone tested.
        var failure = await Assert.ThrowsAsync<CredentialHandoverException>(
            () => session.CompleteAsync(CancellationToken.None));

        Assert.IsAssignableFrom<UnlockFailedException>(failure);
        Assert.Contains("not signed", failure.Message, StringComparison.OrdinalIgnoreCase);
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
