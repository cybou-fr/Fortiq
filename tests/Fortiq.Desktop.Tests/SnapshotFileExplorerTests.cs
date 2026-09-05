using Fortiq.Desktop.ViewModels;

namespace Fortiq.Desktop.Tests;

public sealed class SnapshotFileExplorerTests
{
    private static readonly RecoverySnapshot Snapshot = new("snap-123", DateTimeOffset.UtcNow, "C:/source");
    private static readonly FileRecoveryAccess Access = new("C:/repo", "C:/kit", "phrase", "access", "secret");

    [Fact]
    public void SnapshotFileItemFormatsSizesAndIdentifiesDirectories()
    {
        var dir = new SnapshotFileItem("Docs", "/C/Docs", "dir", 0, null);
        Assert.True(dir.IsDirectory);
        Assert.Equal("<DIR>", dir.FormattedSize);
        Assert.Equal("Docs/", dir.DisplayName);

        var smallFile = new SnapshotFileItem("note.txt", "/C/Docs/note.txt", "file", 500, null);
        Assert.False(smallFile.IsDirectory);
        Assert.Equal("500 B", smallFile.FormattedSize);

        var kbFile = new SnapshotFileItem("doc.docx", "/C/Docs/doc.docx", "file", 2048, null);
        Assert.Equal("2.0 KB", kbFile.FormattedSize);

        var mbFile = new SnapshotFileItem("video.mp4", "/C/Docs/video.mp4", "file", 10485760, null);
        Assert.Equal("10.0 MB", mbFile.FormattedSize);

        var gbFile = new SnapshotFileItem("archive.zip", "/C/Docs/archive.zip", "file", 2147483648, null);
        Assert.Equal("2.0 GB", gbFile.FormattedSize);
    }

    [Fact]
    public async Task LoadsFilesWhenRequestedForSnapshot()
    {
        var stub = new StubFileRecovery
        {
            Files =
            [
                new SnapshotFileItem("folder", "/C/folder", "dir", 0, null),
                new SnapshotFileItem("file.txt", "/C/folder/file.txt", "file", 100, null)
            ]
        };

        var model = new FileRecoveryViewModel(stub);
        await model.LoadAsync(Access);
        await model.LoadFilesAsync(Snapshot);

        Assert.Equal(2, model.Files.Count);
        Assert.Equal(2, model.FilteredFiles.Count);
        Assert.False(model.FilesLoading);
    }

    [Fact]
    public async Task SearchFiltersFilesByNameAndPathCaseInsensitively()
    {
        var stub = new StubFileRecovery
        {
            Files =
            [
                new SnapshotFileItem("Work", "/C/Work", "dir", 0, null),
                new SnapshotFileItem("financial-report.xlsx", "/C/Work/financial-report.xlsx", "file", 2048, null),
                new SnapshotFileItem("photo.jpg", "/C/Personal/photo.jpg", "file", 4096, null)
            ]
        };

        var model = new FileRecoveryViewModel(stub);
        await model.LoadAsync(Access);
        await model.LoadFilesAsync(Snapshot);

        model.SetSearchQuery("report");
        Assert.Single(model.FilteredFiles);
        Assert.Equal("financial-report.xlsx", model.FilteredFiles[0].DisplayName);

        model.SetSearchQuery("PERSONAL");
        Assert.Single(model.FilteredFiles);
        Assert.Equal("photo.jpg", model.FilteredFiles[0].DisplayName);

        model.SetSearchQuery(string.Empty);
        Assert.Equal(3, model.FilteredFiles.Count);
    }

    [Fact]
    public async Task RestoresSpecificFileWhenSelectiveRestoreIsChosen()
    {
        var stub = new StubFileRecovery
        {
            Files =
            [
                new SnapshotFileItem("target.txt", "/C/data/target.txt", "file", 123, null)
            ]
        };

        var model = new FileRecoveryViewModel(stub);
        await model.LoadAsync(Access);
        await model.LoadFilesAsync(Snapshot);

        model.RestoreSpecificItem = true;
        model.SelectedFile = model.Files[0];

        await model.RestoreAsync(Snapshot, "C:/restored-target");

        Assert.Equal("C:/restored-target", stub.LastTarget);
        Assert.Equal("/C/data/target.txt", stub.LastSpecificPath);
        Assert.True(model.Completed);
    }

    [Fact]
    public async Task RestoresEntireSnapshotWhenSelectiveRestoreIsFalse()
    {
        var stub = new StubFileRecovery
        {
            Files =
            [
                new SnapshotFileItem("target.txt", "/C/data/target.txt", "file", 123, null)
            ]
        };

        var model = new FileRecoveryViewModel(stub);
        await model.LoadAsync(Access);
        await model.LoadFilesAsync(Snapshot);

        model.RestoreSpecificItem = false;
        model.SelectedFile = model.Files[0];

        await model.RestoreAsync(Snapshot, "C:/restored-all");

        Assert.Equal("C:/restored-all", stub.LastTarget);
        Assert.Null(stub.LastSpecificPath);
        Assert.True(model.Completed);
    }

    private sealed class StubFileRecovery : IFileRecovery
    {
        public IReadOnlyList<SnapshotFileItem> Files { get; set; } = [];
        public string? LastTarget { get; private set; }
        public string? LastSpecificPath { get; private set; }

        public Task<IReadOnlyList<RecoverySnapshot>> ListAsync(FileRecoveryAccess access, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<RecoverySnapshot>>([Snapshot]);

        public Task<IReadOnlyList<SnapshotFileItem>> ListFilesAsync(FileRecoveryAccess access, RecoverySnapshot snapshot, CancellationToken token) =>
            Task.FromResult(Files);

        public Task<FileRecoveryResult> RestoreAsync(
            FileRecoveryAccess access,
            RecoverySnapshot snapshot,
            string target,
            string? specificPath,
            CancellationToken token = default)
        {
            LastTarget = target;
            LastSpecificPath = specificPath;
            return Task.FromResult(new FileRecoveryResult(target, 42));
        }
    }
}
