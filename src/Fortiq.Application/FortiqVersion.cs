using System.Reflection;

namespace Fortiq.Application;

/// <summary>
/// The version this build is, in one place, for everything that has to say so or compare it.
/// </summary>
/// <remarks>
/// Read from <c>Fortiq.Application</c> rather than from whichever assembly happens to be asking. Every
/// Fortiq assembly is stamped from the same <c>Directory.Build.props</c>, so any of them would give the
/// same answer when they were built together - and the whole reason this exists is the case where they
/// were not. A desktop asking itself what version it is, and a service asking itself, is exactly how
/// two halves of a machine can each be certain and disagree.
/// </remarks>
public static class FortiqVersion
{
    /// <summary>The informational version, without the build metadata after '+'.</summary>
    /// <remarks>
    /// The informational version rather than <see cref="AssemblyName.Version"/>: the latter is a
    /// four-part number that drops the pre-release part, so 0.1.0-beta.1 and 0.1.0 are the same number
    /// to it. Telling those apart is most of the point.
    /// </remarks>
    public static string Current { get; } = Read();

    private static string Read()
    {
        var informational = typeof(FortiqVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            var build = informational.IndexOf('+', StringComparison.Ordinal);
            return build < 0 ? informational : informational[..build];
        }

        return typeof(FortiqVersion).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
