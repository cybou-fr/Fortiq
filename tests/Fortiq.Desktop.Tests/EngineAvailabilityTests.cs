using Fortiq.Desktop;

namespace Fortiq.Desktop.Tests;

/// <summary>
/// Whether this copy of Fortiq has the engine it runs, asked when the application opens.
/// </summary>
/// <remarks>
/// The engine is not committed and is not part of the desktop's build output, so a copy without one
/// is an ordinary mistake rather than an exotic failure: one folder copied out of the package, an
/// archive that did not finish unpacking, a development build on a machine where nobody ran the
/// acquisition script. Every one of those used to look completely normal until the first backup.
/// </remarks>
public sealed class EngineAvailabilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fortiq-engine-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AnEngineThatIsWhereItShouldBeIsNotComplainedAbout()
    {
        await WriteManifestAsync(length: 11);
        await File.WriteAllTextAsync(EnginePath(), "hello world");

        Assert.Null(await EngineAvailability.DescribeMissingAsync(_root, CancellationToken.None));
    }

    [Fact]
    public async Task AFolderCopiedOutOfThePackageOnItsOwnIsToldWhatIsMissing()
    {
        Directory.CreateDirectory(_root);

        var failure = await EngineAvailability.DescribeMissingAsync(_root, CancellationToken.None);

        Assert.NotNull(failure);
        Assert.Contains("cannot find the backup engine", failure, StringComparison.Ordinal);
        // The likeliest cause, named, because it is also the one the person can fix in a minute.
        Assert.Contains("copy the whole", failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AManifestWithoutTheEngineBinaryBesideItIsReportedAsMissing()
    {
        await WriteManifestAsync(length: 11);

        var failure = await EngineAvailability.DescribeMissingAsync(_root, CancellationToken.None);

        Assert.NotNull(failure);
        Assert.Contains("is missing", failure, StringComparison.Ordinal);
        Assert.Contains(EnginePath(), failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEngineOfTheWrongSizeIsAnIncompleteCopyRatherThanAPresentOne()
    {
        // An archive that stopped halfway leaves a file that exists. Reporting it as present is how
        // the confusing failure arrives later, during a backup, as something about the engine.
        await WriteManifestAsync(length: 4096);
        await File.WriteAllTextAsync(EnginePath(), "short");

        var failure = await EngineAvailability.DescribeMissingAsync(_root, CancellationToken.None);

        Assert.NotNull(failure);
        Assert.Contains("not the size it should be", failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AManifestThatCannotBeReadSaysSoRatherThanThrowing()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "manifest.json"), "{ not json");

        var failure = await EngineAvailability.DescribeMissingAsync(_root, CancellationToken.None);

        Assert.NotNull(failure);
        Assert.Contains("damaged", failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AManifestPointingOutsideTheEngineFolderIsRefusedRatherThanFollowed()
    {
        // A package describing a file elsewhere on the machine as the thing to execute is refused, and
        // this asserts that it is refused rather than where. `EngineManifestReader` rejects a path with
        // ".." while parsing, so this start-up check never gets to apply its own guard - which is the
        // right order and is why that guard is a second line rather than the only one.
        await WriteManifestAsync(length: 11, relativePath: "../../windows/system32/cmd.exe");

        var failure = await EngineAvailability.DescribeMissingAsync(_root, CancellationToken.None);

        Assert.NotNull(failure);
        Assert.Contains("damaged", failure, StringComparison.Ordinal);
        Assert.DoesNotContain("cmd.exe", failure, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string EnginePath() => Path.Combine(_root, "restic", "0.19.1", "win-x64", "restic.exe");

    private async Task WriteManifestAsync(long length, string relativePath = "restic/0.19.1/win-x64/restic.exe")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(EnginePath())!);
        await File.WriteAllTextAsync(Path.Combine(_root, "manifest.json"), $$"""
            {
              "schema": "fortiq.engine-manifest",
              "version": 1,
              "engines": [
                {
                  "name": "restic",
                  "version": "0.19.1",
                  "rid": "win-x64",
                  "relativePath": "{{relativePath}}",
                  "binaryLength": {{length}},
                  "binarySha256": "0000000000000000000000000000000000000000000000000000000000000000",
                  "archiveSha256": "0000000000000000000000000000000000000000000000000000000000000000",
                  "sourceUrl": "https://example.invalid/restic.zip",
                  "license": "BSD-2-Clause",
                  "upstreamCommit": "0000000"
                }
              ]
            }
            """);
    }
}
