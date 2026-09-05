using Fortiq.Application;
using Fortiq.Infrastructure.Receipts;

namespace Fortiq.Operations;

/// <summary>
/// Builds the anchor every operation records its ledger head to.
/// </summary>
/// <remarks>
/// The anchor implementations existed for some time without anything constructing one: every
/// production receipt store was built with no anchor at all, while the README said heads were anchored
/// outside the receipt directory. A capability nothing composes is not a control, and this class is
/// the composition that was missing.
/// </remarks>
public static class AuditAnchors
{
    /// <summary>The file heads are appended to, kept out of the receipt directory it attests to.</summary>
    public const string AnchorFileName = "ledger-heads.jsonl";

    /// <summary>
    /// The anchor for a machine's state directory: an append-only file beside the receipts, and on
    /// Windows the event log as well.
    /// </summary>
    /// <remarks>
    /// Two sinks because they fail differently. The file survives a reboot and can be read by anyone
    /// investigating afterwards, but it is on the same disk as the thing it attests to. ETW events go
    /// wherever the machine's event collection sends them, which may be off the machine entirely -
    /// beyond the reach of whoever rewrote the receipts, which is the only place an anchor is worth
    /// having. Neither alone is sufficient and neither is asked to be.
    /// </remarks>
    public static IAuditLedgerAnchor ForState(FortiqStatePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var file = new FileAuditAnchor(Path.Combine(paths.AuditAnchors, AnchorFileName));

        return OperatingSystem.IsWindows()
            ? new CompositeAuditAnchor(file, EtwAuditAnchor.Instance)
            : file;
    }
}
