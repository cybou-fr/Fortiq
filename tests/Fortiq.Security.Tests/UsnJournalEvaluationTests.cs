using System.Runtime.InteropServices;
using System.Text;
using Fortiq.Domain;
using Fortiq.Platform.Windows;

namespace Fortiq.Security.Tests;

public sealed class UsnJournalEvaluationTests
{
    [Fact]
    public void BinaryRecordParsingDecodesUsnRecordV2Accurately()
    {
        const string fileName = "finance-2026.xlsx";
        var nameBytes = Encoding.Unicode.GetBytes(fileName);
        var recordLength = (uint)(Marshal.SizeOf<NtfsUsnRecordParser.UsnRecordHeader>() + nameBytes.Length);
        var totalLength = sizeof(long) + (int)recordLength;

        var buffer = new byte[totalLength];
        var expectedNextUsn = 99998888L;
        MemoryMarshal.Write(buffer.AsSpan(0, sizeof(long)), in expectedNextUsn);

        var now = DateTimeOffset.UtcNow;
        var header = new NtfsUsnRecordParser.UsnRecordHeader
        {
            RecordLength = recordLength,
            MajorVersion = 2,
            MinorVersion = 0,
            FileReferenceNumber = 123456789UL,
            ParentFileReferenceNumber = 987654321UL,
            Usn = 50000000L,
            TimeStamp = now.ToFileTime(),
            Reason = (uint)(UsnChangeReasons.FileCreate | UsnChangeReasons.DataExtend | UsnChangeReasons.Close),
            SourceInfo = 0,
            SecurityId = 0,
            FileAttributes = 0x20, // FILE_ATTRIBUTE_ARCHIVE
            FileNameLength = (ushort)nameBytes.Length,
            FileNameOffset = (ushort)Marshal.SizeOf<NtfsUsnRecordParser.UsnRecordHeader>()
        };

        MemoryMarshal.Write(buffer.AsSpan(sizeof(long), Marshal.SizeOf<NtfsUsnRecordParser.UsnRecordHeader>()), in header);
        nameBytes.CopyTo(buffer, sizeof(long) + header.FileNameOffset);

        var entries = NtfsUsnRecordParser.ParseRecords(buffer, out var nextUsn);

        Assert.Equal(expectedNextUsn, nextUsn);
        var entry = Assert.Single(entries);
        Assert.Equal(fileName, entry.FileName);
        Assert.Equal(123456789UL, entry.FileReferenceNumber);
        Assert.Equal(987654321UL, entry.ParentReferenceNumber);
        Assert.Equal(50000000L, entry.Usn);
        Assert.True(entry.IsCreated);
        Assert.True(entry.IsDataModified);
        Assert.True(entry.IsClosed);
        Assert.False(entry.IsDeleted);
        Assert.False(entry.IsRenamed);
    }

    [Fact]
    public void UnsupportedFilesystemTriggersDeterministicFullScanFallback()
    {
        var stub = new StubUsnReader { Supported = false };
        var result = UsnChangeEvaluator.Evaluate("D:\\Data", null, stub);

        Assert.True(result.FullScanRequired);
        Assert.Equal(UsnFallbackReason.UnsupportedFilesystem, result.FallbackReason);
        Assert.Empty(result.Changes);
    }

    [Fact]
    public void InitialBaselineRequiresFullScanAndSetsCheckpoint()
    {
        var stub = new StubUsnReader
        {
            Supported = true,
            JournalInfo = new UsnJournalInfo(12345UL, 67890UL, 0, 10000L, 0)
        };

        var result = UsnChangeEvaluator.Evaluate("C:\\", null, stub);

        Assert.True(result.FullScanRequired);
        Assert.Equal(UsnFallbackReason.InitialBaseline, result.FallbackReason);
        Assert.Equal(12345UL, result.UpdatedCheckpoint.VolumeSerial);
        Assert.Equal(67890UL, result.UpdatedCheckpoint.JournalId);
        Assert.Equal(10000L, result.UpdatedCheckpoint.NextUsn);
        Assert.Empty(result.Changes);
    }

    [Fact]
    public void VolumeMismatchOrRecreatedJournalTriggersFullScanFallback()
    {
        var prior = new UsnCheckpoint("C:\\", 1111UL, 2222UL, 5000L, DateTimeOffset.UtcNow);

        var mismatchStub = new StubUsnReader
        {
            Supported = true,
            JournalInfo = new UsnJournalInfo(9999UL, 2222UL, 0, 6000L, 0)
        };
        var mismatchResult = UsnChangeEvaluator.Evaluate("C:\\", prior, mismatchStub);
        Assert.True(mismatchResult.FullScanRequired);
        Assert.Equal(UsnFallbackReason.VolumeMismatch, mismatchResult.FallbackReason);

        var recreatedStub = new StubUsnReader
        {
            Supported = true,
            JournalInfo = new UsnJournalInfo(1111UL, 8888UL, 0, 6000L, 0)
        };
        var recreatedResult = UsnChangeEvaluator.Evaluate("C:\\", prior, recreatedStub);
        Assert.True(recreatedResult.FullScanRequired);
        Assert.Equal(UsnFallbackReason.JournalRecreated, recreatedResult.FallbackReason);
    }

    [Fact]
    public void TruncatedJournalTriggersFullScanFallback()
    {
        var prior = new UsnCheckpoint("C:\\", 1111UL, 2222UL, 5000L, DateTimeOffset.UtcNow);

        var truncatedStub = new StubUsnReader
        {
            Supported = true,
            JournalInfo = new UsnJournalInfo(1111UL, 2222UL, 0, 10000L, 7000L) // LowestValidUsn is 7000 > prior 5000
        };

        var result = UsnChangeEvaluator.Evaluate("C:\\", prior, truncatedStub);
        Assert.True(result.FullScanRequired);
        Assert.Equal(UsnFallbackReason.JournalTruncated, result.FallbackReason);
    }

    [Fact]
    public void SequentialContinuousJournalReadsChangesAndReportsMetrics()
    {
        var prior = new UsnCheckpoint("C:\\", 1111UL, 2222UL, 5000L, DateTimeOffset.UtcNow);

        var stub = new StubUsnReader
        {
            Supported = true,
            JournalInfo = new UsnJournalInfo(1111UL, 2222UL, 0, 8000L, 1000L),
            ChangesToReturn =
            [
                new UsnChangeEntry(1, 0, 5100, "created.txt", UsnChangeReasons.FileCreate | UsnChangeReasons.Close, DateTimeOffset.UtcNow),
                new UsnChangeEntry(2, 0, 5200, "modified.docx", UsnChangeReasons.DataExtend | UsnChangeReasons.Close, DateTimeOffset.UtcNow),
                new UsnChangeEntry(3, 0, 5300, "deleted.bin", UsnChangeReasons.FileDelete | UsnChangeReasons.Close, DateTimeOffset.UtcNow),
                new UsnChangeEntry(4, 0, 5400, "renamed.pdf", UsnChangeReasons.RenameNewName | UsnChangeReasons.Close, DateTimeOffset.UtcNow)
            ]
        };

        var result = UsnChangeEvaluator.Evaluate("C:\\", prior, stub);

        Assert.False(result.FullScanRequired);
        Assert.Equal(UsnFallbackReason.None, result.FallbackReason);
        Assert.Equal(8000L, result.UpdatedCheckpoint.NextUsn);
        Assert.Equal(4, result.TotalChanges);
        Assert.Equal(1, result.CreatedCount);
        Assert.Equal(1, result.ModifiedCount);
        Assert.Equal(1, result.DeletedCount);
        Assert.Equal(1, result.RenamedCount);
        Assert.False(result.AnomalyVerdict.IsAnomalyDetected);
    }

    [Fact]
    public void RansomwareHeuristicDetectsMassiveRenameAndExtensionBursts()
    {
        var prior = new UsnCheckpoint("C:\\", 1111UL, 2222UL, 1000L, DateTimeOffset.UtcNow);

        var changes = new List<UsnChangeEntry>();
        for (int i = 0; i < 25; i++)
        {
            changes.Add(new UsnChangeEntry(
                (ulong)i, 0, 1000 + i, $"document_{i}.docx.locked",
                UsnChangeReasons.RenameNewName | UsnChangeReasons.DataOverwrite | UsnChangeReasons.Close,
                DateTimeOffset.UtcNow));
        }

        var stub = new StubUsnReader
        {
            Supported = true,
            JournalInfo = new UsnJournalInfo(1111UL, 2222UL, 0, 2000L, 500L),
            ChangesToReturn = changes
        };

        var options = new UsnEvaluationOptions(AnomalySuspiciousExtensionThreshold: 20);
        var result = UsnChangeEvaluator.Evaluate("C:\\", prior, stub, options);

        Assert.False(result.FullScanRequired);
        Assert.True(result.AnomalyVerdict.IsAnomalyDetected);
        Assert.Equal(25, result.AnomalyVerdict.RapidExtensionChangeCount);
        Assert.Contains("ransomware", result.AnomalyVerdict.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StratifiedSamplingSelectsEligibleCandidatesForRestoreDrill()
    {
        var prior = new UsnCheckpoint("C:\\", 1111UL, 2222UL, 1000L, DateTimeOffset.UtcNow);

        var stub = new StubUsnReader
        {
            Supported = true,
            JournalInfo = new UsnJournalInfo(1111UL, 2222UL, 0, 2000L, 500L),
            ChangesToReturn =
            [
                new UsnChangeEntry(1, 0, 1100, "~tempfile.tmp", UsnChangeReasons.FileCreate | UsnChangeReasons.Close, DateTimeOffset.UtcNow),
                new UsnChangeEntry(2, 0, 1200, "engine.log", UsnChangeReasons.DataExtend | UsnChangeReasons.Close, DateTimeOffset.UtcNow),
                new UsnChangeEntry(3, 0, 1300, "payroll.xlsx", UsnChangeReasons.DataOverwrite | UsnChangeReasons.Close, DateTimeOffset.UtcNow),
                new UsnChangeEntry(4, 0, 1400, "database.sqlite", UsnChangeReasons.FileCreate | UsnChangeReasons.Close, DateTimeOffset.UtcNow)
            ]
        };

        var result = UsnChangeEvaluator.Evaluate("C:\\", prior, stub, new UsnEvaluationOptions(RestoreDrillSampleSize: 5));

        Assert.Equal(2, result.CandidateDrillSample.Count);
        Assert.Contains("payroll.xlsx", result.CandidateDrillSample);
        Assert.Contains("database.sqlite", result.CandidateDrillSample);
        Assert.DoesNotContain("~tempfile.tmp", result.CandidateDrillSample);
        Assert.DoesNotContain("engine.log", result.CandidateDrillSample);
    }

    private sealed class StubUsnReader : IUsnJournalReader
    {
        public bool Supported { get; set; } = true;
        public UsnJournalInfo JournalInfo { get; set; } = new(1, 1, 0, 100, 0);
        public IReadOnlyList<UsnChangeEntry> ChangesToReturn { get; set; } = [];

        public bool IsSupported(string volumePath) => Supported;

        public UsnJournalInfo QueryJournal(string volumePath) => JournalInfo;

        public IReadOnlyList<UsnChangeEntry> ReadChanges(string volumePath, ulong journalId, long startUsn, long maxRecords = 10000) =>
            ChangesToReturn;
    }
}
