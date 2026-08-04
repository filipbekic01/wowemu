using WowEmu.Protocol;

namespace WowEmu.Network;

/// <summary>An outgoing packet: an opcode and a body to write into.</summary>
public sealed class ServerPacket(Opcode opcode, int capacity = 64)
{
    /// <summary>The body. The header is added by the connection at send time.</summary>
    public PacketWriter Body { get; } = new(capacity);

    public Opcode Opcode { get; } = opcode;

    public int Length => Body.Length;
}
