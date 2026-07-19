using System.Buffers.Binary;

namespace AcbFinder.Core;

/// <summary>
/// Moves AFS2-magic files from origin to origin/awb (.awb extension) and extracts
/// their tracks to origin/hca/{name}/. Pairing with an ACB uses the names.map sidecar
/// (original source stem → renamed ACB file) written by AcbExtractService, with a
/// direct {stem}.acb lookup as fallback; a paired AWB takes the ACB's name. Track
/// names come from the paired ACB's cue names, else zero-padded indices. ACBs with
/// an embedded AwbFile blob are extracted the same way.
///
/// Extraction is stream-based (<see cref="Afs2Archive.ParseIndex"/> + per-track seek/copy),
/// never buffering a whole AWB into memory — real streamed-music AWBs run hundreds of MB to
/// multi-GB, which would throw past the 2GB byte[] limit and stall with no visible progress.
///
/// Each hca/{name}/ folder also gets a .hcakey file (8-byte big-endian effective HCA key,
/// derived from the game's base key + the archive's AFS2 subkey) that foobar2000+vgmstream
/// reads to decode the tracks.
/// </summary>
public static class AwbExtractService
{
    private static readonly byte[] Afs2 = "AFS2"u8.ToArray();

    // THE IDOLM@STER Shiny Colors Song for Prism, PC — from vgmstream hca_keys.
    private const ulong HcaBaseKey = 156967709847897761;

    public static async Task RunAsync(
        string originDir,
        IProgress<(int done, int total)>? progress = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(originDir))
        {
            log?.Invoke($"Origin folder not found: {originDir}");
            return;
        }

        var awbCandidates = AcbExtractService.EnumeratePipelineInputs(originDir)
            .Where(f => AcbExtractService.HasMagic(f, Afs2))
            .ToList();

        var acbDir = Path.Combine(originDir, "acb");
        var acbFiles = Directory.Exists(acbDir)
            ? Directory.GetFiles(acbDir, "*.acb", SearchOption.TopDirectoryOnly)
            : [];

        var total = awbCandidates.Count + acbFiles.Length;
        if (total == 0)
        {
            log?.Invoke("No AWB candidate files found.");
            return;
        }

        var awbDir = Path.Combine(originDir, "awb");
        var hcaDir = Path.Combine(originDir, "hca");
        var nameMap = LoadNamesMap(acbDir);
        var done = 0;
        progress?.Report((0, total));

        foreach (var file in awbCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Directory.CreateDirectory(awbDir);
                var stem = Path.GetFileNameWithoutExtension(file);

                // Pair by original source file name via the names.map sidecar written by
                // AcbExtractService; fall back to a direct {stem}.acb hit. When paired,
                // the AWB (and its hca folder) take the ACB's human-readable name.
                var acbPath = nameMap.TryGetValue(stem, out var mappedAcbName)
                    ? Path.Combine(acbDir, mappedAcbName)
                    : Path.Combine(acbDir, stem + ".acb");
                var finalStem = File.Exists(acbPath) ? Path.GetFileNameWithoutExtension(acbPath) : stem;

                var destPath = AcbExtractService.ResolveCollision(
                    awbDir, AcbExtractService.Sanitize(finalStem) + ".awb");
                File.Move(file, destPath);

                var cueNames = LoadCueNames(acbPath);
                var awbBase = Path.GetFileNameWithoutExtension(destPath);
                using (var awbStream = File.OpenRead(destPath))
                {
                    ExtractTracksFromStream(awbStream, awbBase, Path.Combine(hcaDir, awbBase), cueNames, log);
                }
                log?.Invoke($"AWB extraction completed: {awbBase}");
            }
            catch (Exception ex)
            {
                log?.Invoke($"Error ({Path.GetFileName(file)}): {ex.Message}");
            }

            done++;
            progress?.Report((done, total));
        }

        log?.Invoke($"Checking embedded AWBs in {acbFiles.Length} ACB file(s).");

        // Per-item logging here would post thousands of dispatcher updates for a large ACB
        // set (each rebuilding the log TextBox); log one summary after the pass instead.
        // Errors stay per-file since they're rare.
        var extractedCount = 0;

        await Parallel.ForEachAsync(
            acbFiles,
            new ParallelOptions { CancellationToken = cancellationToken },
            async (acbFile, ct) =>
            {
                try
                {
                    var bytes = await File.ReadAllBytesAsync(acbFile, ct).ConfigureAwait(false);
                    if (AcbFile.TryParse(bytes, out var acb) && acb!.InternalAwb is not null)
                    {
                        var acbBase = Path.GetFileNameWithoutExtension(acbFile);
                        using var awbStream = new MemoryStream(acb.InternalAwb, writable: false);
                        ExtractTracksFromStream(awbStream, acbBase, Path.Combine(hcaDir, acbBase), acb.CueNames, log);
                        Interlocked.Increment(ref extractedCount);
                    }
                }
                catch (Exception ex)
                {
                    log?.Invoke($"Error ({Path.GetFileName(acbFile)}): {ex.Message}");
                }

                var current = Interlocked.Increment(ref done);
                progress?.Report((current, total));
            }).ConfigureAwait(false);

        log?.Invoke($"Embedded AWB extraction completed: {extractedCount}/{acbFiles.Length} ACB file(s).");
    }

    private static Dictionary<string, string> LoadNamesMap(string acbDir)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var mapPath = Path.Combine(acbDir, AcbExtractService.NamesMapFileName);
        if (!File.Exists(mapPath))
            return map;
        foreach (var line in File.ReadAllLines(mapPath))
        {
            var parts = line.Split('\t');
            if (parts.Length == 2)
                map[parts[0]] = parts[1];
        }
        return map;
    }

    private static IReadOnlyList<string> LoadCueNames(string acbPath)
    {
        if (!File.Exists(acbPath))
            return [];
        return AcbFile.TryParse(File.ReadAllBytes(acbPath), out var acb) ? acb!.CueNames : [];
    }

    // ponytail: only large archives get a sign-of-life log — it exists so a multi-GB
    // external AWB doesn't look frozen while streaming; logging it for thousands of small
    // embedded AWBs would flood the UI dispatcher instead. Raise/lower if 50MB proves wrong.
    private const double LogStartThresholdMb = 50;

    /// <summary>
    /// Parses only the AFS2 index (fast) then streams each track straight from source to
    /// its .hca file — no whole-archive byte[] buffering, since real AWBs can be multi-GB.
    /// Logs file name/size/track count up front for large archives so a long extraction
    /// doesn't look frozen.
    /// </summary>
    private static void ExtractTracksFromStream(
        Stream awbStream, string label, string destDir, IReadOnlyList<string> cueNames, Action<string>? log)
    {
        var sizeMb = awbStream.Length / (1024.0 * 1024.0);
        var archive = Afs2Archive.ParseIndex(awbStream);
        if (sizeMb >= LogStartThresholdMb)
            log?.Invoke($"AWB extraction started: {label} ({sizeMb:F1} MB, {archive.Entries.Count} track(s))");

        Directory.CreateDirectory(destDir);
        var buffer = new byte[81920];
        for (var i = 0; i < archive.Entries.Count; i++)
        {
            var (_, start, end) = archive.Entries[i];
            var name = i < cueNames.Count && !string.IsNullOrEmpty(cueNames[i])
                ? AcbExtractService.Sanitize(cueNames[i])
                : i.ToString("D3");

            awbStream.Position = start;
            using var dst = File.Create(Path.Combine(destDir, name + ".hca"));
            // Raw track bytes written unchanged, whatever the codec magic is.
            CopyRange(awbStream, dst, end - start, buffer);
        }

        WriteHcaKey(destDir, archive.Subkey);
    }

    /// <summary>
    /// Writes the effective HCA decryption key as an 8-byte big-endian .hcakey file, the
    /// format foobar2000+vgmstream reads. Mixing formula and base key confirmed against a
    /// real extracted archive (subkey 0x64A4 → effective key 0xF10B79E06D9FA37D decoded
    /// correctly); unchecked multiplication wraparound is intentional, matching vgmstream.
    /// </summary>
    private static void WriteHcaKey(string destDir, ushort subkey)
    {
        var effectiveKey = subkey == 0
            ? HcaBaseKey
            : unchecked(HcaBaseKey * (((ulong)subkey << 16) | (ushort)(~subkey + 2)));

        var keyBytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(keyBytes, effectiveKey);
        File.WriteAllBytes(Path.Combine(destDir, ".hcakey"), keyBytes);
    }

    private static void CopyRange(Stream src, Stream dst, long count, byte[] buffer)
    {
        while (count > 0)
        {
            var toRead = (int)Math.Min(buffer.Length, count);
            var read = src.Read(buffer, 0, toRead);
            if (read == 0)
                throw new EndOfStreamException("AWB stream ended before track data was fully read");
            dst.Write(buffer, 0, read);
            count -= read;
        }
    }
}
