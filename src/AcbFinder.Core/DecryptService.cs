namespace AcbFinder.Core;

/// <summary>
/// Scans a game folder for CRI @UTF / AFS2 payloads (plain or 4-byte-shifted "encrypted"
/// variants) and copies only the recognized files FLAT into an output "origin" folder
/// (matching the original Java app), stripping the 4-byte shift where present. Same-named
/// files from different source subfolders get a numeric suffix. The source game folder is
/// never touched.
/// </summary>
public static class DecryptService
{
    private static readonly byte[] Utf = "@UTF"u8.ToArray();
    private static readonly byte[] Afs2 = "AFS2"u8.ToArray();

    public static string GetDefaultOriginDir() =>
        Path.Combine(AppContext.BaseDirectory, "process", DateTime.Now.ToString("yyyyMMdd"), "origin");

    public static async Task RunAsync(
        string gameFolder,
        string outputDir,
        IProgress<(int done, int total)>? progress = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(gameFolder))
        {
            log?.Invoke($"Game folder not found: {gameFolder}");
            return;
        }

        var files = Directory.EnumerateFiles(gameFolder, "*", SearchOption.AllDirectories).ToList();
        var total = files.Count;
        var done = 0;
        progress?.Report((0, total));
        Directory.CreateDirectory(outputDir);

        await Parallel.ForEachAsync(
            files,
            new ParallelOptions { CancellationToken = cancellationToken },
            async (file, ct) =>
            {
                try
                {
                    await ProcessFileAsync(file, outputDir, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    log?.Invoke($"Error ({Path.GetFileName(file)}): {ex.Message}");
                }

                var current = Interlocked.Increment(ref done);
                progress?.Report((current, total));
            }).ConfigureAwait(false);
    }

    private static async Task ProcessFileAsync(string file, string outputDir, CancellationToken ct)
    {
        var header = new byte[8];
        int read;
        using (var fs = File.OpenRead(file))
        {
            read = await fs.ReadAsync(header, ct).ConfigureAwait(false);
        }

        var isEncrypted = header[0] == 0xBA && header[1] == 0x01 &&
                           (MatchesAt(header, 4, Utf, read) || MatchesAt(header, 4, Afs2, read));
        var isPlain = MatchesAt(header, 0, Utf, read) || MatchesAt(header, 0, Afs2, read);

        if (!isEncrypted && !isPlain)
            return;

        using var src = File.OpenRead(file);
        if (isEncrypted)
            src.Seek(4, SeekOrigin.Begin);

        // Flat output: multiple parallel workers may race to claim the same file name
        // (same basename from different source subfolders). CreateNew is atomic at the
        // filesystem level, so retrying on collision is race-safe unlike a File.Exists check.
        using var dst = CreateUniqueFile(outputDir, Path.GetFileName(file));
        await src.CopyToAsync(dst, ct).ConfigureAwait(false);
    }

    private static FileStream CreateUniqueFile(string dir, string fileName)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var candidate = Path.Combine(dir, fileName);
        var n = 0;
        while (true)
        {
            try
            {
                return new FileStream(candidate, FileMode.CreateNew, FileAccess.Write);
            }
            catch (IOException) when (File.Exists(candidate))
            {
                n++;
                candidate = Path.Combine(dir, $"{baseName}_{n}{ext}");
            }
        }
    }

    private static bool MatchesAt(byte[] buffer, int offset, byte[] pattern, int validLength)
    {
        if (validLength < offset + pattern.Length)
            return false;

        for (var i = 0; i < pattern.Length; i++)
        {
            if (buffer[offset + i] != pattern[i])
                return false;
        }

        return true;
    }
}
