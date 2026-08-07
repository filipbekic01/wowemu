using WowEmu.Protocol;

namespace WowEmu.Game;

/// <summary>Why a line was not delivered.</summary>
public enum ChatRefusal
{
    None = 0,

    /// <summary>An empty line, or one that was nothing but a newline.</summary>
    Empty,

    /// <summary>Longer than the client itself will ever send.</summary>
    TooLong,

    /// <summary>Contains a control character the client would render as markup or a hyperlink.</summary>
    BadCharacter,

    /// <summary>A language the character has not learned.</summary>
    UnknownLanguage,

    /// <summary>The dead do not speak.</summary>
    Dead,
}

/// <summary>
/// Who hears what a player says, and in what language.
/// </summary>
/// <remarks>
/// Port of the parts of <c>WorldSession::HandleMessagechatOpcode</c> and
/// <c>WorldObject::Say/Yell/TextEmote</c> that do not need a system this phase lacks. Guild, party,
/// raid and channel chat are all absent because guilds, groups and channels are — what is here is
/// everything that reaches people by standing near them, plus whisper.
/// </remarks>
public static class Chat
{
    /// <summary>How far a normal line carries. <c>ListenRange.Say</c>.</summary>
    public const float SayRange = 40f;

    /// <summary>The same distance, for emotes. <c>ListenRange.TextEmote</c>.</summary>
    public const float EmoteRange = 40f;

    /// <summary>
    /// How far a yell carries. <c>ListenRange.Yell</c>.
    /// </summary>
    /// <remarks>
    /// 300 yards — far beyond the visibility radius, which is why a yell has to be sent by scanning
    /// the map rather than by walking the sender's set of watchers. Reusing the watcher set makes a
    /// yell carry exactly as far as a say and no further.
    /// </remarks>
    public const float YellRange = 300f;

    /// <summary>The longest line the server will relay.</summary>
    /// <remarks>
    /// The client will not compose one longer, so anything over this came from something that is
    /// not the client.
    /// </remarks>
    public const int MaxMessageLength = 255;

    /// <summary>
    /// The skill that lets a character speak a language. <c>lang_description</c>.
    /// </summary>
    /// <remarks>
    /// A skill id of zero means the language needs none — the two binary languages and the zombie
    /// one are effects rather than things you learn.
    /// </remarks>
    public static uint SkillFor(uint language) => language switch
    {
        ChatLanguage.Common => 98,
        ChatLanguage.Orcish => 109,
        ChatLanguage.Dwarvish => 111,
        ChatLanguage.Darnassian => 113,
        ChatLanguage.Taurahe => 115,
        ChatLanguage.Thalassian => 137,
        ChatLanguage.Draconic => 138,
        ChatLanguage.Demonic => 139,
        ChatLanguage.Titan => 140,
        ChatLanguage.Kalimag => 141,
        ChatLanguage.Gnomish => 313,
        ChatLanguage.Troll => 315,
        ChatLanguage.Gutterspeak => 673,
        ChatLanguage.Draenei => 759,
        _ => 0,
    };

    /// <summary>Whether a language exists at all, learnable or not.</summary>
    /// <remarks>
    /// Separate from <see cref="SkillFor"/> returning zero, which is also the answer for a language
    /// that needs no skill. Collapsing the two would let a client speak in language 9999.
    /// </remarks>
    public static bool IsRealLanguage(uint language) => language switch
    {
        ChatLanguage.Universal or ChatLanguage.Orcish or ChatLanguage.Darnassian
            or ChatLanguage.Taurahe or ChatLanguage.Dwarvish or ChatLanguage.Common
            or ChatLanguage.Demonic or ChatLanguage.Titan or ChatLanguage.Thalassian
            or ChatLanguage.Draconic or ChatLanguage.Kalimag or ChatLanguage.Gnomish
            or ChatLanguage.Troll or ChatLanguage.Gutterspeak or ChatLanguage.Draenei
            or ChatLanguage.Zombie or ChatLanguage.GnomishBinary or ChatLanguage.GoblinBinary
            or ChatLanguage.Addon => true,
        _ => false,
    };

    /// <summary>
    /// Whether a character may speak a language.
    /// </summary>
    /// <remarks>
    /// <b>Universal is never speakable by a client.</b> The client offers it only for AFK and DND
    /// messages; a chat line claiming it is an attempt to be understood by the other faction, and
    /// upstream logs it as a hacking attempt rather than merely refusing it.
    /// </remarks>
    public static bool CanSpeak(Player speaker, uint language)
    {
        ArgumentNullException.ThrowIfNull(speaker);

        if (!IsRealLanguage(language) || language == ChatLanguage.Universal)
        {
            return false;
        }

        uint skill = SkillFor(language);

        return skill == 0 || speaker.Skills.Has(skill);
    }

    /// <summary>
    /// Trims and checks a line, giving back what should actually be sent.
    /// </summary>
    /// <remarks>
    /// Port of the validity checks in <c>HandleMessagechatOpcode</c>. Two of them matter beyond
    /// tidiness:
    /// <list type="bullet">
    /// <item>
    /// <b>The line is cut at the first newline</b> rather than having newlines stripped. A message
    /// that begins with one is refused outright — the client cannot compose either, so both are
    /// something else trying to paint several lines from one message.
    /// </item>
    /// <item>
    /// <b>Control characters are refused, not escaped.</b> The client reads several of them as
    /// markup, and <c>|</c> sequences build clickable hyperlinks — a crafted one can make the client
    /// act on an item or a quest the sender chose.
    /// </item>
    /// </list>
    /// </remarks>
    public static ChatRefusal Clean(string message, out string cleaned)
    {
        ArgumentNullException.ThrowIfNull(message);

        cleaned = string.Empty;

        if (message.Length > MaxMessageLength)
        {
            return ChatRefusal.TooLong;
        }

        int newline = message.AsSpan().IndexOfAny('\n', '\r');

        if (newline == 0)
        {
            return ChatRefusal.Empty;
        }

        string text = newline > 0 ? message[..newline] : message;

        if (text.Length == 0)
        {
            return ChatRefusal.Empty;
        }

        foreach (char c in text)
        {
            if (IsNasty(c))
            {
                return ChatRefusal.BadCharacter;
            }
        }

        // "|0" is the escape the client reads as the end of a hyperlink body. Upstream refuses any
        // message containing it outright rather than trying to work out whether the link is whole.
        if (text.Contains("|0", StringComparison.Ordinal))
        {
            return ChatRefusal.BadCharacter;
        }

        cleaned = text;

        return ChatRefusal.None;
    }

    /// <summary>
    /// Everything the client would treat as something other than text.
    /// </summary>
    /// <remarks>
    /// Port of <c>isNasty</c>. Tab is allowed through and every other control character is not,
    /// which is upstream's line and not an obvious one — a bare tab renders harmlessly.
    /// </remarks>
    private static bool IsNasty(char c) => c != '\t' && char.IsControl(c);

    /// <summary>How far a message type carries, or 0 for one that is not spoken aloud.</summary>
    public static float RangeOf(byte type) => type switch
    {
        ChatMsg.Say or ChatMsg.Emote => SayRange,
        ChatMsg.TextEmote => EmoteRange,
        ChatMsg.Yell => YellRange,
        _ => 0f,
    };

    /// <summary>
    /// Whether an audience can understand a line, or hears it as gibberish.
    /// </summary>
    /// <remarks>
    /// The <i>client</i> garbles what it cannot understand — the server sends the real text and the
    /// language id, and the client substitutes syllables. So this decides nothing about the wire and
    /// everything about what the player reads.
    /// <para>
    /// Which means a naive implementation leaks: sending the true text with a language the listener
    /// lacks is exactly right, because the client will not show it. Nothing here needs to redact.
    /// </para>
    /// </remarks>
    public static bool Understands(Player listener, uint language)
    {
        ArgumentNullException.ThrowIfNull(listener);

        if (language == ChatLanguage.Universal)
        {
            return true;
        }

        uint skill = SkillFor(language);

        return skill == 0 || listener.Skills.Has(skill);
    }
}
