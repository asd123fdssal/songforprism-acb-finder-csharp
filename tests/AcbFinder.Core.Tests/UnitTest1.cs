using System.Text;
using AcbFinder.Core;

namespace AcbFinder.Core.Tests;

public class DecryptServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "acbfinder-test-" + Guid.NewGuid());

    private string GameDir => Path.Combine(_root, "game");
    private string OutDir => Path.Combine(_root, "out");

    public DecryptServiceTests()
    {
        Directory.CreateDirectory(GameDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task EncryptedFile_IsCopiedWithFirst4BytesStripped()
    {
        var payload = "@UTF"u8.ToArray().Concat("hello"u8.ToArray()).ToArray();
        var encrypted = new byte[] { 0xBA, 0x01, 0x00, 0x00 }.Concat(payload).ToArray();
        var srcPath = Path.Combine(GameDir, "enc.bin");
        File.WriteAllBytes(srcPath, encrypted);
        var srcBytesBefore = File.ReadAllBytes(srcPath);

        await DecryptService.RunAsync(GameDir, OutDir);

        var destPath = Path.Combine(OutDir, "enc.bin");
        Assert.True(File.Exists(destPath));
        Assert.Equal(payload, File.ReadAllBytes(destPath));

        // source untouched
        Assert.Equal(srcBytesBefore, File.ReadAllBytes(srcPath));
    }

    [Fact]
    public async Task PlainUtfFile_IsCopiedAsIs()
    {
        var bytes = "@UTF"u8.ToArray().Concat("data"u8.ToArray()).ToArray();
        File.WriteAllBytes(Path.Combine(GameDir, "plain.bin"), bytes);

        await DecryptService.RunAsync(GameDir, OutDir);

        var destPath = Path.Combine(OutDir, "plain.bin");
        Assert.True(File.Exists(destPath));
        Assert.Equal(bytes, File.ReadAllBytes(destPath));
    }

    [Fact]
    public async Task NonTargetFile_IsSkipped()
    {
        File.WriteAllBytes(Path.Combine(GameDir, "other.bin"), "not a target"u8.ToArray());

        await DecryptService.RunAsync(GameDir, OutDir);

        Assert.False(File.Exists(Path.Combine(OutDir, "other.bin")));
    }

    [Fact]
    public async Task SameNameInDifferentSubfolders_LandFlatWithSuffix()
    {
        var payload = "@UTF"u8.ToArray().Concat("data"u8.ToArray()).ToArray();
        var subA = Path.Combine(GameDir, "a");
        var subB = Path.Combine(GameDir, "b");
        Directory.CreateDirectory(subA);
        Directory.CreateDirectory(subB);
        File.WriteAllBytes(Path.Combine(subA, "dup.bin"), payload);
        File.WriteAllBytes(Path.Combine(subB, "dup.bin"), payload);

        await DecryptService.RunAsync(GameDir, OutDir);

        // Flat: both land directly in OutDir (no "a"/"b" subfolders), one gets a _1 suffix.
        Assert.True(File.Exists(Path.Combine(OutDir, "dup.bin")));
        Assert.True(File.Exists(Path.Combine(OutDir, "dup_1.bin")));
        Assert.False(Directory.Exists(Path.Combine(OutDir, "a")));
        Assert.False(Directory.Exists(Path.Combine(OutDir, "b")));
    }

    [Fact]
    public async Task GameFolder_IsNeverModified()
    {
        var payload = "@UTF"u8.ToArray().Concat("hello"u8.ToArray()).ToArray();
        var encrypted = new byte[] { 0xBA, 0x01, 0x00, 0x00 }.Concat(payload).ToArray();
        File.WriteAllBytes(Path.Combine(GameDir, "enc.bin"), encrypted);
        File.WriteAllBytes(Path.Combine(GameDir, "other.bin"), "skip me"u8.ToArray());

        var before = Directory.GetFiles(GameDir, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, File.ReadAllBytes);

        await DecryptService.RunAsync(GameDir, OutDir);

        foreach (var (path, bytes) in before)
            Assert.Equal(bytes, File.ReadAllBytes(path));
    }
}

public class AcbExtractServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "acbfinder-test-" + Guid.NewGuid());

    public AcbExtractServiceTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static byte[] BuildAcbBytes(string suffixAfterUtf)
    {
        var header = "@UTF"u8.ToArray();
        var tail = Encoding.ASCII.GetBytes(suffixAfterUtf);
        return header.Concat(tail).ToArray();
    }

    [Fact]
    public async Task NamedAcb_IsRenamedUsingBuildToken()
    {
        var bytes = BuildAcbBytes("junkACB Format/PC Ver.9.9.9 Build:song_abc\0trailing");
        File.WriteAllBytes(Path.Combine(_root, "raw1"), bytes);

        await AcbExtractService.RunAsync(_root);

        var expected = Path.Combine(_root, "acb", "song_abc.acb");
        Assert.True(File.Exists(expected));
        Assert.False(File.Exists(Path.Combine(_root, "raw1")));
    }

    [Fact]
    public async Task AcbWithoutPattern_KeepsOriginalName()
    {
        var bytes = BuildAcbBytes("no marker here at all");
        File.WriteAllBytes(Path.Combine(_root, "raw2"), bytes);

        await AcbExtractService.RunAsync(_root);

        var expected = Path.Combine(_root, "acb", "raw2.acb");
        Assert.True(File.Exists(expected));
    }

    [Fact]
    public async Task InvalidFileNameChars_AreSanitized()
    {
        var bytes = BuildAcbBytes("ACB Format/PC Ver.1.0.0 Build:bad:name?*\0");
        File.WriteAllBytes(Path.Combine(_root, "raw3"), bytes);

        await AcbExtractService.RunAsync(_root);

        var expected = Path.Combine(_root, "acb", "bad_name__.acb");
        Assert.True(File.Exists(expected));
    }

    [Fact]
    public async Task FallbackToNextNul_WhenBuildTokenMissing()
    {
        var bytes = BuildAcbBytes("ACB Format/PC Ver.2.0.0\0fallback_name\0");
        File.WriteAllBytes(Path.Combine(_root, "raw4"), bytes);

        await AcbExtractService.RunAsync(_root);

        var expected = Path.Combine(_root, "acb", "fallback_name.acb");
        Assert.True(File.Exists(expected));
    }

    [Fact]
    public async Task NestedUtfFile_IsFoundAndMoved()
    {
        var nested = Path.Combine(_root, "sub", "dir");
        Directory.CreateDirectory(nested);
        var bytes = BuildAcbBytes("ACB Format/PC Ver.1.0.0 Build:nested_song\0");
        File.WriteAllBytes(Path.Combine(nested, "raw5"), bytes);

        await AcbExtractService.RunAsync(_root);

        Assert.True(File.Exists(Path.Combine(_root, "acb", "nested_song.acb")));
        Assert.False(File.Exists(Path.Combine(nested, "raw5")));
    }

    [Fact]
    public async Task FileAlreadyInAcbDir_IsNotReprocessed()
    {
        var acbDir = Path.Combine(_root, "acb");
        Directory.CreateDirectory(acbDir);
        var bytes = BuildAcbBytes("ACB Format/PC Ver.1.0.0 Build:already_done\0");
        var existing = Path.Combine(acbDir, "already_done.acb");
        File.WriteAllBytes(existing, bytes);

        await AcbExtractService.RunAsync(_root);

        // untouched: no _1 duplicate, original still there
        Assert.True(File.Exists(existing));
        Assert.False(File.Exists(Path.Combine(acbDir, "already_done_1.acb")));
    }

    [Fact]
    public async Task NonUtfFile_IsIgnored()
    {
        File.WriteAllBytes(Path.Combine(_root, "notacb.bin"), "plain data"u8.ToArray());

        await AcbExtractService.RunAsync(_root);

        Assert.True(File.Exists(Path.Combine(_root, "notacb.bin")));
        Assert.False(Directory.Exists(Path.Combine(_root, "acb")));
    }
}

public class CategorizeServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "acbfinder-test-" + Guid.NewGuid());
    private string WavDir => Path.Combine(_root, "wav");

    public CategorizeServiceTests()
    {
        Directory.CreateDirectory(WavDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task MatchingWav_GoesToCharacterFolder()
    {
        File.WriteAllText(Path.Combine(WavDir, "voice_KOGANE_01.wav"), "x");

        await CategorizeService.RunAsync(_root);

        Assert.True(File.Exists(Path.Combine(WavDir, "kogane", "voice_KOGANE_01.wav")));
    }

    [Fact]
    public async Task NonMatchingWav_GoesToEtc()
    {
        File.WriteAllText(Path.Combine(WavDir, "unknown_voice.wav"), "x");

        await CategorizeService.RunAsync(_root);

        Assert.True(File.Exists(Path.Combine(WavDir, "etc", "unknown_voice.wav")));
    }

    [Fact]
    public async Task MissingWavDir_LogsAndReturns()
    {
        Directory.Delete(WavDir, recursive: true);
        string? logged = null;

        await CategorizeService.RunAsync(_root, log: line => logged = line);

        Assert.NotNull(logged);
    }
}
