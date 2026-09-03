namespace Fortiq.Application;

/// <summary>
/// Raised when a restore produced a tree Fortiq refuses to hand over: a reparse point, a symbolic
/// link, or any entry that resolves outside the staging directory. The restored data is discarded
/// and the caller's target is left untouched, so a rejected restore never becomes a partial one.
/// </summary>
public sealed class RestoreRejectedException : Exception
{
    public RestoreRejectedException(string message)
        : base(message)
    {
    }

    public RestoreRejectedException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    public RestoreRejectedException()
        : base("The restored tree was rejected.")
    {
    }
}
