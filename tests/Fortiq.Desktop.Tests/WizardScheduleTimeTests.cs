using Fortiq.Desktop.ViewModels;

namespace Fortiq.Desktop.Tests;

/// <summary>
/// Choosing when a folder is backed up, in the wizard that sets it up.
/// </summary>
/// <remarks>
/// The schedule step stated 02:30 as a fact and added that custom scheduling was not available in
/// this interface - while the source's own settings screen offered exactly that a minute after setup
/// finished. It was never fixed; nothing had asked.
/// </remarks>
public sealed class WizardScheduleTimeTests
{
    [Fact]
    public async Task TheTimeSomebodyChoseIsWhatGetsAskedFor()
    {
        var creator = new RecordingCreator();
        var model = Wizard(creator);
        model.BackupHour = 21;
        model.BackupMinute = 45;

        await model.CreateAsync(CancellationToken.None);

        Assert.Equal((21 * 60) + 45, creator.Request!.BackupMinuteOfDay);
    }

    [Fact]
    public async Task AWizardNobodyTouchedStillAsksForTheHourItAlwaysUsed()
    {
        var creator = new RecordingCreator();

        await Wizard(creator).CreateAsync(CancellationToken.None);

        // 02:30, the hour provisioning has always written, so a person who changes nothing gets what
        // every earlier version gave them.
        Assert.Equal(150, creator.Request!.BackupMinuteOfDay);
        Assert.Equal(150, new ProtectRepositoryRequest("r", "k", "s").BackupMinuteOfDay);
    }

    [Theory]
    [InlineData(-4, 0)]
    [InlineData(99, 23)]
    public void AnHourOutsideADayIsBroughtBackIntoOne(int given, int expected)
    {
        var model = Wizard(new RecordingCreator());

        model.BackupHour = given;

        Assert.Equal(expected, model.BackupHour);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(120, 59)]
    public void AMinuteOutsideAnHourIsBroughtBackIntoOne(int given, int expected)
    {
        var model = Wizard(new RecordingCreator());

        model.BackupMinute = given;

        Assert.Equal(expected, model.BackupMinute);
    }

    private static ProtectRepositoryViewModel Wizard(IProtectRepository creator) => new(creator)
    {
        RepositoryLocation = @"D:\Fortiq\repository",
        KitDirectory = @"D:\Fortiq\kit",
        SourcePath = @"C:\Users\anna\Documents"
    };

    private sealed class RecordingCreator : IProtectRepository
    {
        internal ProtectRepositoryRequest? Request { get; private set; }

        public Task<ProtectedRepositoryResult> CreateAsync(ProtectRepositoryRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new ProtectedRepositoryResult("repo-1", "word ".Repeat(24).Trim(), true, true));
        }

        public Task ConfirmRecoveryPhraseAsync(string repositoryId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

internal static class RepeatExtensions
{
    internal static string Repeat(this string value, int times) => string.Concat(Enumerable.Repeat(value, times));
}
