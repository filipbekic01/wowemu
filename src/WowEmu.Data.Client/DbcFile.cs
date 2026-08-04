using System.Buffers.Binary;
using System.Text;

namespace WowEmu.Data.Client;

/// <summary>
/// The characters a DBC format string is built from.
/// </summary>
/// <remarks>
/// One character per column in the file. Everything is four bytes wide except
/// <c>Byte</c> and <see cref="UnusedByte"/> — that asymmetry is why a format string cannot
/// be validated by length alone, and why <see cref="DbcFile"/> checks the computed record size
/// against the one in the header.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1720:Identifier contains type name",
    Justification = "These name the DBC format's own column types; renaming them would obscure the mapping to upstream's DBCfmt.h.")]
public static class DbcFieldFormat
{
    /// <summary>Unused or unknown, four bytes.</summary>
    public const char Unused = 'x';

    /// <summary>Unused or unknown, one byte.</summary>
    public const char UnusedByte = 'X';

    /// <summary>Offset into the string block.</summary>
    public const char String = 's';

    public const char Float = 'f';

    public const char Int = 'i';

    public const char Byte = 'b';

    /// <summary>Sort key; present in the file but not surfaced.</summary>
    public const char Sort = 'd';

    /// <summary>The record's id — the column the store is indexed by.</summary>
    public const char Index = 'n';

    /// <summary>Boolean stored as four bytes.</summary>
    public const char Logic = 'l';
}

/// <summary>
/// A parsed <c>.dbc</c> file: a fixed-width record table plus a string block.
/// </summary>
/// <remarks>
/// Port of <c>src/common/DataStores/DBCFileLoader.{h,cpp}</c>.
/// <para>
/// The format is spartan — a 20-byte header, <c>recordCount × recordSize</c> bytes of records, then
/// a blob of NUL-terminated strings that string columns index into. The file itself carries no
/// column types, which is what the format string supplies; a format string that disagrees with the
/// file produces plausible garbage rather than an error, so both the column count and the computed
/// record size are checked before a single field is read.
/// </para>
/// </remarks>
public sealed class DbcFile
{
    /// <summary>'WDBC', little-endian.</summary>
    public const uint Magic = 0x43424457;

    /// <summary>Magic, record count, field count, record size, string block size.</summary>
    public const int HeaderSize = 20;

    /// <summary>Locale slots in a localized string group.</summary>
    /// <remarks>
    /// Sixteen strings followed by a flags column. A store's format spells all sixteen out as
    /// <c>s</c> and the flags as <c>x</c>, which is why format strings for tables with names are so
    /// long.
    /// </remarks>
    public const int LocaleCount = 16;

    private readonly byte[] _records;
    private readonly byte[] _stringBlock;
    private readonly int[] _fieldOffsets;

    private DbcFile(
        string name,
        byte[] records,
        byte[] stringBlock,
        int[] fieldOffsets,
        int recordCount,
        int recordSize,
        int fieldCount)
    {
        Name = name;
        _records = records;
        _stringBlock = stringBlock;
        _fieldOffsets = fieldOffsets;
        RecordCount = recordCount;
        RecordSize = recordSize;
        FieldCount = fieldCount;
    }

    /// <summary>File name, for error messages.</summary>
    public string Name { get; }

    public int RecordCount { get; }

    public int RecordSize { get; }

    public int FieldCount { get; }

    /// <summary>Reads and validates a DBC file against a format string.</summary>
    /// <exception cref="InvalidDataException">
    /// The magic is wrong, the file is truncated, or the format does not describe this file.
    /// </exception>
    public static DbcFile Load(string path, string format)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(format);

        byte[] bytes = File.ReadAllBytes(path);
        string name = Path.GetFileName(path);

        if (bytes.Length < HeaderSize)
        {
            throw new InvalidDataException($"{name}: too short to be a DBC file ({bytes.Length} bytes).");
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (magic != Magic)
        {
            throw new InvalidDataException($"{name}: not a DBC file (magic 0x{magic:X8}).");
        }

        int recordCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4));
        int fieldCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8));
        int recordSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12));
        int stringSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(16));

        if (format.Length != fieldCount)
        {
            throw new InvalidDataException(
                $"{name}: format describes {format.Length} columns but the file has {fieldCount}.");
        }

        int[] offsets = BuildFieldOffsets(format, out int formatSize);

        if (formatSize != recordSize)
        {
            throw new InvalidDataException(
                $"{name}: format implies {formatSize}-byte records but the file says {recordSize}.");
        }

        long expected = (long)HeaderSize + ((long)recordCount * recordSize) + stringSize;
        if (bytes.Length < expected)
        {
            throw new InvalidDataException(
                $"{name}: truncated — expected at least {expected} bytes, found {bytes.Length}.");
        }

        byte[] records = bytes.AsSpan(HeaderSize, recordCount * recordSize).ToArray();
        byte[] strings = bytes.AsSpan(HeaderSize + (recordCount * recordSize), stringSize).ToArray();

        return new DbcFile(name, records, strings, offsets, recordCount, recordSize, fieldCount);
    }

    /// <summary>Byte offsets of each column within a record, and the total record width.</summary>
    private static int[] BuildFieldOffsets(string format, out int recordSize)
    {
        int[] offsets = new int[format.Length];
        int offset = 0;

        for (int i = 0; i < format.Length; i++)
        {
            offsets[i] = offset;

            offset += format[i] switch
            {
                DbcFieldFormat.Byte or DbcFieldFormat.UnusedByte => sizeof(byte),
                DbcFieldFormat.Float or DbcFieldFormat.Int or DbcFieldFormat.String or
                DbcFieldFormat.Sort or DbcFieldFormat.Index or DbcFieldFormat.Logic or
                DbcFieldFormat.Unused => sizeof(uint),
                _ => throw new InvalidDataException($"Unknown DBC format character '{format[i]}'."),
            };
        }

        recordSize = offset;
        return offsets;
    }

    /// <summary>Reads one record. The index is positional, not the record's id.</summary>
    public DbcRecord GetRecord(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, RecordCount);

        return new DbcRecord(this, index * RecordSize);
    }

    /// <summary>
    /// Runs <paramref name="action"/> over every record, in file order.
    /// </summary>
    /// <remarks>
    /// A callback rather than an <c>IEnumerable</c> because <see cref="DbcRecord"/> is a ref struct
    /// and cannot be yielded — which is the point: a record is a view over the file's bytes, not a
    /// copy, so it must never outlive the loop that produced it.
    /// </remarks>
    public void ForEachRecord(DbcRecordAction action)
    {
        ArgumentNullException.ThrowIfNull(action);

        for (int i = 0; i < RecordCount; i++)
        {
            action(new DbcRecord(this, i * RecordSize));
        }
    }

    internal ReadOnlySpan<byte> RecordBytes(int recordOffset) => _records.AsSpan(recordOffset, RecordSize);

    internal int FieldOffset(int field)
    {
        if ((uint)field >= (uint)_fieldOffsets.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(field), $"{Name}: column {field} is out of range ({FieldCount} columns).");
        }

        return _fieldOffsets[field];
    }

    /// <summary>
    /// Resolves a string-block offset. An offset past the end is treated as an empty string rather
    /// than an error — real files contain them.
    /// </summary>
    internal string GetString(uint offset)
    {
        if (offset == 0 || offset >= (uint)_stringBlock.Length)
        {
            return string.Empty;
        }

        ReadOnlySpan<byte> remaining = _stringBlock.AsSpan((int)offset);
        int terminator = remaining.IndexOf((byte)0);

        return Encoding.UTF8.GetString(terminator < 0 ? remaining : remaining[..terminator]);
    }
}

/// <summary>Called once per record by <see cref="DbcFile.ForEachRecord"/>.</summary>
public delegate void DbcRecordAction(in DbcRecord record);

/// <summary>One row of a <see cref="DbcFile"/>.</summary>
public readonly ref struct DbcRecord
{
    private readonly DbcFile _file;
    private readonly ReadOnlySpan<byte> _bytes;

    internal DbcRecord(DbcFile file, int recordOffset)
    {
        _file = file;
        _bytes = file.RecordBytes(recordOffset);
    }

    public uint GetUInt32(int field) =>
        BinaryPrimitives.ReadUInt32LittleEndian(_bytes[_file.FieldOffset(field)..]);

    public int GetInt32(int field) =>
        BinaryPrimitives.ReadInt32LittleEndian(_bytes[_file.FieldOffset(field)..]);

    public float GetFloat(int field) =>
        BinaryPrimitives.ReadSingleLittleEndian(_bytes[_file.FieldOffset(field)..]);

    public byte GetByte(int field) => _bytes[_file.FieldOffset(field)];

    public bool GetBool(int field) => GetUInt32(field) != 0;

    /// <summary>Reads a single string column.</summary>
    public string GetString(int field) => _file.GetString(GetUInt32(field));

    /// <summary>
    /// Reads a localized string group, preferring <paramref name="locale"/> and falling back to the
    /// first slot that has anything in it.
    /// </summary>
    /// <remarks>
    /// A 3.3.5a client ships one locale, so fifteen of the sixteen slots are empty in practice.
    /// Falling back rather than returning an empty string is what makes a store load correctly
    /// regardless of which locale the client was extracted from.
    /// </remarks>
    public string GetLocalizedString(int field, int locale = 0)
    {
        if ((uint)locale < DbcFile.LocaleCount)
        {
            string preferred = GetString(field + locale);

            if (preferred.Length > 0)
            {
                return preferred;
            }
        }

        for (int slot = 0; slot < DbcFile.LocaleCount; slot++)
        {
            string value = GetString(field + slot);

            if (value.Length > 0)
            {
                return value;
            }
        }

        return string.Empty;
    }
}
