using System.Buffers.Binary;
using System.Text;

namespace AcbFinder.Core;

/// <summary>
/// Minimal CRI @UTF table reader (big-endian), just enough for the ACB/AWB pipeline.
///
/// Layout assumptions (de-facto structure per vgmtoolbox / SonicAudioTools):
///   0x00 "@UTF" magic
///   0x04 u32 tableSize (bytes after this field)
///   0x08 u16 version, 0x0A u16 rowsOffset,
///   0x0C u32 stringPoolOffset, 0x10 u32 dataPoolOffset,
///   0x14 u32 tableNameOffset (into string pool),
///   0x18 u16 columnCount, 0x1A u16 rowWidth, 0x1C u32 rowCount
///   rowsOffset / stringPoolOffset / dataPoolOffset are relative to 0x08.
/// Column schema entry: u8 flags + u32 name offset (into string pool).
///   Flags high nibble = storage: 0x10 name-only (value null), 0x30 and 0x70 inline
///   constant after the schema entry, 0x50 per-row. Low nibble = type:
///   0=u8 1=s8 2=u16 3=s16 4=u32 5=s32 6=u64 7=s64 8=f32
///   0xA=string (u32 string-pool offset) 0xB=data (u32 data-pool offset + u32 length).
/// Anything outside this set throws InvalidDataException so callers can fall back
/// to byte-pattern heuristics.
/// </summary>
public sealed class CriTable
{
    public string TableName { get; private init; } = "";
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows { get; private init; } = [];

    public static CriTable Parse(byte[] bytes)
    {
        if (bytes.Length < 0x20 ||
            bytes[0] != '@' || bytes[1] != 'U' || bytes[2] != 'T' || bytes[3] != 'F')
            throw new InvalidDataException("@UTF magic not found");

        var span = bytes.AsSpan();
        const int basePos = 8; // pool/row offsets are relative to here
        var rowsOffset = BinaryPrimitives.ReadUInt16BigEndian(span[0x0A..]);
        var stringPoolOffset = BinaryPrimitives.ReadUInt32BigEndian(span[0x0C..]);
        var dataPoolOffset = BinaryPrimitives.ReadUInt32BigEndian(span[0x10..]);
        var tableNameOffset = BinaryPrimitives.ReadUInt32BigEndian(span[0x14..]);
        var columnCount = BinaryPrimitives.ReadUInt16BigEndian(span[0x18..]);
        var rowWidth = BinaryPrimitives.ReadUInt16BigEndian(span[0x1A..]);
        var rowCount = BinaryPrimitives.ReadUInt32BigEndian(span[0x1C..]);

        var stringPool = basePos + (int)stringPoolOffset;
        var dataPool = basePos + (int)dataPoolOffset;
        var rowsEnd = basePos + rowsOffset + (long)rowCount * rowWidth;
        if (stringPool > bytes.Length || dataPool > bytes.Length || rowsEnd > bytes.Length)
            throw new InvalidDataException("@UTF header offsets out of bounds");

        var pos = 0x20;
        var columns = new List<(string Name, byte Storage, byte Type, object? Constant)>(columnCount);
        for (var c = 0; c < columnCount; c++)
        {
            if (pos + 5 > bytes.Length)
                throw new InvalidDataException("@UTF column schema out of bounds");
            var flags = bytes[pos];
            var nameOffset = BinaryPrimitives.ReadUInt32BigEndian(span[(pos + 1)..]);
            pos += 5;

            var storage = (byte)(flags & 0xF0);
            var type = (byte)(flags & 0x0F);
            var name = ReadString(bytes, stringPool + (int)nameOffset);
            object? constant = storage switch
            {
                0x10 or 0x50 => null,
                0x30 or 0x70 => ReadValue(bytes, ref pos, type, stringPool, dataPool),
                _ => throw new InvalidDataException(
                    $"Unsupported @UTF storage flag 0x{storage:X2} for column '{name}'"),
            };
            columns.Add((name, storage, type, constant));
        }

        var rows = new List<IReadOnlyDictionary<string, object?>>((int)rowCount);
        for (var r = 0; r < rowCount; r++)
        {
            var rowPos = basePos + rowsOffset + r * rowWidth;
            var row = new Dictionary<string, object?>(columnCount);
            foreach (var (name, storage, type, constant) in columns)
            {
                row[name] = storage == 0x50
                    ? ReadValue(bytes, ref rowPos, type, stringPool, dataPool)
                    : constant;
            }
            rows.Add(row);
        }

        return new CriTable
        {
            TableName = ReadString(bytes, stringPool + (int)tableNameOffset),
            Rows = rows,
        };
    }

    private static object? ReadValue(byte[] bytes, ref int pos, byte type, int stringPool, int dataPool)
    {
        var span = bytes.AsSpan();
        switch (type)
        {
            case 0: return bytes[pos++];
            case 1: return (sbyte)bytes[pos++];
            case 2: { var v = BinaryPrimitives.ReadUInt16BigEndian(span[pos..]); pos += 2; return v; }
            case 3: { var v = BinaryPrimitives.ReadInt16BigEndian(span[pos..]); pos += 2; return v; }
            case 4: { var v = BinaryPrimitives.ReadUInt32BigEndian(span[pos..]); pos += 4; return v; }
            case 5: { var v = BinaryPrimitives.ReadInt32BigEndian(span[pos..]); pos += 4; return v; }
            case 6: { var v = BinaryPrimitives.ReadUInt64BigEndian(span[pos..]); pos += 8; return v; }
            case 7: { var v = BinaryPrimitives.ReadInt64BigEndian(span[pos..]); pos += 8; return v; }
            case 8: { var v = BinaryPrimitives.ReadSingleBigEndian(span[pos..]); pos += 4; return v; }
            case 0xA:
            {
                var offset = BinaryPrimitives.ReadUInt32BigEndian(span[pos..]);
                pos += 4;
                return ReadString(bytes, stringPool + (int)offset);
            }
            case 0xB:
            {
                var offset = BinaryPrimitives.ReadUInt32BigEndian(span[pos..]);
                var length = BinaryPrimitives.ReadUInt32BigEndian(span[(pos + 4)..]);
                pos += 8;
                if (length == 0)
                    return Array.Empty<byte>();
                var start = dataPool + (int)offset;
                if (start < 0 || start + length > bytes.Length)
                    throw new InvalidDataException("@UTF data blob out of bounds");
                return bytes[start..(start + (int)length)];
            }
            default:
                throw new InvalidDataException($"Unsupported @UTF value type 0x{type:X}");
        }
    }

    private static string ReadString(byte[] bytes, int start)
    {
        if (start < 0 || start >= bytes.Length)
            throw new InvalidDataException("@UTF string offset out of bounds");
        var end = Array.IndexOf(bytes, (byte)0, start);
        if (end < 0)
            throw new InvalidDataException("@UTF string not null-terminated");
        return Encoding.UTF8.GetString(bytes, start, end - start);
    }
}
