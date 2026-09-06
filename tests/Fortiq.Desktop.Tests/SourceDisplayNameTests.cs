using Fortiq.Desktop.ViewModels;
using Fortiq.Monitoring;

namespace Fortiq.Desktop.Tests;

/// <summary>
/// What a protected source is called on screen.
/// </summary>
/// <remarks>
/// It was the schedule id, which provisioning sets to the repository id - a UUID. Every screen showed
/// it: the dashboard, the source list, the recovery selector, the settings title, and the row's own
/// second line, so somebody saw the same UUID twice and the folder they chose nowhere at all.
/// </remarks>
public sealed class SourceDisplayNameTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void ASourceIsCalledByItsFolder()
    {
        var row = new RepositoryRowViewModel(Health(@"C:\Users\anna\Documents"));

        Assert.Equal(Path.Combine("anna", "Documents"), row.Title);
        Assert.Equal(@"C:\Users\anna\Documents", row.SourcePath);
    }

    [Fact]
    public void TwoFoldersOfTheSameNameAreStillTellableApart()
    {
        // The list's whole job is to say which is which, and "Documents" twice does not.
        var user = new RepositoryRowViewModel(Health(@"C:\Users\anna\Documents"));
        var disk = new RepositoryRowViewModel(Health(@"D:\Archive\Documents"));

        Assert.NotEqual(user.Title, disk.Title);
    }

    [Fact]
    public void ATrailingSeparatorDoesNotEatTheName()
    {
        Assert.Equal(Path.Combine("anna", "Documents"), new RepositoryRowViewModel(Health(@"C:\Users\anna\Documents\")).Title);
    }

    [Fact]
    public void AFolderAtTheRootOfADriveKeepsItsOwnName()
    {
        Assert.Equal("Photos", new RepositoryRowViewModel(Health(@"D:\Photos")).Title);
    }

    [Fact]
    public void AReportWithoutASourcePathKeepsTheAnswerItUsedToGive()
    {
        // Every report written before the path was carried. Showing nothing would be worse than
        // showing the identifier those installations have always shown.
        var row = new RepositoryRowViewModel(Health(sourcePath: null));

        Assert.Equal("documents", row.Title);
        Assert.Null(row.SourcePath);
        Assert.False(row.HasSourcePath);
    }

    [Fact]
    public void AnEmptySourcePathIsTreatedAsNoneRatherThanAsAName()
    {
        var row = new RepositoryRowViewModel(Health(sourcePath: string.Empty));

        Assert.Equal("documents", row.Title);
        Assert.Null(row.SourcePath);
    }

    private static RepositoryHealth Health(string? sourcePath) => HealthAssessor.Assess(
        new RepositoryFacts(
            "90669b27-2f4d-4c1e-9a55-2b7f0f6f1a33",
            "documents",
            LastBackupAt: Now.AddHours(-1),
            LastHealthyCheckAt: Now.AddDays(-1),
            LastProvenRestoreAt: Now.AddDays(-2),
            KitPresent: true,
            StorageImmutable: true,
            StorageProtectionNow: StorageProtectionStatus.Immutable,
            SourcePath: sourcePath),
        Now);
}
