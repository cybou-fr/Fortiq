using Fortiq.CommunityModel;

namespace Fortiq.Desktop;

/// <summary>
/// Assembles what the assistant is told about this machine, from what this machine actually has.
/// </summary>
/// <remarks>
/// A thin layer over <see cref="ResourceCatalogSource"/>, which is the point: the screens listing
/// tasks and the assistant describing them read one object. Two separate readings of the same files
/// would eventually disagree, and the day they do is the day somebody believes the wrong one.
/// </remarks>
public sealed class AssistantContextAdapter(ResourceCatalogSource source)
{
    private readonly AssistantContextBuilder _builder = new();

    public async Task<AssistantContext> PrepareAsync(CancellationToken cancellationToken)
    {
        var state = await source.ReadAsync(cancellationToken);
        return _builder.Build(state.Catalog, state.Facts);
    }
}
