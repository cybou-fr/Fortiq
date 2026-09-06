using Fortiq.Application;

namespace Fortiq.Desktop.ViewModels;

/// <summary>
/// Turns an exception into a sentence for the person who is standing there.
/// </summary>
/// <remarks>
/// Fortiq's own exceptions are written for people already, and those are passed through untouched.
/// What was reaching the screen unedited was the other half - the framework's. "Access to the path
/// 'C:\ProgramData\Fortiq\schedules' is denied." is a true sentence that tells somebody protecting
/// their photos nothing they can act on, and a raw <c>System.IO.IOException</c> message with a path
/// in it is worse than useless on a screen they cannot copy from.
///
/// Only the failures a person can actually meet are translated. Anything unrecognised keeps its own
/// message rather than being replaced by a vague one: an unhelpful specific beats a helpful-sounding
/// generality when somebody has to report what happened.
/// </remarks>
public static class PlainFailure
{
    /// <summary>What to put on the screen for <paramref name="error"/>.</summary>
    public static string Describe(Exception? error) => error switch
    {
        null => "Something went wrong, and Fortiq did not record what.",

        // Written for people, by this codebase. Passing these through is the point.
        UnlockFailedException => error.Message,

        OperationCanceledException => "Cancelled.",

        UnauthorizedAccessException => "Windows refused access to that location. Choose a folder you own, "
            + "or use the step that asks for administrator permission.",

        DirectoryNotFoundException => "A folder Fortiq expected is not there any more. It may have been "
            + "moved or renamed, or an external drive may be unplugged.",

        FileNotFoundException => "A file Fortiq expected is not there any more. If this is a repository "
            + "or a recovery kit, check that the drive holding it is connected.",

        // Disk full, drive removed, file locked. The engine's own words are worth keeping here.
        IOException => "The disk or network reported a problem: " + error.Message,

        TimeoutException => "That took longer than Fortiq was willing to wait. It may still be working; "
            + "check again in a moment before trying anything else.",

        _ => error.Message
    };
}
