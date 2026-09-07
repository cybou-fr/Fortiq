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
/// Both manifests are read again here rather than remembered from the startup check. They are two
/// small files, this happens once per session at the moment somebody asks their first question, and
/// carrying a cached copy of them around the application for the sake of that would be trading a
/// clear path for nothing measurable.
/// </remarks>
public sealed class AssistantAdapter(string modelRoot, string runtimeRoot)
{
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
