using WowEmu.Core;
using WowEmu.Data.Db;
using WowEmu.Protocol;
using WowEmu.WorldServer;

namespace WowEmu.Tests.Unit;

/// <summary>
/// <c>SMSG_CHAR_ENUM</c>'s layout.
/// </summary>
/// <remarks>
/// The client reads a fixed number of fields per character with no length prefix anywhere, so one
/// missing or extra field shifts every byte after it. The failure is not an error — it is a
/// character screen full of nonsense, or a disconnect with no explanation.
/// </remarks>
public sealed class CharacterListTests
{
    [Fact]
    public void EmptyList_IsASingleZeroByte()
    {
        PacketWriter writer = new();
        CharacterList.Write(writer, []);

        Assert.Equal([0x00], writer.ToArray());
    }

    [Fact]
    public void CharacterGuid_IsAPlayerGuid()
    {
        PacketWriter writer = new();
        CharacterList.Write(writer, [Sample(id: 42)]);

        PacketReader reader = new(writer.WrittenSpan);
        Assert.True(reader.TryReadUInt8(out byte count));
        Assert.Equal(1, count);

        Assert.True(reader.TryReadUInt64(out ulong raw));

        ObjectGuid guid = new(raw);
        Assert.True(guid.IsPlayer);
        Assert.Equal(42u, guid.Counter);
    }

    /// <summary>
    /// Walks the whole record field by field. If a field is added, removed or reordered, this test
    /// fails at the point of divergence rather than leaving the client to discover it.
    /// </summary>
    [Fact]
    public void Record_HasEveryFieldInOrder()
    {
        CharacterSummary character = Sample(id: 7) with
        {
            Name = "Thrall",
            Race = 2,
            Class = 7,
            Gender = 1,
            Skin = 3,
            Face = 4,
            HairStyle = 5,
            HairColor = 6,
            FacialStyle = 8,
            Level = 80,
            Zone = 215,
            Map = 1,
            PositionX = -618.5f,
            PositionY = -4251.7f,
            PositionZ = 38.7f,
            GuildId = 99,
            AtLoginFlags = 0,
        };

        PacketWriter writer = new();
        CharacterList.Write(writer, [character]);

        PacketReader reader = new(writer.WrittenSpan);

        Assert.True(reader.TryReadUInt8(out byte count));
        Assert.Equal(1, count);

        Assert.True(reader.TryReadUInt64(out _));
        Assert.True(reader.TryReadCString(out string name));
        Assert.Equal("Thrall", name);

        AssertNextBytes(ref reader, 2, 7, 1);              // race, class, gender
        AssertNextBytes(ref reader, 3, 4, 5, 6, 8);        // appearance
        AssertNextBytes(ref reader, 80);                   // level

        Assert.True(reader.TryReadUInt32(out uint zone));
        Assert.Equal(215u, zone);

        Assert.True(reader.TryReadUInt32(out uint map));
        Assert.Equal(1u, map);

        // Position is three floats; skip them by their width.
        reader.Skip(12);

        Assert.True(reader.TryReadUInt32(out uint guildId));
        Assert.Equal(99u, guildId);

        Assert.True(reader.TryReadUInt32(out _));          // character flags
        Assert.True(reader.TryReadUInt32(out _));          // customize flags
        Assert.True(reader.TryReadUInt8(out byte firstLogin));
        Assert.Equal(0, firstLogin);

        reader.Skip(12);                                   // pet display, level, family

        // Every equipment slot is present even with no items: the count is part of the format.
        for (int slot = 0; slot < CharacterList.EquipmentSlots; slot++)
        {
            Assert.True(reader.TryReadUInt32(out _));      // display id
            Assert.True(reader.TryReadUInt8(out _));       // inventory type
            Assert.True(reader.TryReadUInt32(out _));      // enchant
        }

        Assert.True(reader.Ok);
        Assert.Equal(0, reader.Remaining);
    }

    /// <summary>
    /// A character that has never logged in reports zone 0, so the client shows no location rather
    /// than a stale one.
    /// </summary>
    [Fact]
    public void FirstLogin_HidesTheZoneAndSetsTheFlag()
    {
        CharacterSummary character = Sample(id: 1) with
        {
            Zone = 12,
            AtLoginFlags = CharacterList.AtLoginFirst,
        };

        PacketWriter writer = new();
        CharacterList.Write(writer, [character]);

        PacketReader reader = new(writer.WrittenSpan);
        reader.Skip(1 + 8);
        Assert.True(reader.TryReadCString(out _));
        reader.Skip(3 + 5 + 1);

        Assert.True(reader.TryReadUInt32(out uint zone));
        Assert.Equal(0u, zone);

        reader.Skip(4 + 12 + 4 + 4 + 4);
        Assert.True(reader.TryReadUInt8(out byte firstLogin));
        Assert.Equal(1, firstLogin);
    }

    [Fact]
    public void SeveralCharacters_AreAllWritten()
    {
        PacketWriter writer = new();
        CharacterList.Write(writer, [Sample(1), Sample(2), Sample(3)]);

        Assert.Equal(3, writer.ToArray()[0]);
    }

    private static void AssertNextBytes(ref PacketReader reader, params byte[] expected)
    {
        foreach (byte value in expected)
        {
            Assert.True(reader.TryReadUInt8(out byte actual));
            Assert.Equal(value, actual);
        }
    }

    private static CharacterSummary Sample(uint id) => new(
        id, "Testchar", 1, 1, 0, 0, 0, 0, 0, 0, 1, 12, 0, 0f, 0f, 0f, 0, 0, 0);
}

/// <summary>Character name rules, which the server re-applies because the client is not trusted.</summary>
public sealed class CharacterNameTests
{
    [Theory]
    [InlineData("Thrall", "Thrall")]
    [InlineData("THRALL", "Thrall")]
    [InlineData("thrall", "Thrall")]
    [InlineData("tHrAlL", "Thrall")]
    public void ValidNames_AreNormalizedToTitleCase(string input, string expected)
    {
        Assert.True(CharacterName.TryNormalize(input, out string normalized));
        Assert.Equal(expected, normalized);
    }

    /// <summary>
    /// Normalization is not cosmetic: the name column uses a binary collation, so without it
    /// "Thrall" and "THRALL" would be two different characters that look identical in the list.
    /// </summary>
    [Fact]
    public void CaseVariants_NormalizeToTheSameName()
    {
        Assert.True(CharacterName.TryNormalize("THRALL", out string upper));
        Assert.True(CharacterName.TryNormalize("thrall", out string lower));

        Assert.Equal(upper, lower);
    }

    [Theory]
    [InlineData("")]
    [InlineData("A")]                       // too short
    [InlineData("Thisnameistoolong")]       // over 12
    [InlineData("Thr4ll")]                  // digits
    [InlineData("Thr all")]                 // space
    [InlineData("Thr'all")]                 // punctuation
    [InlineData("Thrall\n")]                // control character
    [InlineData(null)]
    public void InvalidNames_AreRejected(string? input)
    {
        Assert.False(CharacterName.TryNormalize(input, out _));
    }

    [Fact]
    public void BoundaryLengths_AreAccepted()
    {
        Assert.True(CharacterName.TryNormalize("Ab", out _));                 // exactly the minimum
        Assert.True(CharacterName.TryNormalize("Abcdefghijkl", out _));       // exactly the maximum
        Assert.False(CharacterName.TryNormalize("Abcdefghijklm", out _));     // one over
    }
}
