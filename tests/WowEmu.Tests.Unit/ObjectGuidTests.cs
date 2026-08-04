using WowEmu.Core;

namespace WowEmu.Tests.Unit;

/// <summary>
/// The guid bit layout, which the client dictates and which two different types read two different
/// ways.
/// </summary>
public sealed class ObjectGuidTests
{
    [Fact]
    public void Create_WithEntry_PacksHighEntryAndCounter()
    {
        ObjectGuid guid = ObjectGuid.Create(HighGuid.Unit, entry: 299, counter: 12345);

        Assert.Equal(HighGuid.Unit, guid.High);
        Assert.Equal(299u, guid.Entry);
        Assert.Equal(12345u, guid.Counter);
    }

    [Fact]
    public void Create_WithEntry_LaysOutBitsExactly()
    {
        ObjectGuid guid = ObjectGuid.Create(HighGuid.Unit, entry: 0x123456, counter: 0xABCDEF);

        // high << 48 | entry << 24 | counter
        Assert.Equal(0xF130_1234_56AB_CDEFul, guid.Value);
    }

    /// <summary>
    /// Player guids have no entry, so their counter gets the full low 32 bits. Reading an entry out
    /// of one would return part of the counter, which is why <see cref="ObjectGuid.Entry"/> guards.
    /// </summary>
    [Fact]
    public void PlayerGuid_HasNoEntry_AndA32BitCounter()
    {
        ObjectGuid guid = ObjectGuid.Create(HighGuid.Player, counter: 0xFFFF_FFFF);

        Assert.True(guid.IsPlayer);
        Assert.Equal(0u, guid.Entry);
        Assert.Equal(0xFFFF_FFFFu, guid.Counter);
        Assert.Equal(0xFFFF_FFFFu, guid.MaxCounter);
    }

    [Fact]
    public void CreatureCounter_IsCappedAt24Bits()
    {
        Assert.Equal(0x00FF_FFFFu, ObjectGuid.MaxCounterFor(HighGuid.Unit));
        Assert.Equal(0xFFFF_FFFFu, ObjectGuid.MaxCounterFor(HighGuid.Item));
    }

    /// <summary>A zero counter collapses the whole guid to empty — upstream's behaviour, relied on.</summary>
    [Fact]
    public void ZeroCounter_ProducesEmptyGuid()
    {
        Assert.True(ObjectGuid.Create(HighGuid.Unit, entry: 299, counter: 0).IsEmpty);
        Assert.True(ObjectGuid.Create(HighGuid.Player, counter: 0).IsEmpty);
        Assert.Equal(ObjectGuid.Empty, ObjectGuid.Create(HighGuid.Unit, 299, 0));
    }

    [Fact]
    public void EmptyGuid_IsNotAPlayer()
    {
        // Player's high word is 0x0000, so an empty guid would otherwise look like player 0.
        Assert.False(ObjectGuid.Empty.IsPlayer);
        Assert.True(ObjectGuid.Empty.IsEmpty);
    }

    [Theory]
    [InlineData(HighGuid.Item, false)]
    [InlineData(HighGuid.Player, false)]
    [InlineData(HighGuid.DynamicObject, false)]
    [InlineData(HighGuid.Corpse, false)]
    [InlineData(HighGuid.MoTransport, false)]
    [InlineData(HighGuid.Instance, false)]
    [InlineData(HighGuid.Group, false)]
    [InlineData(HighGuid.Unit, true)]
    [InlineData(HighGuid.Pet, true)]
    [InlineData(HighGuid.Vehicle, true)]
    [InlineData(HighGuid.GameObject, true)]
    [InlineData(HighGuid.Transport, true)]
    public void HasEntry_MatchesUpstreamTable(HighGuid high, bool expected)
    {
        Assert.Equal(expected, ObjectGuid.HasEntry(high));
    }

    [Fact]
    public void ContainerAndItem_ShareAValue()
    {
        // Not a mistake in the enum: the client uses one value for both.
        Assert.Equal(HighGuid.Item, HighGuid.Container);
    }

    [Fact]
    public void TypePredicates_AgreeWithHighWord()
    {
        Assert.True(ObjectGuid.Create(HighGuid.Unit, 1, 1).IsCreature);
        Assert.True(ObjectGuid.Create(HighGuid.Pet, 1, 1).IsPet);
        Assert.True(ObjectGuid.Create(HighGuid.Vehicle, 1, 1).IsVehicle);
        Assert.True(ObjectGuid.Create(HighGuid.Unit, 1, 1).IsUnit);
        Assert.True(ObjectGuid.Create(HighGuid.Player, 1).IsUnit);
        Assert.True(ObjectGuid.Create(HighGuid.GameObject, 1, 1).IsGameObject);
        Assert.True(ObjectGuid.Create(HighGuid.Item, 1).IsItem);
        Assert.True(ObjectGuid.Create(HighGuid.Corpse, 1).IsCorpse);

        Assert.False(ObjectGuid.Create(HighGuid.Unit, 1, 1).IsPlayer);
        Assert.False(ObjectGuid.Create(HighGuid.Player, 1).IsCreature);
    }

    [Fact]
    public void Ordering_IsByRawValue()
    {
        ObjectGuid low = new(10);
        ObjectGuid high = new(20);

        Assert.True(low < high);
        Assert.True(high > low);
        Assert.True(low <= new ObjectGuid(10));
        Assert.Equal(-1, low.CompareTo(high));
    }

    [Fact]
    public void ToString_NamesTheTypeAndParts()
    {
        string text = ObjectGuid.Create(HighGuid.Unit, entry: 299, counter: 5).ToString();

        Assert.Contains("Unit", text, StringComparison.Ordinal);
        Assert.Contains("Entry: 299", text, StringComparison.Ordinal);
        Assert.Contains("Low: 5", text, StringComparison.Ordinal);
        Assert.Equal("GUID Empty", ObjectGuid.Empty.ToString());
    }
}
