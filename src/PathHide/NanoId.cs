using System;
using System.Security.Cryptography;

namespace PathHide;

/// <summary>
/// Generates nanoid-style random identifiers: crypto-random strings drawn from the standard 64-character
/// URL-safe alphabet (<c>A-Za-z0-9_-</c>), 21 characters by default — nanoid's own default length, chosen
/// so collision odds stay negligible at practical volumes. Used wherever the app needs a short,
/// filesystem- and URL-safe discriminator (an atomic-write temp-file suffix, an elevated-IPC results-file
/// name, a test's throwaway directory name) without a GUID's 32-hex-digit shape or dashed canonical form.
/// </summary>
/// <remarks>
/// 64 divides 256 evenly, so masking a random byte with <c>&amp; 0x3F</c> (0-63) indexes the alphabet with
/// perfect uniformity — no rejection sampling is needed, unlike alphabets whose length isn't a power of
/// two. The randomness comes from <see cref="RandomNumberGenerator"/>, not <see cref="Random"/>, so ids
/// are unguessable as well as merely distinct.
/// </remarks>
public static class NanoId
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-";
    private const int DefaultLength = 21;

    /// <summary>Generates a new id of <paramref name="length"/> characters (21 by default) from the
    /// standard URL-safe alphabet, using one cryptographically random byte per character.</summary>
    public static string New(int length = DefaultLength)
    {
        if (length <= 0)
            throw new ArgumentOutOfRangeException(nameof(length), length, "Length must be positive.");

        var bytes = new byte[length];
        RandomNumberGenerator.Fill(bytes);

        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = Alphabet[bytes[i] & 0x3F];
        }

        return new string(chars);
    }
}
