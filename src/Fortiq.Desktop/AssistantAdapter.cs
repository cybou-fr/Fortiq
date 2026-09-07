using System.Runtime.InteropServices;
using Fortiq.Assistant;

namespace Fortiq.Desktop;

/// <summary>
/// Turns "where this installation keeps its pinned files" into a running assistant.
/// </summary>
/// <remarks>
/// The desktop knows where things are; the view model knows what to ask. This is the seam between
/// them, and it exists so that <c>AssistantViewModel</c> can be tested against a fake runtime rather
/// than against a gigabyte of weights and a child process.
///
/// Both manifests are read here rather than at startup. Startup deliberately does not look for the
/// assistant at all: it is required for a valid installation and not required in order to open the
/// application, and this is the only place that difference has to be understood.
/// </remarks>
public sealed class AssistantAdapter(string modelRoot, string runtimeRoot)
{
    /// <summary>
    /// Says why the assistant cannot run here, or null when it can.
    /// </summary>
    /// <remarks>
    /// Asked by the screen before it offers a question box, so that a machine missing the model
    /// explains itself instead of accepting a question and then failing. Startup does not ask this:
    /// the assistant is required for a valid installation, and not required in order to open the
    /// application, and confusing those two once cost Fortiq the ability to start at all.
    /// </remarks>
    public async Task<string?> DescribeUnavailableAsync(CancellationToken cancellationToken)
    {
        var model = await ModelAvailability.InspectAsync(modelRoot, cancellationToken);
        if (!model.Usable)
        {
            return model.Detail;
        }

        var runtime = await RuntimeAvailability.InspectAsync(
            runtimeRoot,
            RuntimeInformation.RuntimeIdentifier,
            cancellationToken);
        return runtime.Usable ? null : runtime.Detail;
    }

    public async Task<IAssistantRuntime> StartAsync(CancellationToken cancellationToken)
    {
        var model = await ModelAvailability.InspectAsync(modelRoot, cancellationToken);
        if (!model.Usable)
        {
            throw new InvalidOperationException(model.Detail);
        }

        var runtime = await RuntimeAvailability.InspectAsync(
            runtimeRoot,
            RuntimeInformation.RuntimeIdentifier,
            cancellationToken);
        if (!runtime.Usable)
        {
            throw new InvalidOperationException(runtime.Detail);
        }

        return await LlamaServerRuntime.StartAsync(
            new AssistantRuntimeOptions(
                runtime.Path!,
                model.Path!,
                model.Entry!.ContextTokens,
                // Half the machine, never fewer than two. Inference will take everything it is given,
                // and the assistant is the least important thing running: a backup that has to share
                // this PC with it must still finish, and so must whatever the person is doing.
                Threads: Math.Max(2, Environment.ProcessorCount / 2)),
            cancellationToken);
    }
}
