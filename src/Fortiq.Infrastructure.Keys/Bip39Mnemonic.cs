using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Fortiq.Infrastructure.Keys;

/// <summary>
/// BIP-39 as a human-readable encoding of recovery entropy, and nothing more: the key hierarchy is
/// derived by Fortiq's own versioned context, not by BIP-39.
/// </summary>
/// <remarks>
/// The English wordlist ships with the assembly so an offline recovery tool needs no network and no
/// installed data. Its provenance and normalized SHA-256 are recorded next to it in
/// <c>Bip39/english.provenance.json</c>.
/// </remarks>
public static class Bip39Mnemonic
{
    public const int WordCount = 2048;
    public const int DefaultEntropySize = 32;
    private const int BitsPerWord = 11;
    private const int Pbkdf2Iterations = 2048;
    private const int SeedSize = 64;

    private static readonly string[] Words = LoadWordlist();
    private static readonly Dictionary<string, int> Indexes = BuildIndexes(Words);

    public static IReadOnlyList<string> Wordlist => Words;

    /// <summary>Encodes entropy of 16, 20, 24, 28 or 32 bytes as a mnemonic with its checksum.</summary>
    public static string Encode(ReadOnlySpan<byte> entropy)
    {
        RequireValidEntropyLength(entropy.Length);

        var checksumBits = entropy.Length * 8 / 32;
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(entropy, digest);

        var bits = new bool[(entropy.Length * 8) + checksumBits];
        for (var index = 0; index < entropy.Length * 8; index++)
        {
            bits[index] = (entropy[index / 8] & (1 << (7 - (index % 8)))) != 0;
        }

        for (var index = 0; index < checksumBits; index++)
        {
            bits[(entropy.Length * 8) + index] = (digest[index / 8] & (1 << (7 - (index % 8)))) != 0;
        }

        var mnemonic = new StringBuilder();
        for (var word = 0; word < bits.Length / BitsPerWord; word++)
        {
            var value = 0;
            for (var bit = 0; bit < BitsPerWord; bit++)
            {
                value = (value << 1) | (bits[(word * BitsPerWord) + bit] ? 1 : 0);
            }

            if (word > 0)
            {
                mnemonic.Append(' ');
            }

            mnemonic.Append(Words[value]);
        }

        return mnemonic.ToString();
    }

    /// <summary>
    /// Decodes a mnemonic back to its entropy. The wordlist and the checksum are both verified, so a
    /// mistyped word is reported before any key derivation is attempted.
    /// </summary>
    public static byte[] Decode(string mnemonic)
    {
        var words = Split(mnemonic);
        if (words.Length is not (12 or 15 or 18 or 21 or 24))
        {
            throw new FormatException("A BIP-39 mnemonic must contain 12, 15, 18, 21 or 24 words.");
        }

        var bits = new bool[words.Length * BitsPerWord];
        for (var word = 0; word < words.Length; word++)
        {
            if (!Indexes.TryGetValue(words[word], out var value))
            {
                throw new FormatException("The mnemonic contains a word that is not in the BIP-39 English wordlist.");
            }

            for (var bit = 0; bit < BitsPerWord; bit++)
            {
                bits[(word * BitsPerWord) + bit] = (value & (1 << (BitsPerWord - 1 - bit))) != 0;
            }
        }

        var checksumBits = bits.Length / 33;
        var entropy = new byte[(bits.Length - checksumBits) / 8];
        for (var index = 0; index < entropy.Length * 8; index++)
        {
            if (bits[index])
            {
                entropy[index / 8] |= (byte)(1 << (7 - (index % 8)));
            }
        }

        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(entropy, digest);
        for (var index = 0; index < checksumBits; index++)
        {
            var expected = (digest[index / 8] & (1 << (7 - (index % 8)))) != 0;
            if (bits[(entropy.Length * 8) + index] != expected)
            {
                throw new FormatException("The mnemonic checksum does not match; a word is wrong or out of order.");
            }
        }

        return entropy;
    }

    public static string Create(int entropySize = DefaultEntropySize)
    {
        RequireValidEntropyLength(entropySize);
        var entropy = RandomNumberGenerator.GetBytes(entropySize);
        try
        {
            return Encode(entropy);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(entropy);
        }
    }

    /// <summary>
    /// The standard BIP-39 seed: PBKDF2-HMAC-SHA512 with 2048 iterations over the NFKD-normalized
    /// mnemonic and the salt "mnemonic" plus the optional passphrase. The iteration count belongs to
    /// the standard and is never substituted, or compatibility with other BIP-39 tools is lost.
    /// </summary>
    public static byte[] DeriveSeed(string mnemonic, string? passphrase = null)
    {
        var normalizedMnemonic = string.Join(' ', Split(mnemonic));
        var normalizedPassphrase = (passphrase ?? string.Empty).Normalize(NormalizationForm.FormKD);
        var password = Encoding.UTF8.GetBytes(normalizedMnemonic);
        var salt = Encoding.UTF8.GetBytes("mnemonic" + normalizedPassphrase);
        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA512, SeedSize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
            CryptographicOperations.ZeroMemory(salt);
        }
    }

    /// <summary>
    /// Applies the BIP-39 NFKD normalization and collapses whitespace, so a mnemonic pasted with
    /// odd spacing or a different Unicode composition still resolves to the same words.
    /// </summary>
    private static string[] Split(string mnemonic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mnemonic);
        return mnemonic
            .Normalize(NormalizationForm.FormKD)
            .ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static void RequireValidEntropyLength(int length)
    {
        if (length is not (16 or 20 or 24 or 28 or 32))
        {
            throw new ArgumentException("BIP-39 entropy must be 16, 20, 24, 28 or 32 bytes.", nameof(length));
        }
    }

    private static string[] LoadWordlist()
    {
        using var stream = typeof(Bip39Mnemonic).GetTypeInfo().Assembly
            .GetManifestResourceStream("Fortiq.Infrastructure.Keys.Bip39.english.txt")
            ?? throw new InvalidOperationException("The BIP-39 English wordlist is missing from the assembly.");
        using var reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var words = reader.ReadToEnd()
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return words.Length == WordCount
            ? words
            : throw new InvalidOperationException("The BIP-39 English wordlist does not contain 2048 words.");
    }

    private static Dictionary<string, int> BuildIndexes(string[] words)
    {
        var indexes = new Dictionary<string, int>(words.Length, StringComparer.Ordinal);
        for (var index = 0; index < words.Length; index++)
        {
            if (!indexes.TryAdd(words[index].Normalize(NormalizationForm.FormKD), index))
            {
                throw new InvalidOperationException("The BIP-39 English wordlist contains a duplicate word.");
            }
        }

        return indexes;
    }

    internal static string NormalizedWordlistText() =>
        string.Join('\n', Words.Select(word => word.Normalize(NormalizationForm.FormKD))) + '\n';
}
