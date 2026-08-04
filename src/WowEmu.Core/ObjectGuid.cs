using System.Globalization;

namespace WowEmu.Core;

/// <summary>
/// The type field of an <see cref="ObjectGuid"/> — the top 16 bits.
/// </summary>
/// <remarks>
/// These values are the client's, not ours, which is why they look arbitrary.
/// <see cref="Container"/> deliberately shares a value with <see cref="Item"/>: that is how the
/// client works, and code that switches on the high word must not assume the values are distinct.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1069:Enums values should not be duplicated",
    Justification = "Container and Item genuinely share a value in the 3.3.5a client; naming both documents that.")]
public enum HighGuid : ushort
{
    Player = 0x0000,
    Item = 0x4000,
    Container = 0x4000,
    Instance = 0x1F40,
    Group = 0x1F50,
    MoTransport = 0x1FC0,
    DynamicObject = 0xF100,
    Corpse = 0xF101,
    GameObject = 0xF110,
    Transport = 0xF120,
    Unit = 0xF130,
    Pet = 0xF140,
    Vehicle = 0xF150,
}

/// <summary>
/// A 64-bit object identifier: <c>high</c> in bits 48-63, <c>entry</c> in bits 24-47, and a counter
/// in the low bits.
/// </summary>
/// <remarks>
/// Port of <c>src/server/game/Entities/Object/ObjectGuid.h</c>.
/// <para>
/// The counter is <b>24 bits wide for types that carry an entry and 32 bits wide for those that do
/// not</b> — see <see cref="HasEntry(HighGuid)"/>. A player guid's counter therefore overlaps the
/// bits an entry would occupy, which is why <see cref="Entry"/> returns 0 rather than garbage for
/// those types.
/// </para>
/// <para>
/// Constructing with a zero counter yields <see cref="Empty"/>, entry and high word included. That
/// is upstream's behaviour and some call sites rely on it to mean "no object".
/// </para>
/// </remarks>
public readonly record struct ObjectGuid(ulong Value) : IComparable<ObjectGuid>
{
    /// <summary>The null guid.</summary>
    public static readonly ObjectGuid Empty;

    private const ulong EntryMask = 0x0000_0000_00FF_FFFF;
    private const ulong CounterMaskWithEntry = 0x0000_0000_00FF_FFFF;
    private const ulong CounterMaskWithoutEntry = 0x0000_0000_FFFF_FFFF;

    /// <summary>Builds a guid for a type that carries an entry (creatures, gameobjects, …).</summary>
    public static ObjectGuid Create(HighGuid high, uint entry, uint counter) =>
        counter == 0
            ? Empty
            : new ObjectGuid(counter | ((ulong)entry << 24) | ((ulong)high << 48));

    /// <summary>Builds a guid for a type that carries no entry (players, items, groups, …).</summary>
    public static ObjectGuid Create(HighGuid high, uint counter) =>
        counter == 0 ? Empty : new ObjectGuid(counter | ((ulong)high << 48));

    public bool IsEmpty => Value == 0;

    public HighGuid High => (HighGuid)((Value >> 48) & 0xFFFF);

    /// <summary>The entry, or 0 for types whose low bits are all counter.</summary>
    public uint Entry => HasEntry(High) ? (uint)((Value >> 24) & EntryMask) : 0;

    public uint Counter => HasEntry(High)
        ? (uint)(Value & CounterMaskWithEntry)
        : (uint)(Value & CounterMaskWithoutEntry);

    /// <summary>Largest counter this guid's type can hold before it wraps into the entry bits.</summary>
    public uint MaxCounter => MaxCounterFor(High);

    public bool IsPlayer => !IsEmpty && High == HighGuid.Player;

    public bool IsCreature => High == HighGuid.Unit;

    public bool IsPet => High == HighGuid.Pet;

    public bool IsVehicle => High == HighGuid.Vehicle;

    public bool IsCreatureOrPet => IsCreature || IsPet;

    public bool IsCreatureOrVehicle => IsCreature || IsVehicle;

    /// <summary>Creature, pet or vehicle — anything that lives on a map and is not a player.</summary>
    public bool IsAnyTypeCreature => IsCreature || IsPet || IsVehicle;

    public bool IsUnit => IsAnyTypeCreature || IsPlayer;

    public bool IsItem => High == HighGuid.Item;

    public bool IsGameObject => High == HighGuid.GameObject;

    public bool IsDynamicObject => High == HighGuid.DynamicObject;

    public bool IsCorpse => High == HighGuid.Corpse;

    public bool IsTransport => High == HighGuid.Transport;

    public bool IsMoTransport => High == HighGuid.MoTransport;

    public bool IsInstance => High == HighGuid.Instance;

    public bool IsGroup => High == HighGuid.Group;

    public static uint MaxCounterFor(HighGuid high) => HasEntry(high) ? 0x00FF_FFFF : 0xFFFF_FFFF;

    /// <summary>
    /// Whether this type stores an entry in bits 24-47. The types that do not give their counter
    /// the full low 32 bits instead.
    /// </summary>
    public static bool HasEntry(HighGuid high) => high switch
    {
        HighGuid.Item or            // == HighGuid.Container
        HighGuid.Player or
        HighGuid.DynamicObject or
        HighGuid.Corpse or
        HighGuid.MoTransport or
        HighGuid.Instance or
        HighGuid.Group => false,
        _ => true,
    };

    public int CompareTo(ObjectGuid other) => Value.CompareTo(other.Value);

    public static bool operator <(ObjectGuid left, ObjectGuid right) => left.CompareTo(right) < 0;

    public static bool operator <=(ObjectGuid left, ObjectGuid right) => left.CompareTo(right) <= 0;

    public static bool operator >(ObjectGuid left, ObjectGuid right) => left.CompareTo(right) > 0;

    public static bool operator >=(ObjectGuid left, ObjectGuid right) => left.CompareTo(right) >= 0;

    public override string ToString()
    {
        if (IsEmpty)
        {
            return "GUID Empty";
        }

        string text = string.Create(
            CultureInfo.InvariantCulture,
            $"GUID Full: 0x{Value:X16} Type: {High}");

        return HasEntry(High)
            ? string.Create(CultureInfo.InvariantCulture, $"{text} Entry: {Entry} Low: {Counter}")
            : string.Create(CultureInfo.InvariantCulture, $"{text} Low: {Counter}");
    }
}
