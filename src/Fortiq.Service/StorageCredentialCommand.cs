using System.Runtime.Versioning;
using Fortiq.Application;
using Fortiq.Infrastructure.ObjectStorage;

namespace Fortiq.Service;

/// <summary>
/// Manages the storage credentials a machine holds, from the same binary that uses them.
/// </summary>
/// <remarks>
/// A credential store nobody can write to is not a credential store, and this is the smallest thing
/// that makes it usable without an installer. The secret key is read from standard input rather than
/// taken as an argument: a command line is visible to every process on the machine through the
/// process list, and it survives in shell history.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class StorageCredentialCommand
{
    public const string Usage = """
        Fortiq.Service credentials set    --repository <s3:https://host/bucket> --access-key <id> [--region <region>]
        Fortiq.Service credentials remove --repository <s3:https://host/bucket>
        Fortiq.Service credentials list

        The secret key is read from standard input, never from the command line.
        """;

    public static async Task<int> RunAsync(string[] args, FortiqStatePaths paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(paths);

        var credentials = new StoredObjectStorageCredentials(Path.Combine(paths.Root, "credentials"));
        var verb = args.Length > 1 ? args[1] : string.Empty;

        switch (verb)
        {
            case "set":
                return await SetAsync(args, credentials, cancellationToken);

            case "remove":
                if (Option(args, "--repository") is not { Length: > 0 } removing)
                {
                    return Fail("A repository is required.");
                }

                Console.WriteLine(
                    credentials.Remove(removing)
                        ? "Removed."
                        : "There were no stored credentials for that repository.");
                return 0;

            case "list":
                // Which repositories have credentials, never the credentials themselves.
                foreach (var subject in new Fortiq.Platform.Windows.MachineCredentialStore(
                    Path.Combine(paths.Root, "credentials")).Subjects())
                {
                    Console.WriteLine(subject);
                }

                return 0;

            default:
                return Fail("Unknown credentials command.");
        }
    }

    private static async Task<int> SetAsync(
        string[] args,
        StoredObjectStorageCredentials credentials,
        CancellationToken cancellationToken)
    {
        if (Option(args, "--repository") is not { Length: > 0 } repository)
        {
            return Fail("A repository is required.");
        }

        if (Option(args, "--access-key") is not { Length: > 0 } accessKey)
        {
            return Fail("An access key is required.");
        }

        if (Console.IsInputRedirected is false)
        {
            await Console.Error.WriteLineAsync("Provide the secret key on standard input, for example:");
            await Console.Error.WriteLineAsync(
                "  Get-Content secret.txt | Fortiq.Service credentials set --repository <repo> --access-key <id>");
            return 2;
        }

        var secret = (await Console.In.ReadToEndAsync(cancellationToken)).Trim();
        if (secret.Length == 0)
        {
            return Fail("The secret key on standard input was empty.");
        }

        await credentials.WriteAsync(
            repository,
            new ObjectStorageCredentials(accessKey, secret, Option(args, "--region")),
            cancellationToken);

        // Says what was stored and for which repository, and never echoes the secret.
        Console.WriteLine($"Stored credentials for {repository}.");
        return 0;
    }

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine();
        Console.Error.WriteLine(Usage);
        return 2;
    }
}
