using System.Text.RegularExpressions;

namespace Fortiq.Domain;

/// <summary>
/// The stable identity of a backup source. It is written into the repository itself, so it has to
/// survive engine metadata unchanged: a restricted ASCII form, no separators the engine treats
/// specially, and a bounded length.
/// </summary>
public static partial class SourceIdentifier
{
    public const int MaximumLength = 128;

    public static bool IsValid(string? value) => value is not null && PatternRegex().IsMatch(value);

    public static string Require(string value, string parameterName) =>
        IsValid(value)
            ? value
            : throw new ArgumentException(
                "A source identifier must be 1 to 128 characters of ASCII letters, digits, '.', '_', ':' or '-', starting with a letter or digit.",
                parameterName);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:\-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex PatternRegex();
}
