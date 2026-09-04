namespace Fortiq.Infrastructure.Keys;

/// <summary>
/// The suites this build knows and the provider each one belongs to. A suite identifier decides how
/// an envelope is opened, so it may never disagree with the provider type recorded beside it: policy
/// that reads the provider type would otherwise be deciding on a different envelope than the one the
/// cryptography would open.
/// </summary>
public static class EnvelopeSuites
{
    private static readonly Dictionary<string, EnvelopeProviderType> Known = new(StringComparer.Ordinal)
    {
        [RecoverySecretEnvelope.SuiteId] = EnvelopeProviderType.Bip39,
        [Bip39RecoveryEnvelope.SuiteId] = EnvelopeProviderType.Bip39,
        [WindowsTpmEnvelope.SuiteId] = EnvelopeProviderType.WindowsTpm
    };

    /// <summary>The provider a known suite belongs to, or null for a suite this build does not know.</summary>
    public static EnvelopeProviderType? ProviderTypeFor(string suite) =>
        Known.TryGetValue(suite, out var providerType) ? providerType : null;

    /// <summary>
    /// Rejects an envelope whose suite and provider type contradict each other. A suite this build
    /// does not know is left alone: it is refused later, by the provider asked to open it, rather
    /// than mistaken for a malformed envelope here.
    /// </summary>
    public static void RequireConsistent(string suite, EnvelopeProviderType providerType)
    {
        if (ProviderTypeFor(suite) is { } expected && expected != providerType)
        {
            throw new InvalidDataException("The envelope suite and its provider type contradict each other.");
        }
    }
}
