using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Fortiq.Assistant;

/// <summary>How the assistant's process is started, and how patient callers should be with it.</summary>
/// <param name="ServerPath">The pinned llama-server executable.</param>
/// <param name="ModelPath">The pinned model file.</param>
/// <param name="ContextTokens">The context window, from the model manifest.</param>
/// <param name="Threads">CPU threads. Null leaves the decision to llama.cpp.</param>
/// <param name="StartupTimeout">How long loading a gigabyte of weights may take before giving up.</param>
/// <param name="ReplyTimeout">How long one answer may take.</param>
/// <param name="MaxReplyTokens">The ceiling on one answer.</param>
/// <param name="Structured">
/// Whether the model is held to the response schema. On by default, because a screen can only treat
/// a recorded fact differently from an opinion if the answer says which is which.
/// </param>
public sealed record AssistantRuntimeOptions(
    string ServerPath,
    string ModelPath,
    int ContextTokens,
    int? Threads = null,
    TimeSpan? StartupTimeout = null,
    TimeSpan? ReplyTimeout = null,
    int MaxReplyTokens = 512,
    bool Structured = true)
{
    public TimeSpan ResolvedStartupTimeout => StartupTimeout ?? TimeSpan.FromMinutes(2);

    public TimeSpan ResolvedReplyTimeout => ReplyTimeout ?? TimeSpan.FromMinutes(2);
}

/// <summary>
/// Runs the assistant's model in a llama-server process of its own, on the loopback interface.
/// </summary>
/// <remarks>
/// A separate process rather than a library inside the desktop, for the reason ADR-003 gives the
/// assistant its own row in the process-boundary table: it is the component that reads text written
/// by whoever created the files being backed up, and it is the one whose failure should cost the
/// least. A model that runs out of memory, loops, or is talked into misbehaving takes down a child
/// process Fortiq can restart, not the window somebody is using to get their data back.
///
/// It binds 127.0.0.1 and additionally requires an API key generated for this one run. The loopback
/// bind is what keeps it off the network; the key is what keeps other local processes - which share
/// loopback - from using somebody's assistant as their own free inference server.
///
/// The process is killed on disposal, tree and all. An orphaned llama-server holding a gigabyte of
/// weights and a port is not something a person would connect to Fortiq, or know how to find.
/// </remarks>
public sealed class LlamaServerRuntime : IAssistantRuntime
{
    private readonly AssistantRuntimeOptions _options;
    private readonly HttpClient _client;
    private readonly Process _process;
    private readonly Uri _endpoint;
    private readonly StringBuilder _errors = new();

    private LlamaServerRuntime(AssistantRuntimeOptions options, Process process, Uri endpoint, string apiKey)
    {
        _options = options;
        _process = process;
        _endpoint = endpoint;

        // Timeouts are per call, from the options, so that starting and answering can differ.
        _client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public bool IsReady { get; private set; }

    /// <summary>The child's process id, so a test can confirm disposal actually ended it.</summary>
    internal int ProcessId => _process.Id;

    /// <summary>Starts the runtime and waits until it will answer, or gives up saying why.</summary>
    public static async Task<LlamaServerRuntime> StartAsync(AssistantRuntimeOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ServerPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ModelPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.ContextTokens);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxReplyTokens);

        if (!File.Exists(options.ServerPath))
        {
            throw new FileNotFoundException(
                $"Fortiq's assistant runtime is missing. It should be at '{options.ServerPath}'. "
                + "Install the Fortiq release again, or run scripts/Get-Runtime.ps1.",
                options.ServerPath);
        }

        if (!File.Exists(options.ModelPath))
        {
            throw new FileNotFoundException(
                $"Fortiq's assistant model is missing. It should be at '{options.ModelPath}'.",
                options.ModelPath);
        }

        var port = ReserveLoopbackPort();
        var apiKey = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var endpoint = new Uri($"http://127.0.0.1:{port}/");

        var process = new Process { StartInfo = CreateStartInfo(options, port, apiKey) };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Fortiq could not start the assistant runtime.");
        }

        var runtime = new LlamaServerRuntime(options, process, endpoint, apiKey);
        runtime.DrainOutput();

        try
        {
            await runtime.WaitUntilReadyAsync(cancellationToken);
            return runtime;
        }
        catch
        {
            await runtime.DisposeAsync();
            throw;
        }
    }

    public async Task<AssistantReply> AskAsync(AssistantAsk ask, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ask);

        if (!IsReady || _process.HasExited)
        {
            throw new InvalidOperationException(
                _process.HasExited
                    ? "The assistant runtime is not running."
                    : "The assistant is still starting.");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.ResolvedReplyTimeout);

        using var content = new StringContent(
            LlamaChatProtocol.BuildRequest(ask, _options.MaxReplyTokens, _options.Structured),
            Encoding.UTF8,
            "application/json");

        try
        {
            using var response = await _client.PostAsync(new Uri(_endpoint, "v1/chat/completions"), content, deadline.Token);
            var body = await response.Content.ReadAsStringAsync(deadline.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidDataException($"The assistant runtime answered with {(int)response.StatusCode}. {Tail(body)}");
            }

            return LlamaChatProtocol.ReadReply(body);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The caller did not cancel, so this is our own deadline, and a timeout is what it is.
            throw new TimeoutException(
                $"The assistant did not answer within {_options.ResolvedReplyTimeout.TotalSeconds:N0} seconds.");
        }
    }

    private static ProcessStartInfo CreateStartInfo(AssistantRuntimeOptions options, int port, string apiKey)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = options.ServerPath,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(options.ServerPath))!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in new[]
                 {
                     "--model", options.ModelPath,
                     "--host", "127.0.0.1",
                     "--port", port.ToString(CultureInfo.InvariantCulture),
                     "--ctx-size", options.ContextTokens.ToString(CultureInfo.InvariantCulture),
                     "--api-key", apiKey,
                     // Nothing about this process is meant for a person to open in a browser.
                     "--no-webui"
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (options.Threads is { } threads)
        {
            startInfo.ArgumentList.Add("--threads");
            startInfo.ArgumentList.Add(threads.ToString(CultureInfo.InvariantCulture));
        }

        return startInfo;
    }

    /// <summary>
    /// Reads the child's output so that it never blocks, and keeps the last of it for diagnosis.
    /// </summary>
    /// <remarks>
    /// A child process whose output nobody reads eventually fills its pipe and stops. llama-server
    /// is talkative while it loads a model, so this is not a theoretical amount of text. The error
    /// stream is also the only place that says why a start failed, which is the sentence somebody
    /// needs when it does.
    /// </remarks>
    private void DrainOutput()
    {
        _process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is null)
            {
                return;
            }

            lock (_errors)
            {
                _errors.AppendLine(args.Data);
                if (_errors.Length > 8192)
                {
                    _errors.Remove(0, _errors.Length - 8192);
                }
            }
        };
        _process.OutputDataReceived += (_, _) => { };
        _process.BeginErrorReadLine();
        _process.BeginOutputReadLine();
    }

    private async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        var health = new Uri(_endpoint, "health");
        var deadline = DateTimeOffset.UtcNow + _options.ResolvedStartupTimeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_process.HasExited)
            {
                throw new InvalidOperationException(
                    $"The assistant runtime stopped while starting (exit code {_process.ExitCode}). {LastErrors()}");
            }

            try
            {
                using var response = await _client.GetAsync(health, cancellationToken);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    IsReady = true;
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet. Loading a gigabyte of weights takes as long as it takes.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        throw new TimeoutException(
            $"The assistant did not become ready within {_options.ResolvedStartupTimeout.TotalSeconds:N0} seconds. {LastErrors()}");
    }

    /// <summary>
    /// Asks the operating system for a free loopback port and gives it straight back.
    /// </summary>
    /// <remarks>
    /// There is a gap between letting go of the port and llama-server binding it, in which something
    /// else could take it. The alternative is letting llama-server choose and parsing the port out
    /// of its log, which trades a small race for a dependency on the wording of somebody else's log
    /// line. If the port is taken, the child exits immediately and startup says so with its output.
    /// </remarks>
    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private string LastErrors()
    {
        lock (_errors)
        {
            return Tail(_errors.ToString());
        }
    }

    private static string Tail(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 600 ? trimmed : trimmed[^600..];
    }

    public async ValueTask DisposeAsync()
    {
        IsReady = false;
        _client.Dispose();

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(CancellationToken.None);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
        finally
        {
            _process.Dispose();
        }
    }
}
