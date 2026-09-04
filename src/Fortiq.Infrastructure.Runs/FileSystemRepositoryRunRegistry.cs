using System.Text;
using System.Text.Json;
using Fortiq.Application;
using Fortiq.Domain;

namespace Fortiq.Infrastructure.Runs;

/// <summary>
/// A run registry built on file locks: one file per repository, held open for as long as a run
/// lasts. Shared runs hold it for reading, an exclusive run holds it for writing, so the operating
/// system arbitrates between processes.
/// </summary>
/// <remarks>
/// The lock lives in the handle, not in the file's contents, which is what makes it correct across a
/// crash: a process that dies has its handles closed by the operating system, so there is no stale
/// entry to time out and no judgement call about whether an owner is still alive. The contents are
/// written for diagnostics only and are never trusted to decide whether a repository is busy.
/// </remarks>
public sealed class FileSystemRepositoryRunRegistry : IRepositoryRunRegistry
{
    private readonly string _directory;
    private readonly TimeSpan _wait;
    private readonly TimeProvider _clock;

    public FileSystemRepositoryRunRegistry(string directory, TimeSpan? wait = null, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        _wait = wait ?? TimeSpan.FromSeconds(10);
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<IRepositoryRun> BeginAsync(
        RepositoryId repository,
        OperationKind operation,
        Guid operationId,
        RunExclusivity exclusivity,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, $"{repository.ToString().ToLowerInvariant()}.run");
        var deadline = _clock.GetUtcNow() + _wait;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileRun(Open(path, exclusivity), operationId, exclusivity, Describe(operation, operationId));
            }
            catch (IOException) when (_clock.GetUtcNow() < deadline)
            {
                // Another run holds the repository. Waiting a moment is normal; waiting forever would
                // turn a busy repository into a hung process.
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            }
            catch (IOException error)
            {
                throw new RepositoryBusyException(
                    exclusivity == RunExclusivity.Exclusive
                        ? "This operation needs the repository to itself, and another Fortiq run is using it."
                        : "Another Fortiq run holds this repository exclusively.",
                    error);
            }
        }
    }

    private static FileStream Open(string path, RunExclusivity exclusivity) => exclusivity switch
    {
        // A shared run tolerates other shared runs and blocks an exclusive one; an exclusive run
        // tolerates nothing.
        RunExclusivity.Shared => new FileStream(path, FileMode.OpenOrCreate, FileAccess.Read, FileShare.Read),
        RunExclusivity.Exclusive => new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None),
        _ => throw new ArgumentOutOfRangeException(nameof(exclusivity))
    };

    private string Describe(OperationKind operation, Guid operationId) => JsonSerializer.Serialize(new
    {
        schema = "fortiq.repository-run",
        version = 1,
        operationId,
        operation = operation.ToString().ToLowerInvariant(),
        processId = Environment.ProcessId,
        machine = Environment.MachineName,
        startedAt = _clock.GetUtcNow()
    });

    private sealed class FileRun : IRepositoryRun
    {
        private readonly FileStream _handle;

        internal FileRun(FileStream handle, Guid operationId, RunExclusivity exclusivity, string description)
        {
            _handle = handle;
            OperationId = operationId;
            Exclusivity = exclusivity;

            if (handle.CanWrite)
            {
                // Only an exclusive holder may write: a shared holder has the file open for reading.
                var bytes = Encoding.UTF8.GetBytes(description);
                handle.SetLength(0);
                handle.Write(bytes);
                handle.Flush();
            }
        }

        public Guid OperationId { get; }

        public RunExclusivity Exclusivity { get; }

        public ValueTask DisposeAsync()
        {
            _handle.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
