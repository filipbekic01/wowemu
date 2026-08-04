using System.Globalization;

namespace WowEmu.WorldServer;

/// <summary>
/// Validates and normalizes character names.
/// </summary>
/// <remarks>
/// Port of the shape of <c>ObjectMgr::CheckPlayerName</c>. The client enforces these rules too, but
/// a client is a program on someone else's machine — every rule it applies has to be reapplied
/// here or a modified client can create a 200-character name with a newline in it.
/// <para>
/// Normalization to Titlecase is not cosmetic: names are compared under a binary collation, so
/// "Thrall" and "THRALL" would otherwise be two different characters that render identically in the
/// character list.
/// </para>
/// </remarks>
public static class CharacterName
{
    /// <summary>The client's own limits.</summary>
    public const int MinLength = 2;

    /// <summary>Matches the <c>name</c> column width.</summary>
    public const int MaxLength = 12;

    /// <summary>
    /// Checks a name and returns it normalized: first letter upper, rest lower.
    /// </summary>
    public static bool TryNormalize(string? name, out string normalized)
    {
        normalized = string.Empty;

        if (string.IsNullOrEmpty(name) || name.Length is < MinLength or > MaxLength)
        {
            return false;
        }

        foreach (char character in name)
        {
            // Letters only. No digits, spaces, punctuation or control characters — the client's
            // rule, and the reason names cannot be used to smuggle formatting into chat.
            if (!char.IsLetter(character))
            {
                return false;
            }
        }

        normalized = string.Concat(
            char.ToUpper(name[0], CultureInfo.InvariantCulture),
            name[1..].ToLowerInvariant());

        return true;
    }
}
