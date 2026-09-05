namespace Fortiq.Domain;

/// <summary>
/// Bit flags indicating what changes occurred to a file in the NTFS USN Change Journal.
/// Matches Win32 USN_REASON_* constants.
/// </summary>
[Flags]
public enum UsnChangeReasons : uint
{
    None = 0,
    DataOverwrite = 0x00000001,
    DataExtend = 0x00000002,
    DataTruncation = 0x00000004,
    NamedDataOverwrite = 0x00000010,
    NamedDataExtend = 0x00000020,
    NamedDataTruncation = 0x00000040,
    FileCreate = 0x00000100,
    FileDelete = 0x00000200,
    PropertyChange = 0x00000400,
    SecurityChange = 0x00000800,
    RenameOldName = 0x00001000,
    RenameNewName = 0x00002000,
    IndexableChange = 0x00004000,
    BasicInfoChange = 0x00008000,
    HardLinkChange = 0x00010000,
    CompressionChange = 0x00020000,
    EncryptionChange = 0x00040000,
    ObjectIdChange = 0x00080000,
    ReparsePointChange = 0x00100000,
    StreamChange = 0x00200000,
    TransactedChange = 0x00400000,
    IntegrityChange = 0x00800000,
    Close = 0x80000000
}

/// <summary>
/// Represents a single change event recorded in the NTFS USN Change Journal.
/// </summary>
public sealed record UsnChangeEntry(
    ulong FileReferenceNumber,
    ulong ParentReferenceNumber,
    long Usn,
    string FileName,
    UsnChangeReasons Reasons,
    DateTimeOffset Timestamp)
{
    public bool IsDataModified => (Reasons & (UsnChangeReasons.DataOverwrite | UsnChangeReasons.DataExtend | UsnChangeReasons.DataTruncation)) != 0;
    public bool IsCreated => (Reasons & UsnChangeReasons.FileCreate) != 0;
    public bool IsDeleted => (Reasons & UsnChangeReasons.FileDelete) != 0;
    public bool IsRenamed => (Reasons & (UsnChangeReasons.RenameOldName | UsnChangeReasons.RenameNewName)) != 0;
    public bool IsClosed => (Reasons & UsnChangeReasons.Close) != 0;
}

/// <summary>
/// Tracks the state of the USN Change Journal for a volume at a specific snapshot point.
/// </summary>
public sealed record UsnCheckpoint(
    string VolumePath,
    ulong VolumeSerial,
    ulong JournalId,
    long NextUsn,
    DateTimeOffset RecordedAt);

/// <summary>
/// Reason why Fortiq must perform a full scan instead of relying on incremental USN evaluation.
/// </summary>
public enum UsnFallbackReason
{
    None = 0,
    InitialBaseline = 1,
    JournalTruncated = 2,
    JournalRecreated = 3,
    VolumeMismatch = 4,
    AccessDenied = 5,
    UnsupportedFilesystem = 6,
    ReadError = 7
}

/// <summary>
/// Anomaly verdict detecting potential ransomware bursts or unusual filesystem dynamics.
/// </summary>
public sealed record UsnAnomalyVerdict(
    bool IsAnomalyDetected,
    int RenameBurstCount,
    int MassDeletionCount,
    int RapidExtensionChangeCount,
    string? Explanation = null);

/// <summary>
/// Result of evaluating the USN Change Journal between checkpoints.
/// </summary>
public sealed record UsnChangeEvaluationResult(
    bool FullScanRequired,
    UsnFallbackReason FallbackReason,
    UsnCheckpoint UpdatedCheckpoint,
    IReadOnlyList<UsnChangeEntry> Changes,
    UsnAnomalyVerdict AnomalyVerdict,
    IReadOnlyList<string> CandidateDrillSample)
{
    public int TotalChanges => Changes.Count;
    public int CreatedCount => Changes.Count(c => c.IsCreated);
    public int DeletedCount => Changes.Count(c => c.IsDeleted);
    public int ModifiedCount => Changes.Count(c => c.IsDataModified);
    public int RenamedCount => Changes.Count(c => c.IsRenamed);
}
