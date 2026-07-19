using System.Text;

namespace AcbFinder.Core;

/// <summary>
/// Finds @UTF-magic files in the origin folder, moves them to origin/acb, and renames
/// them using the ACB name embedded near the "ACB Format/PC Ver." marker (version text
/// itself is variable and never hardcoded).
/// </summary>
public static class AcbExtractService
{
    private const string AcbMagicPattern = "ACB Format/PC Ver.";
    private const string BuildToken = "Build:";
    private static readonly byte[] Utf = "@UTF"u8.ToArray();

    // ponytail: only search the first 8MB for the name marker instead of loading whole
    // (sometimes huge) ACB files into memory. Raise this if a real ACB ever hides its
    // name table further in.
    private const int SearchCap = 8 * 1024 * 1024;

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

        var candidates = EnumeratePipelineInputs(originDir)
            .Where(f => HasMagic(f, Utf))
            .ToList();

        var total = candidates.Count;
        if (total == 0)
        {
            log?.Invoke("No ACB candidate files found.");
            return;
        }

        var acbDir = Path.Combine(originDir, "acb");
        Directory.CreateDirectory(acbDir);

        // Sidecar mapping (originalStem \t finalAcbFileName) so AwbExtractService can
        // pair AWBs by original source file name after the ACBs have been renamed.
        var mapPath = Path.Combine(acbDir, NamesMapFileName);
        File.Delete(mapPath);

        var done = 0;
        progress?.Report((0, total));

        foreach (var file in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var destPath = await MoveAndRenameAsync(file, acbDir, log, cancellationToken).ConfigureAwait(false);
                File.AppendAllText(mapPath,
                    $"{Path.GetFileNameWithoutExtension(file)}\t{Path.GetFileName(destPath)}\n");
            }
            catch (Exception ex)
            {
                log?.Invoke($"Error ({Path.GetFileName(file)}): {ex.Message}");
            }

            done++;
            progress?.Report((done, total));
        }
    }

    internal const string NamesMapFileName = "names.map";

    /// <summary>
    /// Decrypt preserves the game folder's nested structure, so scan recursively —
    /// but skip the pipeline's own output subdirs so a second run doesn't
    /// re-process already-moved files. Shared with AwbExtractService.
    /// </summary>
    internal static IEnumerable<string> EnumeratePipelineInputs(string originDir)
    {
        string[] pipelineDirs = ["acb", "awb", "hca", "wav"];
        return Directory.EnumerateFiles(originDir, "*", SearchOption.AllDirectories)
            .Where(f =>
            {
                var relative = Path.GetRelativePath(originDir, f);
                return !pipelineDirs.Any(d =>
                    relative.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            });
    }

    internal static bool HasMagic(string file, byte[] magic)
    {
        try
        {
            var header = new byte[magic.Length];
            using var fs = File.OpenRead(file);
            var read = fs.Read(header);
            return read == magic.Length && header.AsSpan().SequenceEqual(magic);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> MoveAndRenameAsync(string file, string acbDir, Action<string>? log, CancellationToken ct)
    {
        var bytes = await ReadUpToAsync(file, SearchCap, ct).ConfigureAwait(false);

        // Prefer the @UTF table's Name field; the byte-pattern search stays as fallback
        // for malformed/truncated tables.
        var name = AcbFile.TryParse(bytes, out var acb) && !string.IsNullOrEmpty(acb!.Name)
            ? acb.Name
            : ExtractAcbName(bytes, log, file);

        var destPath = ResolveCollision(acbDir, Sanitize(name) + ".acb");
        File.Move(file, destPath);
        return destPath;
    }

    private static string ExtractAcbName(byte[] bytes, Action<string>? log, string originalFile)
    {
        var patternBytes = Encoding.ASCII.GetBytes(AcbMagicPattern);
        var patternIdx = IndexOf(bytes, patternBytes, 0);
        if (patternIdx < 0)
        {
            log?.Invoke($"Name pattern not found; keeping the original filename: {Path.GetFileName(originalFile)}");
            return Path.GetFileNameWithoutExtension(originalFile);
        }

        var searchFrom = patternIdx + patternBytes.Length;
        var buildBytes = Encoding.ASCII.GetBytes(BuildToken);
        var buildIdx = IndexOf(bytes, buildBytes, searchFrom);

        string? name;
        if (buildIdx >= 0)
        {
            name = ReadNullTerminatedString(bytes, buildIdx + buildBytes.Length);
        }
        else
        {
            // Fallback: skip past the variable version text to the next NUL, then read
            // the name that follows it.
            var zeroIdx = Array.IndexOf(bytes, (byte)0, searchFrom);
            name = zeroIdx >= 0 ? ReadNullTerminatedString(bytes, zeroIdx + 1) : null;
        }

        if (string.IsNullOrEmpty(name))
        {
            log?.Invoke($"Could not extract a name; keeping the original filename: {Path.GetFileName(originalFile)}");
            return Path.GetFileNameWithoutExtension(originalFile);
        }

        return name;
    }

    private static string? ReadNullTerminatedString(byte[] bytes, int start)
    {
        if (start < 0 || start >= bytes.Length)
            return null;

        var end = Array.IndexOf(bytes, (byte)0, start);
        if (end < 0 || end == start)
            return null;

        return Encoding.UTF8.GetString(bytes, start, end - start);
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start)
    {
        if (start < 0)
            start = 0;

        var limit = haystack.Length - needle.Length;
        for (var i = start; i <= limit; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
                return i;
        }

        return -1;
    }

    internal static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    internal static string ResolveCollision(string dir, string fileName)
    {
        var destPath = Path.Combine(dir, fileName);
        if (!File.Exists(destPath))
            return destPath;

        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var n = 1;
        string candidate;
        do
        {
            candidate = Path.Combine(dir, $"{baseName}_{n}{ext}");
            n++;
        } while (File.Exists(candidate));

        return candidate;
    }

    private static async Task<byte[]> ReadUpToAsync(string file, int cap, CancellationToken ct)
    {
        using var fs = File.OpenRead(file);
        var length = (int)Math.Min(fs.Length, cap);
        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await fs.ReadAsync(buffer.AsMemory(offset, length - offset), ct).ConfigureAwait(false);
            if (read == 0)
                break;
            offset += read;
        }

        if (offset != length)
            Array.Resize(ref buffer, offset);

        return buffer;
    }
}
