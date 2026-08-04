using WowEmu.Protocol;

namespace WowEmu.Tests.Unit;

/// <summary>
/// The generated opcode table, spot-checked against upstream's <c>Opcodes.cpp</c>.
/// </summary>
/// <remarks>
/// The whole table is generated, so these tests are not proving each of 1312 rows — they prove the
/// generator read the macros correctly and that the classification means what PLAN.md §4.2 says it
/// means. The values asserted here were read out of the C++ by hand.
/// </remarks>
public sealed class OpcodeTableTests
{
    [Fact]
    public void Table_CoversTheWholeOpcodeSpace()
    {
        // 725 client handlers plus 587 server-only opcodes, as upstream defines them.
        Assert.Equal(1312, OpcodeTable.All.Count);
    }

    [Fact]
    public void ClientOpcodes_CarryTheUpstreamHandlerName()
    {
        OpcodeInfo info = Get(Opcode.CMSG_CHAR_ENUM);

        Assert.Equal("HandleCharEnumOpcode", info.UpstreamHandler);
        Assert.False(info.IsServerOpcode);
        Assert.Equal(SessionStatus.Authed, info.Status);
    }

    [Fact]
    public void ServerOpcodes_HaveNoHandler()
    {
        OpcodeInfo info = Get(Opcode.SMSG_CHAR_ENUM);

        Assert.True(info.IsServerOpcode);
        Assert.Null(info.UpstreamHandler);
        Assert.Equal(SessionStatus.Never, info.Status);
    }

    /// <summary>
    /// The processing class is the thread-safety contract: <c>ThreadUnsafe</c> means the world tick
    /// and only the world tick may run it.
    /// </summary>
    [Fact]
    public void ProcessingClass_MatchesUpstream()
    {
        OpcodeInfo login = Get(Opcode.CMSG_PLAYER_LOGIN);
        Assert.Equal(PacketProcessing.ThreadUnsafe, login.Processing);

        OpcodeInfo movement = Get(Opcode.MSG_MOVE_HEARTBEAT);
        Assert.Equal(PacketProcessing.ThreadSafe, movement.Processing);
        Assert.Equal(SessionStatus.LoggedIn, movement.Status);
    }

    /// <summary>
    /// A client that sends a server-to-client opcode is confused or hostile; either way it is not
    /// something to hand to a handler.
    /// </summary>
    [Fact]
    public void ServerOpcodes_AreNeverAcceptedFromAClient()
    {
        Assert.False(OpcodeTable.IsAllowedFrom(Opcode.SMSG_AUTH_RESPONSE, SessionStatus.Authed));
        Assert.False(OpcodeTable.IsAllowedFrom(Opcode.SMSG_AUTH_RESPONSE, SessionStatus.LoggedIn));
    }

    [Fact]
    public void AuthedOpcodes_AreAllowedBeforeAPlayerExists()
    {
        Assert.True(OpcodeTable.IsAllowedFrom(Opcode.CMSG_CHAR_ENUM, SessionStatus.Authed));
        Assert.True(OpcodeTable.IsAllowedFrom(Opcode.CMSG_REALM_SPLIT, SessionStatus.Authed));
    }

    /// <summary>
    /// Gameplay opcodes need a player in the world. This is what stops a client skipping character
    /// selection and sending movement straight after authenticating.
    /// </summary>
    [Fact]
    public void LoggedInOpcodes_AreRejectedWhileOnlyAuthed()
    {
        Assert.False(OpcodeTable.IsAllowedFrom(Opcode.MSG_MOVE_HEARTBEAT, SessionStatus.Authed));
        Assert.True(OpcodeTable.IsAllowedFrom(Opcode.MSG_MOVE_HEARTBEAT, SessionStatus.LoggedIn));
    }

    [Fact]
    public void UnclassifiedOpcode_IsNotFound()
    {
        // 0x0000 has no entry upstream.
        Assert.False(OpcodeTable.TryGet((Opcode)0x0000, out _));
        Assert.False(OpcodeTable.IsAllowedFrom((Opcode)0x0000, SessionStatus.Authed));
    }

    [Fact]
    public void EveryEntry_HasAHandlerNameExactlyWhenItIsAClientOpcode()
    {
        foreach (OpcodeInfo info in OpcodeTable.All)
        {
            Assert.Equal(info.UpstreamHandler is null, info.IsServerOpcode);
        }
    }

    /// <summary>Sanity: the generated enum and the generated table agree on values.</summary>
    [Fact]
    public void TableEntries_AreAllKnownOpcodes()
    {
        foreach (OpcodeInfo info in OpcodeTable.All)
        {
            Assert.True(Enum.IsDefined(info.Opcode), $"{info.Opcode} is not in the Opcode enum");
        }
    }

    /// <summary>Looks up an opcode that must exist, so the tests read without null juggling.</summary>
    private static OpcodeInfo Get(Opcode opcode)
    {
        Assert.True(OpcodeTable.TryGet(opcode, out OpcodeInfo? info), $"{opcode} is missing from the table");
        return info!.Value;
    }
}
