using WowEmu.Data.Db;

namespace WowEmu.AuthServer;

/// <summary>A realm as advertised to the client.</summary>
public sealed class Realm
{
    public required byte Id { get; init; }

    public required string Name { get; init; }

    /// <summary>
    /// What the client is told to connect to, formatted <c>"host:port"</c>. The client parses this
    /// string directly, so the formatting is part of the protocol.
    /// </summary>
    public required string ClientAddress { get; init; }

    public RealmType Type { get; init; } = RealmType.Normal;

    public RealmFlags Flags { get; init; } = RealmFlags.None;

    public float PopulationLevel { get; init; }

    public byte Timezone { get; init; } = 1;

    public byte AllowedSecurityLevel { get; init; }

    /// <summary>
    /// Client build this realm serves. A realm whose build differs from the connecting client is
    /// shown as offline rather than hidden.
    /// </summary>
    public ushort Build { get; init; } = AuthProtocol.SupportedBuild;

    /// <summary>Maps a stored row onto the wire-facing shape.</summary>
    public static Realm FromRegistration(RealmRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        return new Realm
        {
            Id = registration.Id,
            Name = registration.Name,
            ClientAddress = $"{registration.Address}:{registration.Port}",
            Type = (RealmType)registration.Type,
            Flags = (RealmFlags)registration.Flags,
            PopulationLevel = registration.PopulationLevel,
            Timezone = registration.Timezone,
            AllowedSecurityLevel = registration.AllowedSecurityLevel,
            Build = registration.Build,
        };
    }
}

/// <summary>
/// The set of realms this logon server advertises, cached in memory and refreshed from the
/// <c>realmlist</c> table.
/// </summary>
/// <remarks>
/// Sessions read this from many tasks at once while the refresher rewrites it. The array is
/// swapped wholesale rather than mutated, so a reader sees either the old list or the new one and
/// never a torn one — no lock on the read path, which is the hot one.
/// </remarks>
public sealed class RealmList
{
    private volatile Realm[] _realms = [];

    public IReadOnlyList<Realm> Realms => _realms;

    public void Update(IEnumerable<RealmRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);

        _realms = [.. registrations.Select(Realm.FromRegistration)];
    }
}
