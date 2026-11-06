using System.Security.Cryptography;

namespace Nms.Shared;

/// <summary>
/// Short human-readable claim codes: 8 characters from an alphabet without look-alikes (no 0/O, 1/I/L),
/// shown as XXXX-XXXX. 32^8 ≈ 1.1e12 combinations for a 15-minute lifetime behind a rate limit.
/// </summary>
public static class ClaimCode
{
    public const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    public const int Length = 8;

    public static string Generate()
    {
        var chars = new char[Length];
        for (var i = 0; i < Length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
    }

    /// <summary>Upper-cases and strips separators and whitespace so "kd7p-4qrs" and "KD7P4QRS" compare equal.</summary>
    public static string Normalize(string input)
    {
        Span<char> buf = stackalloc char[input.Length];
        var n = 0;
        foreach (var c in input)
        {
            if (char.IsLetterOrDigit(c)) buf[n++] = char.ToUpperInvariant(c);
        }
        return new string(buf[..n]);
    }

    public static string Format(string normalized) =>
        normalized.Length == Length ? $"{normalized[..4]}-{normalized[4..]}" : normalized;

    public static bool IsValid(string normalized) =>
        normalized.Length == Length && normalized.All(c => Alphabet.Contains(c));
}
