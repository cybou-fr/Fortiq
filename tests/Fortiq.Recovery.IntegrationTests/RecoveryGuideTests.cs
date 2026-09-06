using System.Text.RegularExpressions;
using Fortiq.Recover;

namespace Fortiq.Recovery.IntegrationTests;

/// <summary>
/// That the guide somebody reads on the worst day of their year describes the tool that shipped.
/// </summary>
/// <remarks>
/// It had drifted. The tool grew <c>files</c> and <c>check</c> and a <c>--source</c> option for
/// restoring one folder instead of everything, and RECOVERY-GUIDE.md documented three of the five
/// commands and none of that option - while its own opening steps promised "pick what to restore".
///
/// Nothing catches this otherwise: <c>Test-DocumentationClaims.ps1</c> checks that documents name
/// identifiers that exist, which is a different question from whether a document that teaches a
/// command line teaches all of it. This is the cheapest possible answer to that question - the usage
/// text is the tool's own account of itself, and the guide has to cover it.
/// </remarks>
public sealed class RecoveryGuideTests
{
    [Fact]
    public void EveryCommandTheToolOffersIsInTheGuide()
    {
        var guide = Guide();

        foreach (var command in CommandsInUsage())
        {
            Assert.True(
                guide.Contains($"Fortiq.Recover.exe {command} ", StringComparison.Ordinal),
                $"RECOVERY-GUIDE.md never shows how to run '{command}', which the tool offers.");
        }
    }

    [Fact]
    public void TheOptionThatRestoresOneFolderRatherThanEverythingIsInTheGuide()
    {
        // Singled out because the guide's own step 3 promises it - "pick what to restore" - and
        // because a person restoring one file from a large backup is the likeliest reader there is.
        Assert.Contains("--source", Guide(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheGuideDoesNotTeachPuttingTheRecoveryWordsOnACommandLine()
    {
        // The one instruction that would undo the reason the tool reads them from the keyboard.
        var guide = Guide();

        Assert.DoesNotContain("--mnemonic", guide, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--passphrase", guide, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The commands the tool's own usage text lists, read from the tool rather than restated.</summary>
    private static string[] CommandsInUsage()
    {
        var usage = RecoveryCli.Usage;
        var commands = usage[usage.IndexOf("Commands:", StringComparison.Ordinal)..];
        commands = commands[..commands.IndexOf("Options:", StringComparison.Ordinal)];

        var found = Regex.Matches(commands, @"^\s{2,}([a-z]+)\s{2,}", RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value)
            .ToArray();

        // A parser that found nothing, or found one thing and missed the rest, would make this whole
        // file pass forever. "restore" is the command the tool exists for; if that is not in what was
        // parsed, what was parsed is not the command list.
        Assert.NotEmpty(found);
        Assert.Contains("restore", found);
        return found;
    }

    private static string Guide() =>
        File.ReadAllText(Path.Combine(RecoveryWorkspace.RepositoryRootPath, "RECOVERY-GUIDE.md"));
}
