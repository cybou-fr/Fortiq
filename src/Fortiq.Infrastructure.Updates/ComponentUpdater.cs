namespace Fortiq.Infrastructure.Updates;

/// <summary>A component to replace: the name the release signs it under, and where it is installed.</summary>
/// <remarks>
/// The two are separate because they answer to different authorities. The target path is part of the
/// signed release and must match it exactly; the install path is a property of this machine's layout.
/// Collapsing them would make a change to either look like tampering with the other.
/// </remarks>
public sealed record ComponentTarget(string TargetPath, string RelativeInstallPath);

/// <summary>Where component bytes are fetched from.</summary>
public interface IUpdateContentSource
{
    /// <summary>
    /// Fetches <paramref name="targetPath"/>, reading no more than <paramref name="maximumLength"/> bytes.
    /// </summary>
    /// <remarks>
    /// The bound is not a convenience. Without it, a server that answers an update request with an
    /// endless stream exhausts the disk of the machine it was supposed to protect, and it does so
    /// before any hash check gets a chance to run. The trusted metadata already says how long the file
    /// is, so there is never a reason to read further.
    /// </remarks>
    Task<byte[]> FetchAsync(string targetPath, long maximumLength, CancellationToken cancellationToken);
}

/// <summary>What a platform's code-signing check said about a component.</summary>
public enum BinarySignatureStatus
{
    /// <summary>The platform does not check signatures, so nothing was asserted either way.</summary>
    NotChecked,

    /// <summary>The binary carries no signature. Recorded, not refused - see <see cref="IBinarySignaturePolicy"/>.</summary>
    Absent,

    /// <summary>The binary carries a signature that verifies.</summary>
    Valid,

    /// <summary>The binary carries a signature that does not verify.</summary>
    Invalid
}

/// <summary>The platform's opinion on a binary's code signature.</summary>
/// <remarks>
/// Injected rather than called directly, so that this project stays free of a platform dependency and
/// so that the policy below can be tested without producing signed binaries.
///
/// Per ADR-008 Revision 1 an <see cref="BinarySignatureStatus.Invalid"/> signature is refused and an
/// <see cref="BinarySignatureStatus.Absent"/> one is recorded. Fortiq holds no code-signing
/// certificate, so demanding a valid signature would reject every build the project produces - a gate
/// whose only possible resolution is switching it off, which is worse than not having it.
/// </remarks>
public interface IBinarySignaturePolicy
{
    BinarySignatureStatus Inspect(string targetPath, ReadOnlySpan<byte> content);
}

/// <summary>What an update did, in the terms a receipt records it in.</summary>
public sealed record UpdateOutcome(
    long RootVersion,
    long TargetsVersion,
    IReadOnlyList<ComponentUpdateResult> Components);

/// <summary>One component's part in an update.</summary>
public sealed record ComponentUpdateResult(
    string TargetPath,
    string RelativeInstallPath,
    long Length,
    string Sha256,
    BinarySignatureStatus Signature);

/// <summary>
/// Applies an update: fetch what the trusted release names, prove it is that, and install it or leave
/// the machine as it was.
/// </summary>
/// <remarks>
/// This is the only place the two halves meet. <see cref="TufTrustedMetadata"/> decides what is
/// authorised and <see cref="UpdateTransaction"/> decides how files move; neither knows about the
/// other, and this class is deliberately thin enough to read in one sitting, because it is where a
/// mistake would let an unverified byte reach an installed path.
/// </remarks>
public sealed class ComponentUpdater
{
    /// <summary>
    /// Staging lives beside the installation, not in the state directory.
    /// </summary>
    /// <remarks>
    /// A move between volumes is a copy and a delete, which is neither atomic nor free, and the whole
    /// crash-safety argument in <see cref="UpdateTransaction"/> rests on the move being a rename. A
    /// sibling directory is on the same volume by construction; <c>%ProgramData%</c> only usually is.
    /// </remarks>
    public const string WorkingDirectoryName = ".fortiq-update";

    private readonly IUpdateContentSource _source;
    private readonly IBinarySignaturePolicy? _signaturePolicy;

    public ComponentUpdater(IUpdateContentSource source, IBinarySignaturePolicy? signaturePolicy = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _signaturePolicy = signaturePolicy;
    }

    /// <summary>The directory an installation keeps its in-flight update in.</summary>
    public static string WorkingDirectoryFor(string installDirectory) =>
        Path.Combine(Path.GetFullPath(installDirectory), WorkingDirectoryName);

    /// <summary>
    /// Finishes anything a previous update left half-done. Call at start-up, before the installation is
    /// trusted to be one release.
    /// </summary>
    public static Task<UpdateRecoveryOutcome> RecoverAsync(
        string installDirectory,
        CancellationToken cancellationToken = default) =>
        UpdateTransaction.RecoverAsync(WorkingDirectoryFor(installDirectory), cancellationToken);

    /// <summary>
    /// Installs <paramref name="components"/> from the release <paramref name="trusted"/> currently
    /// describes, or leaves the installation exactly as it was.
    /// </summary>
    /// <exception cref="TufMetadataException">
    /// A component is not named by the trusted targets document, does not match what it says, or
    /// carries a signature that does not verify.
    /// </exception>
    public async Task<UpdateOutcome> ApplyAsync(
        TufTrustedMetadata trusted,
        string installDirectory,
        IReadOnlyList<ComponentTarget> components,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trusted);
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);
        ArgumentNullException.ThrowIfNull(components);

        if (components.Count == 0)
        {
            throw new ArgumentException("An update must name at least one component.", nameof(components));
        }

        var working = WorkingDirectoryFor(installDirectory);
        await UpdateTransaction.RecoverAsync(working, cancellationToken);

        // Everything is fetched and proven before the transaction opens. A component that turns out to
        // be wrong then costs nothing: no installed file has been touched, so there is no rollback to
        // perform and no window in which the machine is missing a binary.
        var verified = new List<(ComponentTarget Component, byte[] Content, ComponentUpdateResult Result)>();
        foreach (var component in components)
        {
            verified.Add(await VerifyAsync(trusted, component, cancellationToken));
        }

        var transaction = await UpdateTransaction.BeginAsync(
            working,
            installDirectory,
            [.. components.Select(component => component.RelativeInstallPath)],
            cancellationToken);

        try
        {
            foreach (var (component, content, _) in verified)
            {
                await transaction.StageAsync(component.RelativeInstallPath, content, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }

        return new UpdateOutcome(
            trusted.RootVersion,
            trusted.TargetsVersion ?? throw new InvalidOperationException("No trusted targets document."),
            [.. verified.Select(entry => entry.Result)]);
    }

    private async Task<(ComponentTarget, byte[], ComponentUpdateResult)> VerifyAsync(
        ComponentTarget component,
        TufFileInfo expected,
        CancellationToken cancellationToken)
    {
        var content = await _source.FetchAsync(component.TargetPath, expected.Length, cancellationToken);

        // Checked again here even though the source was told the length. The bound is a limit on what a
        // hostile server can make the client read; it is not a promise that what came back is right.
        expected.RequireMatch(content, component.TargetPath);

        var signature = _signaturePolicy?.Inspect(component.TargetPath, content) ?? BinarySignatureStatus.NotChecked;
        if (signature == BinarySignatureStatus.Invalid)
        {
            throw new TufMetadataException(
                $"'{component.TargetPath}' carries a code signature that does not verify. " +
                "A binary whose signature is broken is refused; one that is simply unsigned is recorded.");
        }

        return (
            component,
            content,
            new ComponentUpdateResult(
                component.TargetPath,
                component.RelativeInstallPath,
                expected.Length,
                expected.Sha256,
                signature));
    }

    private Task<(ComponentTarget, byte[], ComponentUpdateResult)> VerifyAsync(
        TufTrustedMetadata trusted,
        ComponentTarget component,
        CancellationToken cancellationToken)
    {
        var expected = trusted.FindTarget(component.TargetPath)
            ?? throw new TufMetadataException(
                $"The trusted release names no target '{component.TargetPath}', so nothing authorises installing it.");

        return VerifyAsync(component, expected, cancellationToken);
    }
}
