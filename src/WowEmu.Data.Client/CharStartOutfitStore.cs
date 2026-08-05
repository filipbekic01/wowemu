namespace WowEmu.Data.Client;

/// <summary>
/// One row of <c>CharStartOutfit.dbc</c>: what a race, class and gender begins with.
/// </summary>
/// <remarks>
/// <b>This, not <c>playercreateinfo_item</c>, is where starting gear lives.</b> The world table
/// carries a single row in the whole database — extras a server has chosen to add — and reading it
/// alone produces a naked character with no error anywhere.
/// </remarks>
public sealed record CharStartOutfitEntry(uint Id, byte Race, byte Class, byte Gender, int[] ItemIds);

/// <summary>
/// <c>CharStartOutfit.dbc</c>, keyed the way it is actually looked up.
/// </summary>
/// <remarks>
/// A <see cref="DbcStore{TEntry}"/> is keyed by the file's id, and nothing ever asks for an outfit
/// by id — the question is always "what does a female dwarf hunter start with". So this keeps its
/// own composite index rather than making every caller scan.
/// </remarks>
public sealed class CharStartOutfitStore
{
    /// <summary>How many item slots a row carries. <c>MAX_OUTFIT_ITEMS</c>.</summary>
    public const int MaxItems = 24;

    /// <summary>
    /// <c>d</c> then three <c>b</c>, then a skipped byte, then 24 ints.
    /// </summary>
    /// <remarks>
    /// <b>Race, class and gender are single bytes, not words.</b> Reading them as <c>i</c> would put
    /// the first item id 9 bytes late and every row would look like an outfit for race 0. The 76
    /// trailing columns are display ids and inventory slots the client works out for itself.
    /// </remarks>
    private const string Format =
        "dbbbX" + "iiiiiiiiiiiiiiiiiiiiiiii" +
        "xxxxxxxxxxxxxxxxxxxxxxxx" + "xxxxxxxxxxxxxxxxxxxxxxxx";

    /// <summary>Field index of the first item id: id, three bytes and a skipped one come first.</summary>
    private const int FirstItemField = 5;

    private readonly Dictionary<(byte Race, byte Class, byte Gender), CharStartOutfitEntry> _byWho = [];

    private CharStartOutfitStore(DbcStore<CharStartOutfitEntry> rows)
    {
        Rows = rows;

        foreach (CharStartOutfitEntry entry in rows.Entries)
        {
            // Later rows win, which is upstream's behaviour: the file holds one row per
            // race/class/gender and a duplicate would be a broken extract, not a variant.
            _byWho[(entry.Race, entry.Class, entry.Gender)] = entry;
        }
    }

    /// <summary>Every row, by the file's own id.</summary>
    public DbcStore<CharStartOutfitEntry> Rows { get; }

    public int Count => _byWho.Count;

    /// <summary>What one race, class and gender starts with.</summary>
    public bool TryGet(byte race, byte characterClass, byte gender, out CharStartOutfitEntry? outfit) =>
        _byWho.TryGetValue((race, characterClass, gender), out outfit);

    /// <summary>The item ids of one outfit, skipping the empty slots.</summary>
    /// <remarks>
    /// <b>An unused slot is <c>-1</c>, not zero</b>, and the column is signed for that reason.
    /// Reading it unsigned turns every gap into item 4,294,967,295.
    /// </remarks>
    public IEnumerable<uint> ItemsFor(byte race, byte characterClass, byte gender)
    {
        if (!TryGet(race, characterClass, gender, out CharStartOutfitEntry? outfit) || outfit is null)
        {
            yield break;
        }

        foreach (int itemId in outfit.ItemIds)
        {
            if (itemId > 0)
            {
                yield return (uint)itemId;
            }
        }
    }

    public static CharStartOutfitStore Load(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);

        return new CharStartOutfitStore(DbcStore<CharStartOutfitEntry>.Load(
            Path.Combine(directory, "CharStartOutfit.dbc"),
            Format,
            idField: 0,
            (in DbcRecord record) =>
            {
                int[] items = new int[MaxItems];

                for (int i = 0; i < items.Length; i++)
                {
                    items[i] = record.GetInt32(FirstItemField + i);
                }

                return new CharStartOutfitEntry(
                    Id: record.GetUInt32(0),
                    Race: record.GetByte(1),
                    Class: record.GetByte(2),
                    Gender: record.GetByte(3),
                    ItemIds: items);
            }));
    }

    public override string ToString() =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Count} starting outfits");
}
