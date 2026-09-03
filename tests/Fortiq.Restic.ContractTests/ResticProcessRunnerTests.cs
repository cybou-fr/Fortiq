using Fortiq.Infrastructure.Restic;

namespace Fortiq.Restic.ContractTests;

public sealed class ResticProcessRunnerTests
{
    [Fact]
    public void StartInfoUsesArgumentListWithoutShellOrInheritedEnvironment()
    {
        var engine = new VerifiedEngine("restic", "test", "win-x64", Path.GetFullPath("restic.exe"), new string('0', 64));
        var request = new ResticProcessRequest(
            ResticOperation.Backup,
            ["source path", "--json"],
            Directory.GetCurrentDirectory(),
            new Dictionary<string, string> { ["TEMP"] = "allowed" });

        var startInfo = ResticProcessRunner.CreateStartInfo(engine, request);

        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.Equal(["backup", "source path", "--json"], startInfo.ArgumentList);
        Assert.Single(startInfo.Environment);
        Assert.Equal("allowed", startInfo.Environment["TEMP"]);
    }

    [Fact]
    public void StartInfoRejectsSecretBearingEnvironmentVariable()
    {
        var engine = new VerifiedEngine("restic", "test", "win-x64", Path.GetFullPath("restic.exe"), new string('0', 64));
        var request = new ResticProcessRequest(
            ResticOperation.Check,
            [],
            Directory.GetCurrentDirectory(),
            new Dictionary<string, string> { ["RESTIC_PASSWORD"] = "must-not-leak" });

        Assert.Throws<ArgumentException>(() => ResticProcessRunner.CreateStartInfo(engine, request));
    }
}
