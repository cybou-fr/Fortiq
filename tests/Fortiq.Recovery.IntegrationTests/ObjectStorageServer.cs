using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Fortiq.Application;

namespace Fortiq.Recovery.IntegrationTests;

/// <summary>
/// A local S3 server for the storage tests, started per test and thrown away with it. Four drives,
/// because object locking needs erasure coding; a single-drive server would quietly not support the
/// thing these tests exist to check.
/// </summary>
/// <remarks>
/// The server binary is acquired by scripts/Get-TestStorage.ps1 with its SHA-256 pinned, and tests
/// skip when it is absent rather than pretending object storage was tested.
/// </remarks>
public sealed class ObjectStorageServer : IAsyncDisposable
{
    private const string AccessKey = "fortiqtestaccess";
    private const string SecretKey = "fortiqtestsecret1234";

    private readonly Process _process;
    private readonly string _root;

    private ObjectStorageServer(Process process, string root, int port)
    {
        _process = process;
        _root = root;
        Endpoint = $"http://127.0.0.1:{port}";
    }

    public string Endpoint { get; }

    public ObjectStorageCredentials Credentials { get; } = new(AccessKey, SecretKey, "us-east-1");

    public static string ServerPath
    {
        get
        {
            var manifestPath = Path.Combine(RecoveryWorkspace.RepositoryRootPath, "test-assets", "storage", "manifest.json");
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var relative = document.RootElement.GetProperty("servers")[0].GetProperty("relativePath").GetString()!;
            return Path.Combine(RecoveryWorkspace.RepositoryRootPath, "tools", relative.Replace('/', Path.DirectorySeparatorChar));
        }
    }

    public static async Task<ObjectStorageServer> StartAsync(CancellationToken cancellationToken)
    {
        Skip.IfNot(File.Exists(ServerPath), "The local S3 server is not present; run scripts/Get-TestStorage.ps1.");

        var root = Path.Combine(Path.GetTempPath(), "fortiq-s3-" + Guid.NewGuid().ToString("N"));
        for (var drive = 1; drive <= 4; drive++)
        {
            Directory.CreateDirectory(Path.Combine(root, $"d{drive}"));
        }

        var port = FreePort();
        var startInfo = new ProcessStartInfo
        {
            FileName = ServerPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        startInfo.ArgumentList.Add("server");
        for (var drive = 1; drive <= 4; drive++)
        {
            startInfo.ArgumentList.Add(Path.Combine(root, $"d{drive}"));
        }

        startInfo.ArgumentList.Add("--address");
        startInfo.ArgumentList.Add($"127.0.0.1:{port}");
        startInfo.Environment["MINIO_ROOT_USER"] = AccessKey;
        startInfo.Environment["MINIO_ROOT_PASSWORD"] = SecretKey;
        startInfo.Environment["MINIO_BROWSER"] = "off";

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start the S3 server.");
        var server = new ObjectStorageServer(process, root, port);

        await server.WaitUntilHealthyAsync(cancellationToken);
        return server;
    }

    public IAmazonS3 CreateClient() => new AmazonS3Client(
        AccessKey,
        SecretKey,
        new AmazonS3Config
        {
            ServiceURL = Endpoint,
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1"
        });

    /// <summary>Creates a bucket that keeps what is written to it for <paramref name="retention"/>.</summary>
    public async Task<string> CreateLockedBucketAsync(TimeSpan retention, CancellationToken cancellationToken)
    {
        var bucket = "fortiq-" + Guid.NewGuid().ToString("N")[..12];
        using var client = CreateClient();

        // Object locking can only be turned on when the bucket is made, which is why a repository
        // cannot be made immutable after the fact by changing a setting.
        await client.PutBucketAsync(
            new PutBucketRequest { BucketName = bucket, ObjectLockEnabledForBucket = true },
            cancellationToken);

        await client.PutObjectLockConfigurationAsync(
            new PutObjectLockConfigurationRequest
            {
                BucketName = bucket,
                ObjectLockConfiguration = new ObjectLockConfiguration
                {
                    ObjectLockEnabled = ObjectLockEnabled.Enabled,
                    Rule = new ObjectLockRule
                    {
                        DefaultRetention = new DefaultRetention
                        {
                            Mode = ObjectLockRetentionMode.Compliance,
                            Days = Math.Max(1, (int)Math.Ceiling(retention.TotalDays))
                        }
                    }
                }
            },
            cancellationToken);

        return bucket;
    }

    public async Task<string> CreatePlainBucketAsync(CancellationToken cancellationToken)
    {
        var bucket = "fortiq-" + Guid.NewGuid().ToString("N")[..12];
        using var client = CreateClient();
        await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket }, cancellationToken);
        return bucket;
    }

    /// <summary>The repository location restic is given for a bucket on this server.</summary>
    public string RepositoryLocationFor(string bucket) => $"s3:{Endpoint}/{bucket}";

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }

        _process.Dispose();

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A locked temporary file must not turn a passing test into a failure.
        }
    }

    private async Task WaitUntilHealthyAsync(CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow.AddSeconds(60);

        while (DateTime.UtcNow < deadline)
        {
            if (_process.HasExited)
            {
                throw new InvalidOperationException(
                    $"The S3 server exited during start-up: {await _process.StandardError.ReadToEndAsync(cancellationToken)}");
            }

            try
            {
                using var response = await client.GetAsync($"{Endpoint}/minio/health/live", cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }

            await Task.Delay(200, cancellationToken);
        }

        throw new TimeoutException("The S3 server did not become healthy.");
    }

    private static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
