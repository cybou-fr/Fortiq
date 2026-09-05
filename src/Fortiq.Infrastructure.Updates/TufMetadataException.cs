namespace Fortiq.Infrastructure.Updates;

/// <summary>
/// Raised when update metadata is malformed, unsigned by enough trusted keys, expired, or older than
/// metadata already held.
/// </summary>
/// <remarks>
/// One exception type for every way metadata can be refused, because callers do not have a different
/// recovery for each: an update that cannot be trusted is not applied, whichever check said so. The
/// distinction that matters is in the message, which names the specific reason so an operator reading
/// a receipt can tell a clock problem from an attack.
/// </remarks>
public sealed class TufMetadataException : Exception
{
    public TufMetadataException(string message)
        : base(message)
    {
    }

    public TufMetadataException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
