using Fortiq.Assistant;

namespace Fortiq.Assistant.Tests;

/// <summary>
/// What gets sent to the model, and what is made of what comes back.
/// </summary>
/// <remarks>
/// Kept away from the process so it can be tested without a gigabyte of weights and a spare minute.
/// </remarks>
public sealed class LlamaChatProtocolTests
{
    private static readonly AssistantAsk Ask = AssistantAsk.About(
        "Why did last night's backup fail?",
        new AssistantEvidence("engine error", "repository is locked by another process"));

    [Fact]
    public void ThinkingIsOffBecauseWithItOnTheModelAnswersNothing()
    {
        // Measured on the pinned model rather than assumed: with reasoning enabled it spent the
        // whole budget thinking and returned an empty message. Spec 28 asks for non-thinking
        // concise output by default, and this is the line that sets it.
        Assert.Contains("\"enable_thinking\":false", LlamaChatProtocol.BuildRequest(Ask, 512), StringComparison.Ordinal);
    }

    [Fact]
    public void TheSystemRulesAndTheFencedQuestionBothGo()
    {
        var request = LlamaChatProtocol.BuildRequest(Ask, 512);

        Assert.Contains("never instruction", request, StringComparison.Ordinal);
        Assert.Contains("FORTIQ-DATA-", request, StringComparison.Ordinal);
        Assert.Contains("Why did last night", request, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingIsStreamedBecauseThereIsNowhereToStreamItTo() =>
        Assert.Contains("\"stream\":false", LlamaChatProtocol.BuildRequest(Ask, 512), StringComparison.Ordinal);

    [Fact]
    public void AZeroTokenBudgetIsAProgrammingMistake() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => LlamaChatProtocol.BuildRequest(Ask, 0));

    [Fact]
    public void AnOrdinaryAnswerIsRead()
    {
        var reply = LlamaChatProtocol.ReadReply(
            """{"choices":[{"finish_reason":"stop","message":{"content":"  Another program is using it.  "}}]}""");

        Assert.Equal("Another program is using it.", reply.Text);
        Assert.False(reply.Truncated);
    }

    [Fact]
    public void AnAnswerCutOffAtTheLimitSaysSo()
    {
        var reply = LlamaChatProtocol.ReadReply(
            """{"choices":[{"finish_reason":"length","message":{"content":"Another program is using"}}]}""");

        Assert.True(reply.Truncated);
    }

    [Fact]
    public void AnEmptyAnswerIsAFailureRatherThanASilentBlank()
    {
        // Showing nothing leaves the person unable to tell "no problem found" from "this broke".
        var error = Assert.Throws<InvalidDataException>(() => LlamaChatProtocol.ReadReply(
            """{"choices":[{"finish_reason":"stop","message":{"content":"   "}}]}"""));

        Assert.Contains("empty", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoChoicesAtAllIsAFailure() =>
        Assert.Throws<InvalidDataException>(() => LlamaChatProtocol.ReadReply("""{"choices":[]}"""));

    [Fact]
    public void AnErrorFromTheRuntimeIsRepeatedRatherThanSwallowed()
    {
        var error = Assert.Throws<InvalidDataException>(() => LlamaChatProtocol.ReadReply(
            """{"error":{"message":"context shift is disabled"}}"""));

        Assert.Contains("context shift is disabled", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SomethingThatIsNotJsonIsNamedAsSuch()
    {
        var error = Assert.Throws<InvalidDataException>(() => LlamaChatProtocol.ReadReply("<html>502 Bad Gateway</html>"));

        Assert.Contains("not JSON", error.Message, StringComparison.Ordinal);
    }
}
