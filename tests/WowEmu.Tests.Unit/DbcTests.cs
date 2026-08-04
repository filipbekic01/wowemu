using WowEmu.Data.Client;

namespace WowEmu.Tests.Unit;

/// <summary>
/// Marks a test that reads extracted client data.
/// </summary>
/// <remarks>
/// The data is tens of gigabytes and comes from the reader's own retail client, so it cannot be
/// committed and will not exist on every machine. These tests skip rather than fail when it is
/// absent — a red suite for "you have not extracted a WoW client" trains people to ignore red.
/// </remarks>
public sealed class RequiresClientDataFactAttribute : FactAttribute
{
    public RequiresClientDataFactAttribute()
    {
        if (!ClientData.Available)
        {
            Skip = $"no extracted client data at {ClientData.DbcDirectory}";
        }
    }
}

/// <summary>Marks a theory that reads extracted client data. See <see cref="RequiresClientDataFactAttribute"/>.</summary>
public sealed class RequiresClientDataTheoryAttribute : TheoryAttribute
{
    public RequiresClientDataTheoryAttribute()
    {
        if (!ClientData.Available)
        {
            Skip = $"no extracted client data at {ClientData.DataDirectory}";
        }
    }
}

/// <summary>Marks a test that reads extracted map tiles.</summary>
public sealed class RequiresMapsFactAttribute : FactAttribute
{
    public RequiresMapsFactAttribute()
    {
        if (!ClientData.MapsAvailable)
        {
            Skip = $"no extracted map tiles at {ClientData.DataDirectory}/maps";
        }
    }
}

/// <inheritdoc cref="RequiresMapsFactAttribute"/>
public sealed class RequiresMapsTheoryAttribute : TheoryAttribute
{
    public RequiresMapsTheoryAttribute()
    {
        if (!ClientData.MapsAvailable)
        {
            Skip = $"no extracted map tiles at {ClientData.DataDirectory}/maps";
        }
    }
}

/// <summary>Locates the extracted client data relative to the repository.</summary>
public static class ClientData
{
    static ClientData()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WowEmu.slnx")))
        {
            directory = directory.Parent;
        }

        DataDirectory = directory is null ? "data" : Path.Combine(directory.FullName, "data");
        DbcDirectory = Path.Combine(DataDirectory, "dbc");

        Available = File.Exists(Path.Combine(DbcDirectory, "ChrRaces.dbc"));
        MapsAvailable = Directory.Exists(Path.Combine(DataDirectory, "maps"))
            && Directory.EnumerateFiles(Path.Combine(DataDirectory, "maps"), "*.map").Any();
    }

    public static string DataDirectory { get; }

    public static string DbcDirectory { get; }

    public static bool Available { get; }

    public static bool MapsAvailable { get; }
}

/// <summary>
/// The DBC loader, against real files.
/// </summary>
/// <remarks>
/// A DBC carries no type information: the format string supplies it, and a wrong one yields values
/// that are plausible but shifted. Asserting known values — Human's display id, the class names —
/// is the only way to prove the columns line up, which is why these tests read the real thing
/// rather than a synthetic fixture.
/// </remarks>
public sealed class DbcStoresTests
{
    [RequiresClientDataFact]
    public void ChrRaces_LoadsTheRacesTheClientHas()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);

        // Ten playable races in WotLK, plus a handful of unplayable rows the client ships.
        Assert.True(stores.Races.Count >= 10, $"only {stores.Races.Count} races loaded");

        Assert.True(stores.Races.TryGet(1, out ChrRacesEntry? human));
        Assert.Equal("Human", human.Name);
        Assert.True(human.IsAlliance);

        Assert.True(stores.Races.TryGet(2, out ChrRacesEntry? orc));
        Assert.Equal("Orc", orc.Name);
        Assert.False(orc.IsAlliance);
    }

    /// <summary>
    /// The display id is the model the client draws. Wrong column here and the character is
    /// invisible — with no error anywhere, which is why it gets its own test.
    /// </summary>
    [RequiresClientDataFact]
    public void ChrRaces_HasDistinctDisplayIdsPerGender()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);
        ChrRacesEntry human = stores.Races.Get(1);

        Assert.NotEqual(0u, human.MaleDisplayId);
        Assert.NotEqual(0u, human.FemaleDisplayId);
        Assert.NotEqual(human.MaleDisplayId, human.FemaleDisplayId);
    }

    [RequiresClientDataFact]
    public void ChrClasses_LoadsAllTenClasses()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);

        Assert.Equal(10, stores.Classes.Count);

        Assert.Equal("Warrior", stores.Classes.Get(1).Name);
        Assert.Equal("Mage", stores.Classes.Get(8).Name);
        Assert.Equal("Death Knight", stores.Classes.Get(6).Name);
    }

    /// <summary>Warriors use rage (power type 1), mages mana (0). A shifted column breaks this.</summary>
    [RequiresClientDataFact]
    public void ChrClasses_HasTheRightPowerTypes()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);

        Assert.Equal(1u, stores.Classes.Get(1).PowerType);
        Assert.Equal(0u, stores.Classes.Get(8).PowerType);
    }

    [RequiresClientDataFact]
    public void Map_LoadsTheContinentsAndInstances()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);

        Assert.True(stores.Maps.Count > 100, $"only {stores.Maps.Count} maps loaded");

        MapEntry azeroth = stores.Maps.Get(0);
        Assert.Equal("Azeroth", azeroth.Directory);
        Assert.True(azeroth.IsContinent);
        Assert.False(azeroth.IsInstance);

        MapEntry kalimdor = stores.Maps.Get(1);
        Assert.Equal("Kalimdor", kalimdor.Directory);

        // Northrend is a WotLK map, so it reports expansion 2.
        Assert.Equal(2u, stores.Maps.Get(571).Expansion);
    }

    [RequiresClientDataFact]
    public void Store_LookupsAgreeWithEachOther()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);

        Assert.True(stores.Races.Contains(1));
        Assert.False(stores.Races.Contains(9999));
        Assert.False(stores.Races.TryGet(9999, out _));
        Assert.Throws<KeyNotFoundException>(() => stores.Races.Get(9999));
    }

    /// <summary>
    /// A format that disagrees with the file must be rejected outright. Upstream asserts on this;
    /// letting it through produces shifted fields and no error.
    /// </summary>
    [RequiresClientDataFact]
    public void WrongFormat_IsRejected()
    {
        string path = Path.Combine(ClientData.DbcDirectory, "ChrRaces.dbc");

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => DbcFile.Load(path, "nii"));

        Assert.Contains("columns", error.Message, StringComparison.Ordinal);
    }

    [RequiresClientDataFact]
    public void Header_MatchesTheFilesOwnGeometry()
    {
        DbcFile file = DbcFile.Load(
            Path.Combine(ClientData.DbcDirectory, "ChrRaces.dbc"),
            "niixiixixxxxiissssssssssssssssxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxi");

        Assert.Equal(69, file.FieldCount);
        Assert.Equal(69 * 4, file.RecordSize);
        Assert.True(file.RecordCount > 0);
    }

    [Fact]
    public void NonDbcFile_IsRejected()
    {
        string path = Path.Combine(Path.GetTempPath(), $"wowemu-not-a-dbc-{Environment.ProcessId}.dbc");
        File.WriteAllBytes(path, [0x00, 0x01, 0x02, 0x03, .. new byte[32]]);

        try
        {
            InvalidDataException error = Assert.Throws<InvalidDataException>(() => DbcFile.Load(path, "n"));
            Assert.Contains("not a DBC file", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MissingDirectory_SaysWhereToPutTheData()
    {
        DirectoryNotFoundException error = Assert.Throws<DirectoryNotFoundException>(
            () => DbcStores.Load(Path.Combine(Path.GetTempPath(), "wowemu-no-such-dbc-dir")));

        Assert.Contains("data/dbc", error.Message, StringComparison.Ordinal);
    }
}
