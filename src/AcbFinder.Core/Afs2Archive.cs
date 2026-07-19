using System.Buffers.Binary;

namespace AcbFinder.Core;

/// <summary>
/// Minimal AFS2 (AWB) archive reader (little-endian).
///
/// Layout assumptions:
///   0x00 "AFS2", 0x04 u8 version, 0x05 u8 offsetSize (bytes per offset entry),
///   0x06 u8 idSize (bytes per track id), 0x07 padding,
///   0x08 u32 entryCount, 0x0C u16 alignment, 0x0E u16 subkey (per-archive HCA key mixer;
///   see <see cref="Subkey"/>). Then entryCount ids, then (entryCount + 1) offsets. Track i spans
/// [alignUp(offset[i]), offset[i+1]); the raw offset of entry 0 points right after
/// the offset table, alignment padding belongs to no track.
///
/// <see cref="ParseIndex"/> reads only the (small) header/index from a Stream, without
/// buffering track payloads — real streamed-music AWBs are hundreds of MB to multi-GB, so
/// callers extract each track by seeking to its (Start, End) range instead of holding the
/// whole archive in memory. <see cref="Parse"/> keeps the old fully-buffered byte[] API,
/// implemented on top of the same index parser.
/// </summary>
public sealed class Afs2Archive
{
    public IReadOnlyList<(int Id, long Start, long End)> Entries { get; private init; } = [];
    public IReadOnlyList<(int Id, byte[] Data)> Tracks { get; private init; } = [];

    /// <summary>
    /// Per-archive HCA key mixer at header offset 0x0E. Combined with the game's base HCA
    /// key to derive the effective decryption key (see AwbExtractService); 0 means the
    /// archive's tracks use the base key unmixed.
    /// </summary>
    public ushort Subkey { get; private init; }

    public static Afs2Archive Parse(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes, writable: false);
        var index = ParseIndex(ms);

        var tracks = new List<(int Id, byte[] Data)>(index.Entries.Count);
        foreach (var (id, start, end) in index.Entries)
        {
            var data = new byte[end - start];
            ms.Position = start;
            ReadExact(ms, data);
            tracks.Add((id, data));
        }

        return new Afs2Archive { Entries = index.Entries, Tracks = tracks, Subkey = index.Subkey };
    }

    /// <summary>Parses only the header/id/offset tables from a seekable stream (small, fast).</summary>
    public static Afs2Archive ParseIndex(Stream stream)
    {
        var length = stream.Length;
        if (length < 0x10)
            throw new InvalidDataException("AFS2 magic not found");

        var header = new byte[0x10];
        stream.Position = 0;
        ReadExact(stream, header);

        if (header[0] != 'A' || header[1] != 'F' || header[2] != 'S' || header[3] != '2')
            throw new InvalidDataException("AFS2 magic not found");

        var offsetSize = header[5];
        var idSize = header[6];
        var entryCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x08));
        var alignment = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0x0C));
        var subkey = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0x0E));
        if (alignment == 0)
            alignment = 1;

        // Guard before allocating: a corrupted file with valid magic must not be able
        // to request a multi-GB array (OOM would bypass callers' per-file catch).
        if (offsetSize is not (2 or 4) || idSize is not (2 or 4))
            throw new InvalidDataException($"Unsupported AFS2 offset/id sizes {offsetSize}/{idSize}");
        if (entryCount < 0 ||
            0x10 + (long)entryCount * idSize + ((long)entryCount + 1) * offsetSize > length)
            throw new InvalidDataException("AFS2 entry count out of bounds");

        var tableBytes = new byte[entryCount * idSize + (entryCount + 1) * offsetSize];
        ReadExact(stream, tableBytes);
        var span = (ReadOnlySpan<byte>)tableBytes;

        var pos = 0;
        var ids = new int[entryCount];
        for (var i = 0; i < entryCount; i++)
        {
            ids[i] = (int)ReadEntry(span, ref pos, idSize, "id");
        }

        var offsets = new long[entryCount + 1];
        for (var i = 0; i <= entryCount; i++)
        {
            offsets[i] = ReadEntry(span, ref pos, offsetSize, "offset");
        }

        var entries = new List<(int Id, long Start, long End)>(entryCount);
        for (var i = 0; i < entryCount; i++)
        {
            var start = (offsets[i] + alignment - 1) / alignment * alignment;
            var end = offsets[i + 1];
            if (start < 0 || end < start || end > length)
                throw new InvalidDataException($"AFS2 track {i} offsets out of bounds");
            entries.Add((ids[i], start, end));
        }

        return new Afs2Archive { Entries = entries, Subkey = subkey };
    }

    private static void ReadExact(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0)
                throw new InvalidDataException("AFS2 stream ended unexpectedly");
            offset += read;
        }
    }

    private static long ReadEntry(ReadOnlySpan<byte> span, ref int pos, byte size, string what)
    {
        if (pos + size > span.Length)
            throw new InvalidDataException($"AFS2 {what} table out of bounds");
        long value = size switch
        {
            2 => BinaryPrimitives.ReadUInt16LittleEndian(span[pos..]),
            4 => BinaryPrimitives.ReadUInt32LittleEndian(span[pos..]),
            _ => throw new InvalidDataException($"Unsupported AFS2 {what} size {size}"),
        };
        pos += size;
        return value;
    }
}
