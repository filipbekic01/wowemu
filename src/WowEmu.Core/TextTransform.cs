namespace WowEmu.Core;

/// <summary>
/// String transforms whose exact behaviour is part of the protocol or the stored data format.
/// </summary>
public static class TextTransform
{
    /// <summary>
    /// Uppercases <b>only</b> the basic Latin letters <c>a</c>–<c>z</c>, leaving every other
    /// character — including accented Latin, Cyrillic and CJK — exactly as it was.
    /// </summary>
    /// <remarks>
    /// Port of <c>Utf8ToUpperOnlyLatin</c> / <c>wcharToUpperOnlyLatin</c>
    /// (<c>src/common/Utilities/Util.{h,cpp}</c>), which is gated on
    /// <c>isBasicLatinCharacter</c> — true only for <c>a-z</c> and <c>A-Z</c>.
    /// <para>
    /// <b>This is not <see cref="string.ToUpperInvariant"/>.</b> Account names and passwords are
    /// passed through this transform before the SRP6 verifier is computed, so the stored verifier
    /// for an account containing (say) <c>é</c> was computed with that character left lowercase.
    /// Using <c>ToUpperInvariant</c> would map it to <c>É</c> and silently make the account
    /// impossible to log into, while every ASCII-only account kept working.
    /// </para>
    /// </remarks>
    public static string Utf8ToUpperOnlyLatin(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        // Fast path: nothing to change.
        int firstLower = -1;
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] is >= 'a' and <= 'z')
            {
                firstLower = i;
                break;
            }
        }

        if (firstLower < 0)
        {
            return value;
        }

        return string.Create(value.Length, (value, firstLower), static (destination, state) =>
        {
            (string source, int start) = state;
            source.AsSpan().CopyTo(destination);

            for (int i = start; i < destination.Length; i++)
            {
                if (destination[i] is >= 'a' and <= 'z')
                {
                    destination[i] = (char)(destination[i] - 0x20);
                }
            }
        });
    }
}
