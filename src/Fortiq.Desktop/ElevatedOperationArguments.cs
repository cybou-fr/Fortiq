namespace Fortiq.Desktop;

/// <summary>
/// Reads the command line that asks this instance to perform one privileged operation and exit.
/// </summary>
/// <remarks>
/// Its own type, and pure, because the alternative is parsing arguments inside application start-up
/// where nothing can test it. What it refuses matters as much as what it accepts: a switch with no
/// repository after it, or one followed by another switch, is a malformed request rather than a
/// request to operate on something unnamed.
/// </remarks>
public static class ElevatedOperationArguments
{
    /// <summary>
    /// Recognises <c>--backup &lt;repository&gt;</c> and <c>--prove &lt;repository&gt;</c>.
    /// </summary>
    /// <returns>True when <paramref name="args"/> asks for one operation on one repository.</returns>
    public static bool TryParse(string[] args, out ElevatedOperation operation, out string repositoryId)
    {
        ArgumentNullException.ThrowIfNull(args);

        operation = default;
        repositoryId = string.Empty;

        for (var index = 0; index < args.Length; index++)
        {
            var candidate = args[index];
            ElevatedOperation parsed;
            if (string.Equals(candidate, "--backup", StringComparison.OrdinalIgnoreCase))
            {
                parsed = ElevatedOperation.Backup;
            }
            else if (string.Equals(candidate, "--prove", StringComparison.OrdinalIgnoreCase))
            {
                parsed = ElevatedOperation.Prove;
            }
            else
            {
                continue;
            }

            if (index + 1 >= args.Length)
            {
                return false;
            }

            var value = args[index + 1];
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal))
            {
                return false;
            }

            operation = parsed;
            repositoryId = value;
            return true;
        }

        return false;
    }
}
