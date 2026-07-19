using System.Buffers.Binary;
using System.Text;
using AcbFinder.Core;

namespace AcbFinder.Core.Tests;

/// <summary>Builds synthetic big-endian @UTF tables matching CriTable's layout.</summary>
internal static class UtfBuilder
{
    // Storage: 0x10 name-only, 0x30 constant, 0x50 per-row.
    // Types: 2=u16, 4=u32, 0xA=string, 0xB=data (only what the tests need).
    public sealed record Column(string Name, byte Type, byte Storage, object? Constant = null);

    public static byte[] Build(string tableName, Column[] columns, params Dictionary<string, object?>[] rows)
    {
        var strings = new List<byte>();
        var stringOffsets = new Dictionary<string, int>();
        var dataPool = new List<byte>();

        int AddString(string s)
        {
            if (stringOffsets.TryGetValue(s, out var existing))
                return existing;
            var off = strings.Count;
            strings.AddRange(Encoding.UTF8.GetBytes(s));
            strings.Add(0);
            stringOffsets[s] = off;
            return off;
        }

        byte[] ValueBytes(byte type, object? v) => type switch
        {
            2 => Be16(Convert.ToUInt16(v)),
            4 => Be32(Convert.ToUInt32(v)),
            0xA => Be32((uint)AddString((string)v!)),
            0xB => DataRef((byte[])v!),
            _ => throw new NotSupportedException($"builder type 0x{type:X}"),
        };

        byte[] DataRef(byte[] d)
        {
            var off = dataPool.Count;
            dataPool.AddRange(d);
            return [.. Be32((uint)off), .. Be32((uint)d.Length)];
        }

        var tableNameOff = AddString(tableName);

        var schema = new List<byte>();
        foreach (var col in columns)
        {
            schema.Add((byte)(col.Storage | col.Type));
            schema.AddRange(Be32((uint)AddString(col.Name)));
            if (col.Storage == 0x30)
                schema.AddRange(ValueBytes(col.Type, col.Constant));
        }

        var rowBytes = new List<byte>();
        foreach (var row in rows)
        {
            foreach (var col in columns.Where(c => c.Storage == 0x50))
                rowBytes.AddRange(ValueBytes(col.Type, row[col.Name]));
        }

        var rowWidth = columns.Where(c => c.Storage == 0x50).Sum(c => c.Type switch
        {
            2 => 2, 4 => 4, 0xA => 4, 0xB => 8,
            _ => throw new NotSupportedException(),
        });

        var rowsOffset = (ushort)(0x20 + schema.Count - 8);
        var stringPoolOffset = (uint)(rowsOffset + rowBytes.Count);
        var dataPoolOffset = stringPoolOffset + (uint)strings.Count;
        var total = 0x20 + schema.Count + rowBytes.Count + strings.Count + dataPool.Count;

        var buf = new List<byte>(total);
        buf.AddRange("@UTF"u8.ToArray());
        buf.AddRange(Be32((uint)(total - 8)));
        buf.AddRange(Be16(1)); // version
        buf.AddRange(Be16(rowsOffset));
        buf.AddRange(Be32(stringPoolOffset));
        buf.AddRange(Be32(dataPoolOffset));
        buf.AddRange(Be32((uint)tableNameOff));
        buf.AddRange(Be16((ushort)columns.Length));
        buf.AddRange(Be16((ushort)rowWidth));
        buf.AddRange(Be32((uint)rows.Length));
        buf.AddRange(schema);
        buf.AddRange(rowBytes);
        buf.AddRange(strings);
        buf.AddRange(dataPool);
        return [.. buf];
    }

    private static byte[] Be16(ushort v) => [(byte)(v >> 8), (byte)v];
    private static byte[] Be32(uint v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
}

internal static class Afs2Builder
{
    public static byte[] Build((int Id, byte[] Data)[] tracks, ushort alignment = 32, ushort subkey = 0)
    {
        var n = tracks.Length;
        var headerEnd = 0x10 + n * 2 + (n + 1) * 4;
        var offsets = new int[n + 1];
        var starts = new int[n];
        offsets[0] = headerEnd;
        var cur = headerEnd;
        for (var i = 0; i < n; i++)
        {
            starts[i] = (cur + alignment - 1) / alignment * alignment;
            cur = starts[i] + tracks[i].Data.Length;
            offsets[i + 1] = cur;
        }

        var buf = new byte[cur];
        "AFS2"u8.CopyTo(buf);
        buf[4] = 1; // version
        buf[5] = 4; // offsetSize
        buf[6] = 2; // idSize
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x08), (uint)n);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x0C), alignment);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x0E), subkey);
        var pos = 0x10;
        foreach (var t in tracks)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(pos), (ushort)t.Id);
            pos += 2;
        }
        foreach (var o in offsets)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(pos), (uint)o);
            pos += 4;
        }
        for (var i = 0; i < n; i++)
            tracks[i].Data.CopyTo(buf, starts[i]);
        return buf;
    }
}

public class CriTableTests
{
    [Fact]
    public void ParsesPerRowConstantAndNameOnlyColumns()
    {
        var bytes = UtfBuilder.Build("TestTable",
            [
                new("Id", 4, 0x50),
                new("Label", 0xA, 0x50),
                new("Payload", 0xB, 0x50),
                new("ConstVal", 4, 0x30, 7u),
                new("NameOnly", 4, 0x10),
            ],
            new Dictionary<string, object?> { ["Id"] = 1u, ["Label"] = "one", ["Payload"] = new byte[] { 1, 2, 3 } },
            new Dictionary<string, object?> { ["Id"] = 2u, ["Label"] = "two", ["Payload"] = new byte[] { 9 } });

        var table = CriTable.Parse(bytes);

        Assert.Equal("TestTable", table.TableName);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(1u, table.Rows[0]["Id"]);
        Assert.Equal("one", table.Rows[0]["Label"]);
        Assert.Equal(new byte[] { 1, 2, 3 }, table.Rows[0]["Payload"]);
        Assert.Equal(2u, table.Rows[1]["Id"]);
        Assert.Equal("two", table.Rows[1]["Label"]);
        Assert.Equal(new byte[] { 9 }, table.Rows[1]["Payload"]);
        Assert.Equal(7u, table.Rows[0]["ConstVal"]);
        Assert.Equal(7u, table.Rows[1]["ConstVal"]);
        Assert.Null(table.Rows[0]["NameOnly"]);
    }

    [Fact]
    public void GarbageAfterMagic_Throws()
    {
        var bytes = "@UTF"u8.ToArray().Concat(Encoding.ASCII.GetBytes("junk junk junk junk junk junk junk")).ToArray();
        Assert.ThrowsAny<Exception>(() => CriTable.Parse(bytes));
    }
}

public class AcbFileTests
{
    private static byte[] BuildCueTable(params (string Name, ushort Index)[] cues) =>
        UtfBuilder.Build("CueName",
            [new("CueName", 0xA, 0x50), new("CueIndex", 2, 0x50)],
            cues.Select(c => new Dictionary<string, object?> { ["CueName"] = c.Name, ["CueIndex"] = c.Index }).ToArray());

    internal static byte[] BuildAcb(string name, (string, ushort)[] cues, byte[]? awb = null, byte[]? streamHeader = null) =>
        UtfBuilder.Build("Header",
            [
                new("Name", 0xA, 0x50),
                new("CueNameTable", 0xB, 0x50),
                new("AwbFile", 0xB, 0x50),
                new("StreamAwbAfs2Header", 0xB, 0x50),
            ],
            new Dictionary<string, object?>
            {
                ["Name"] = name,
                ["CueNameTable"] = BuildCueTable(cues),
                ["AwbFile"] = awb ?? [],
                ["StreamAwbAfs2Header"] = streamHeader ?? [],
            });

    [Fact]
    public void ParsesNameCueNamesAndInternalAwb()
    {
        // cues intentionally out of order — CueIndex must drive ordering
        var acbBytes = BuildAcb("my_song", [("main", 1), ("intro", 0)],
            awb: [1, 2, 3], streamHeader: [0xFF]);

        Assert.True(AcbFile.TryParse(acbBytes, out var acb));
        Assert.Equal("my_song", acb!.Name);
        Assert.Equal(["intro", "main"], acb.CueNames);
        Assert.Equal(new byte[] { 1, 2, 3 }, acb.InternalAwb);
        Assert.True(acb.HasStreamAwb);
    }

    [Fact]
    public void EmptyAwbBlob_MeansNoInternalAwb()
    {
        var acbBytes = BuildAcb("x", [("a", 0)]);

        Assert.True(AcbFile.TryParse(acbBytes, out var acb));
        Assert.Null(acb!.InternalAwb);
        Assert.False(acb.HasStreamAwb);
    }

    [Fact]
    public void GarbageBytes_ReturnsFalse()
    {
        var bytes = "@UTF"u8.ToArray().Concat("this is not a table at all........"u8.ToArray()).ToArray();
        Assert.False(AcbFile.TryParse(bytes, out _));
    }
}

public class Afs2ArchiveTests
{
    [Fact]
    public void ParsesTracksWithAlignmentPadding()
    {
        (int, byte[])[] tracks =
        [
            (0, [0xAA, 0xAA, 0xAA, 0xAA, 0xAA]),
            (1, [0xBB, 0xBB, 0xBB, 0xBB, 0xBB, 0xBB, 0xBB]),
            (7, [0xCC, 0xCC, 0xCC]),
        ];
        var bytes = Afs2Builder.Build(tracks, alignment: 32);

        var archive = Afs2Archive.Parse(bytes);

        Assert.Equal(3, archive.Tracks.Count);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(tracks[i].Item1, archive.Tracks[i].Id);
            Assert.Equal(tracks[i].Item2, archive.Tracks[i].Data);
        }
    }

    [Fact]
    public void Subkey_RoundTripsFromHeaderBytes()
    {
        var bytesWithSubkey = Afs2Builder.Build([(0, new byte[] { 1 })], subkey: 0x64A4);
        var bytesWithoutSubkey = Afs2Builder.Build([(0, new byte[] { 1 })]);

        Assert.Equal(0x64A4, Afs2Archive.Parse(bytesWithSubkey).Subkey);
        Assert.Equal(0x64A4, Afs2Archive.ParseIndex(new MemoryStream(bytesWithSubkey)).Subkey);
        Assert.Equal(0, Afs2Archive.Parse(bytesWithoutSubkey).Subkey);
    }

    [Fact]
    public void BadMagic_Throws()
    {
        Assert.Throws<InvalidDataException>(() => Afs2Archive.Parse("NOPE0000000000000000"u8.ToArray()));
    }

    [Fact]
    public void GarbageEntryCount_ThrowsInsteadOfHugeAllocation()
    {
        var bytes = Afs2Builder.Build([(0, new byte[] { 1 })]);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x08), 0x7FFFFFFF);

        Assert.Throws<InvalidDataException>(() => Afs2Archive.Parse(bytes));
    }

    [Fact]
    public void ParseIndexFromFileStream_MatchesByteArrayParse()
    {
        (int, byte[])[] tracks =
        [
            (0, [0xAA, 0xAA, 0xAA, 0xAA, 0xAA]),
            (1, [0xBB, 0xBB, 0xBB, 0xBB, 0xBB, 0xBB, 0xBB]),
            (7, [0xCC, 0xCC, 0xCC]),
        ];
        var bytes = Afs2Builder.Build(tracks, alignment: 32);
        var path = Path.Combine(Path.GetTempPath(), "acbfinder-test-" + Guid.NewGuid() + ".awb");
        File.WriteAllBytes(path, bytes);
        try
        {
            using var fs = File.OpenRead(path);
            var streamed = Afs2Archive.ParseIndex(fs);
            var buffered = Afs2Archive.Parse(bytes);

            Assert.Equal(buffered.Entries, streamed.Entries);
            for (var i = 0; i < tracks.Length; i++)
            {
                var (_, start, end) = streamed.Entries[i];
                var data = new byte[end - start];
                fs.Position = start;
                var offset = 0;
                while (offset < data.Length)
                    offset += fs.Read(data, offset, data.Length - offset);

                Assert.Equal(tracks[i].Item2, data);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class AwbExtractServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "acbfinder-test-" + Guid.NewGuid());

    public AwbExtractServiceTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task PairedAwb_TracksGetCueNames()
    {
        var acbDir = Path.Combine(_root, "acb");
        Directory.CreateDirectory(acbDir);
        File.WriteAllBytes(Path.Combine(acbDir, "song1.acb"),
            AcbFileTests.BuildAcb("song1", [("intro", 0), ("main", 1)]));

        byte[] track0 = [0x11, 0x11, 0x11];
        byte[] track1 = [0x22, 0x22];
        File.WriteAllBytes(Path.Combine(_root, "song1"), Afs2Builder.Build([(0, track0), (1, track1)]));

        await AwbExtractService.RunAsync(_root);

        Assert.True(File.Exists(Path.Combine(_root, "awb", "song1.awb")));
        Assert.False(File.Exists(Path.Combine(_root, "song1")));
        Assert.Equal(track0, File.ReadAllBytes(Path.Combine(_root, "hca", "song1", "intro.hca")));
        Assert.Equal(track1, File.ReadAllBytes(Path.Combine(_root, "hca", "song1", "main.hca")));
    }

    [Fact]
    public async Task UnpairedAwb_TracksGetIndexNames()
    {
        byte[] data = [0x33, 0x33];
        File.WriteAllBytes(Path.Combine(_root, "nopair"), Afs2Builder.Build([(0, data)]));

        await AwbExtractService.RunAsync(_root);

        Assert.True(File.Exists(Path.Combine(_root, "awb", "nopair.awb")));
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(_root, "hca", "nopair", "000.hca")));
    }

    [Fact]
    public async Task HashedStemAwb_PairsViaNamesMapAndTakesAcbName()
    {
        // Extract ACB first: hashed source name gets renamed and recorded in names.map.
        File.WriteAllBytes(Path.Combine(_root, "a1b2c3d4"),
            AcbFileTests.BuildAcb("nice_song", [("intro", 0)]));
        await AcbExtractService.RunAsync(_root);
        Assert.True(File.Exists(Path.Combine(_root, "acb", "nice_song.acb")));

        // AWB with the same hashed stem must pair through the map.
        byte[] data = [0x55, 0x66];
        File.WriteAllBytes(Path.Combine(_root, "a1b2c3d4"), Afs2Builder.Build([(0, data)]));
        await AwbExtractService.RunAsync(_root);

        Assert.True(File.Exists(Path.Combine(_root, "awb", "nice_song.awb")));
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(_root, "hca", "nice_song", "intro.hca")));
    }

    [Fact]
    public async Task AcbWithInternalAwb_TracksExtracted()
    {
        var acbDir = Path.Combine(_root, "acb");
        Directory.CreateDirectory(acbDir);
        byte[] data = [0x44, 0x44, 0x44];
        var internalAwb = Afs2Builder.Build([(0, data)]);
        File.WriteAllBytes(Path.Combine(acbDir, "emb.acb"),
            AcbFileTests.BuildAcb("emb", [("voice", 0)], awb: internalAwb));

        await AwbExtractService.RunAsync(_root);

        Assert.Equal(data, File.ReadAllBytes(Path.Combine(_root, "hca", "emb", "voice.hca")));
    }

    [Fact]
    public async Task ExtractedTracks_WriteHcaKeyWithEffectiveKey()
    {
        // subkey 0x64A4 -> effective key 0xF10B79E06D9FA37D, confirmed against a real
        // extracted archive that foobar2000+vgmstream decoded correctly with that key.
        var acbDir = Path.Combine(_root, "acb");
        Directory.CreateDirectory(acbDir);
        var internalAwb = Afs2Builder.Build([(0, [0x44, 0x44, 0x44])], subkey: 0x64A4);
        File.WriteAllBytes(Path.Combine(acbDir, "keyed.acb"),
            AcbFileTests.BuildAcb("keyed", [("voice", 0)], awb: internalAwb));

        await AwbExtractService.RunAsync(_root);

        var keyBytes = File.ReadAllBytes(Path.Combine(_root, "hca", "keyed", ".hcakey"));
        Assert.Equal(8, keyBytes.Length);
        Assert.Equal(0xF10B79E06D9FA37DUL, BinaryPrimitives.ReadUInt64BigEndian(keyBytes));
    }
}

public class AcbExtractUtfNameTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "acbfinder-test-" + Guid.NewGuid());

    public AcbExtractUtfNameTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task UtfTableName_TakesPriorityOverPatternSearch()
    {
        File.WriteAllBytes(Path.Combine(_root, "raw"),
            AcbFileTests.BuildAcb("utf_name", [("c", 0)]));

        await AcbExtractService.RunAsync(_root);

        Assert.True(File.Exists(Path.Combine(_root, "acb", "utf_name.acb")));
    }
}
