using WowEmu.Core;

namespace WowEmu.Protocol;

/// <summary>What kind of message this is. <c>ChatMsg</c>.</summary>
/// <remarks>
/// The type decides how the client renders the line and, for several values, what the packet body
/// even looks like — the monster and battleground types carry a sender name inline. Only the ones
/// this phase produces are here; the enum runs to 0x34 upstream.
/// </remarks>
public static class ChatMsg
{
    public const byte System = 0x00;
    public const byte Say = 0x01;
    public const byte Yell = 0x06;
    public const byte Whisper = 0x07;

    /// <summary>The echo of your own whisper, shown as "To Name: ...".</summary>
    /// <remarks>
    /// A separate type rather than a copy of the original: the client renders it differently and
    /// addresses it to the receiver. Echoing back a plain <see cref="Whisper"/> shows the sender
    /// whispering to themselves.
    /// </remarks>
    public const byte WhisperInform = 0x09;

    public const byte Emote = 0x0A;
    public const byte TextEmote = 0x0B;

    /// <summary>One past the last valid type. <c>MAX_CHAT_MSG_TYPE</c>.</summary>
    public const uint MaxType = 0x34;
}

/// <summary>Which language a line is spoken in. <c>Language</c>.</summary>
public static class ChatLanguage
{
    /// <summary>Understood by everyone. Only the server may choose it.</summary>
    /// <remarks>
    /// <b>A client claiming this is cheating</b>, which is why the handler refuses it outright: it
    /// would let a Horde character be understood by the Alliance, defeating the whole mechanic.
    /// </remarks>
    public const uint Universal = 0;

    public const uint Orcish = 1;
    public const uint Darnassian = 2;
    public const uint Taurahe = 3;
    public const uint Dwarvish = 6;
    public const uint Common = 7;
    public const uint Demonic = 8;
    public const uint Titan = 9;
    public const uint Thalassian = 10;
    public const uint Draconic = 11;
    public const uint Kalimag = 12;
    public const uint Gnomish = 13;
    public const uint Troll = 14;
    public const uint Gutterspeak = 33;
    public const uint Draenei = 35;
    public const uint Zombie = 36;
    public const uint GnomishBinary = 37;
    public const uint GoblinBinary = 38;

    /// <summary>Addon traffic. <c>0xFFFFFFFF</c>, not a real language.</summary>
    public const uint Addon = 0xFFFFFFFF;
}

/// <summary>The badge shown beside a name. <c>ChatTag</c>.</summary>
public static class ChatTag
{
    public const byte None = 0x00;
    public const byte Afk = 0x01;
    public const byte Dnd = 0x02;
    public const byte Gm = 0x04;
}

/// <summary>
/// <c>SMSG_MESSAGECHAT</c>.
/// </summary>
/// <remarks>
/// Port of the default branch of <c>ChatHandler::BuildChatPacket</c>, which is the one every type
/// this phase sends takes. The other branches exist because several types put a sender name inline
/// — the monster lines, the battleground system lines — and none of those are produced here.
/// </remarks>
public static class ChatPackets
{
    /// <summary>
    /// Writes one chat line.
    /// </summary>
    /// <param name="receiver">
    /// Who it is addressed to. Empty for anything broadcast; for a whisper it is the other party,
    /// and the client uses it to name the conversation.
    /// </param>
    /// <remarks>
    /// <b>The message length includes its terminator.</b> Writing the string's own length leaves the
    /// client one byte short and it renders the line with its last character missing — a small
    /// enough error to look like an encoding problem rather than a length one.
    /// </remarks>
    public static void Write(
        PacketWriter writer,
        byte type,
        uint language,
        ObjectGuid sender,
        ObjectGuid receiver,
        string message,
        byte tag = ChatTag.None)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(message);

        writer.WriteUInt8(type);
        writer.WriteUInt32(language);
        writer.WriteUInt64(sender.Value);

        // "Some flags", per upstream, which has never identified them. Always zero.
        writer.WriteUInt32(0);

        writer.WriteUInt64(receiver.Value);

        writer.WriteUInt32((uint)System.Text.Encoding.UTF8.GetByteCount(message) + 1);
        writer.WriteCString(message);

        writer.WriteUInt8(tag);
    }
}
