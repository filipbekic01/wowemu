using WowEmu.Core;
using WowEmu.Data.Client;
using WowEmu.Game;
using WowEmu.Protocol;

namespace WowEmu.Tests.Unit;

/// <summary>
/// What a player may say, and in what language.
/// </summary>
/// <remarks>
/// Most of this is refusal rather than delivery, and that is the shape of the real handler too: the
/// language rules are the whole of the faction barrier, and the message validation is what stops a
/// client painting arbitrary markup into everyone else's chat window.
/// </remarks>
public sealed class ChatRulesTests
{
    /// <summary>
    /// A client may never speak Universal, even though the server sends in it constantly.
    /// </summary>
    /// <remarks>
    /// Universal is understood by everyone, so a client choosing it is asking to be understood by
    /// the other faction — which is the entire mechanic. Upstream logs it as a hacking attempt
    /// rather than a typo.
    /// </remarks>
    [Fact]
    public void AClient_MayNeverSpeakUniversal()
    {
        Player player = Speaker();
        player.Skills.Set(CommonSkill, 0, 300, 300);

        Assert.False(Chat.CanSpeak(player, ChatLanguage.Universal));
    }

    /// <summary>A language the character has not learned is refused.</summary>
    [Fact]
    public void AnUnlearnedLanguage_IsRefused() =>
        Assert.False(Chat.CanSpeak(Speaker(), ChatLanguage.Common));

    /// <summary>And a learned one is allowed.</summary>
    [Fact]
    public void ALearnedLanguage_IsAllowed()
    {
        Player player = Speaker();
        player.Skills.Set(CommonSkill, 0, 300, 300);

        Assert.True(Chat.CanSpeak(player, ChatLanguage.Common));
    }

    /// <summary>
    /// A language that exists but needs no skill is allowed to anyone.
    /// </summary>
    /// <remarks>
    /// The binary languages and the zombie one are effects rather than things you learn, so their
    /// skill id is zero. That is <b>not</b> the same as an invented language id, which also has no
    /// skill — collapsing the two lets a client speak in language 9999.
    /// </remarks>
    [Fact]
    public void ASkilllessLanguage_IsAllowedButAnInventedOneIsNot()
    {
        Player player = Speaker();

        Assert.True(Chat.CanSpeak(player, ChatLanguage.Zombie));
        Assert.False(Chat.CanSpeak(player, 9999));
        Assert.Equal(0u, Chat.SkillFor(ChatLanguage.Zombie));
        Assert.Equal(0u, Chat.SkillFor(9999));
    }

    /// <summary>Every real language maps to the skill upstream's table gives it.</summary>
    [Theory]
    [InlineData(ChatLanguage.Common, 98u)]
    [InlineData(ChatLanguage.Orcish, 109u)]
    [InlineData(ChatLanguage.Dwarvish, 111u)]
    [InlineData(ChatLanguage.Darnassian, 113u)]
    [InlineData(ChatLanguage.Taurahe, 115u)]
    [InlineData(ChatLanguage.Gnomish, 313u)]
    [InlineData(ChatLanguage.Troll, 315u)]
    [InlineData(ChatLanguage.Gutterspeak, 673u)]
    [InlineData(ChatLanguage.Draenei, 759u)]
    public void LanguagesMapToTheirSkill(uint language, uint skill) =>
        Assert.Equal(skill, Chat.SkillFor(language));

    // ------------------------------------------------------------------ the message itself

    /// <summary>An ordinary line comes through unchanged.</summary>
    [Fact]
    public void AnOrdinaryLine_ComesThroughUnchanged()
    {
        Assert.Equal(ChatRefusal.None, Chat.Clean("hello there", out string text));
        Assert.Equal("hello there", text);
    }

    /// <summary>An empty line is refused rather than broadcast.</summary>
    [Fact]
    public void AnEmptyLine_IsRefused() =>
        Assert.Equal(ChatRefusal.Empty, Chat.Clean(string.Empty, out _));

    /// <summary>
    /// A line is cut at the first newline, and one that starts with a newline is refused.
    /// </summary>
    /// <remarks>
    /// The client composes neither, so both are something else trying to paint several lines from
    /// one message — a convincing way to fake other people's chat.
    /// </remarks>
    [Fact]
    public void ALine_IsCutAtTheFirstNewline()
    {
        Assert.Equal(ChatRefusal.None, Chat.Clean("real line\nfaked line", out string text));
        Assert.Equal("real line", text);

        Assert.Equal(ChatRefusal.Empty, Chat.Clean("\nfaked line", out _));
        Assert.Equal(ChatRefusal.None, Chat.Clean("real\rfaked", out string carriage));
        Assert.Equal("real", carriage);
    }

    /// <summary>
    /// Control characters are refused rather than escaped.
    /// </summary>
    /// <remarks>
    /// The client reads several of them as markup. Refusing the whole line is upstream's answer and
    /// the safe one — escaping means being certain about every rendering rule the client has.
    /// </remarks>
    [Fact]
    public void ControlCharacters_AreRefused() =>
        Assert.Equal(ChatRefusal.BadCharacter, Chat.Clean("hello\u0001there", out _));

    /// <summary>Tab is the one control character allowed through.</summary>
    /// <remarks>Upstream's line, and not an obvious one — a bare tab renders harmlessly.</remarks>
    [Fact]
    public void Tab_IsAllowedThrough()
    {
        Assert.Equal(ChatRefusal.None, Chat.Clean("a\tb", out string text));
        Assert.Equal("a\tb", text);
    }

    /// <summary>
    /// A hyperlink escape is refused.
    /// </summary>
    /// <remarks>
    /// <c>|0</c> ends a hyperlink body, and a crafted link makes the receiving client act on an item
    /// or quest the sender chose. Upstream refuses any message containing the sequence rather than
    /// trying to decide whether the link is well formed.
    /// </remarks>
    [Fact]
    public void AHyperlinkEscape_IsRefused() =>
        Assert.Equal(ChatRefusal.BadCharacter, Chat.Clean("look at |0this", out _));

    /// <summary>Anything longer than the client can compose is refused.</summary>
    [Fact]
    public void AnOverlongLine_IsRefused() =>
        Assert.Equal(
            ChatRefusal.TooLong,
            Chat.Clean(new string('a', Chat.MaxMessageLength + 1), out _));

    /// <summary>Exactly the limit is fine.</summary>
    [Fact]
    public void ALineAtTheLimit_IsFine() =>
        Assert.Equal(ChatRefusal.None, Chat.Clean(new string('a', Chat.MaxMessageLength), out _));

    // ------------------------------------------------------------------ range

    /// <summary>
    /// A yell carries far past what is visible; a say does not.
    /// </summary>
    /// <remarks>
    /// Three hundred yards against forty. It is why a yell has to be sent by scanning the map rather
    /// than by walking the speaker's watcher set — that set is bounded by visibility, so reusing it
    /// makes a yell reach exactly as far as a say and no further.
    /// </remarks>
    [Fact]
    public void AYell_CarriesFurtherThanASay()
    {
        Assert.Equal(40f, Chat.RangeOf(ChatMsg.Say));
        Assert.Equal(40f, Chat.RangeOf(ChatMsg.Emote));
        Assert.Equal(40f, Chat.RangeOf(ChatMsg.TextEmote));
        Assert.Equal(300f, Chat.RangeOf(ChatMsg.Yell));
    }

    /// <summary>Something not spoken aloud has no range.</summary>
    [Fact]
    public void SomethingNotSpokenAloud_HasNoRange()
    {
        Assert.Equal(0f, Chat.RangeOf(ChatMsg.Whisper));
        Assert.Equal(0f, Chat.RangeOf(ChatMsg.System));
    }

    /// <summary>Understanding is the listener's skill, and Universal is understood by all.</summary>
    [Fact]
    public void UnderstandingFollowsTheListenersSkill()
    {
        Player monoglot = Speaker();
        Player bilingual = Speaker();
        bilingual.Skills.Set(CommonSkill, 0, 300, 300);

        Assert.True(Chat.Understands(monoglot, ChatLanguage.Universal));
        Assert.False(Chat.Understands(monoglot, ChatLanguage.Common));
        Assert.True(Chat.Understands(bilingual, ChatLanguage.Common));
    }

    private const uint CommonSkill = 98;

    private static Player Speaker() =>
        InventoryFixture.Player(level: 10, proficiencies: false);
}

/// <summary>
/// <c>SMSG_MESSAGECHAT</c> on the wire.
/// </summary>
public sealed class ChatPacketTests
{
    /// <summary>
    /// The message length includes its terminator.
    /// </summary>
    /// <remarks>
    /// Writing the string's own length leaves the client one byte short, and it renders the line
    /// with its last character missing — which reads as an encoding problem rather than a length
    /// one, and sends you looking in the wrong place.
    /// </remarks>
    [Fact]
    public void TheLength_IncludesTheTerminator()
    {
        byte[] body = Build("hello", out _);

        // type(1) + language(4) + sender(8) + flags(4) + receiver(8) = 25
        uint length = BitConverter.ToUInt32(body, 25);

        Assert.Equal(6u, length);
        Assert.Equal((byte)0, body[25 + 4 + 5]);
    }

    /// <summary>The fields land in the order the client reads them.</summary>
    [Fact]
    public void TheFields_AreInTheClientsOrder()
    {
        ObjectGuid sender = ObjectGuid.Create(HighGuid.Player, 7);
        ObjectGuid receiver = ObjectGuid.Create(HighGuid.Player, 9);

        PacketWriter writer = new();
        ChatPackets.Write(writer, ChatMsg.Whisper, ChatLanguage.Common, sender, receiver, "hi", ChatTag.Gm);

        byte[] body = writer.ToArray();

        Assert.Equal(ChatMsg.Whisper, body[0]);
        Assert.Equal(ChatLanguage.Common, BitConverter.ToUInt32(body, 1));
        Assert.Equal(sender.Value, BitConverter.ToUInt64(body, 5));
        Assert.Equal(0u, BitConverter.ToUInt32(body, 13));
        Assert.Equal(receiver.Value, BitConverter.ToUInt64(body, 17));

        // The tag is last, after the message and its terminator.
        Assert.Equal(ChatTag.Gm, body[^1]);
    }

    /// <summary>
    /// A multi-byte character counts its bytes, not its chars.
    /// </summary>
    /// <remarks>
    /// The length prefix is a byte count. Using the string's <c>Length</c> under-counts anything
    /// non-ASCII and truncates the line at the client — which only shows up once someone types in a
    /// language the developers do not.
    /// </remarks>
    [Fact]
    public void AMultiByteCharacter_CountsItsBytes()
    {
        byte[] body = Build("é", out _);

        Assert.Equal(3u, BitConverter.ToUInt32(body, 25));
    }

    private static byte[] Build(string message, out PacketWriter writer)
    {
        writer = new PacketWriter();

        ChatPackets.Write(
            writer,
            ChatMsg.Say,
            ChatLanguage.Common,
            ObjectGuid.Create(HighGuid.Player, 1),
            ObjectGuid.Empty,
            message);

        return writer.ToArray();
    }
}
