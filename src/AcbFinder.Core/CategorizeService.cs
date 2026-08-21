using System.Text.RegularExpressions;

namespace AcbFinder.Core;

/// <summary>
/// Sorts origin/wav/*.wav into case-insensitive generic prefix categories (bgm, cs,
/// se, and scenario) before character-name matching; unmatched files land in "etc".
/// </summary>
public static class CategorizeService
{
    public static readonly string[] DefaultCharacters =
    [
        "mano", "hiori", "meguru", "kogane", "mamimi", "sakuya", "yuika", "kiriko",
        "kaho", "chiyoko", "juri", "rinze", "natsuha", "amana", "tenka", "chiyuki",
        "asahi", "fuyuko", "mei", "toru", "madoka", "koito", "hinana", "nichika",
        "mikoto", "luca", "hana", "haruki", "haruka", "chihaya", "miki", "yukiho",
        "ritsuko", "azusa", "iori", "makoto", "ami", "mami", "takane", "hibiki",
    ];

    private static readonly Regex ScenarioPrefix = new(@"^s\d+_", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static Task RunAsync(
        string originDir,
        IProgress<(int done, int total)>? progress = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<string>? characters = null)
    {
        characters ??= DefaultCharacters;

        var wavDir = Path.Combine(originDir, "wav");
        var files = Directory.Exists(wavDir)
            ? Directory.EnumerateFiles(wavDir, "*.wav", SearchOption.TopDirectoryOnly).ToList()
            : [];

        if (files.Count == 0)
        {
            log?.Invoke($"No WAV files to categorize: {wavDir}");
            return Task.CompletedTask;
        }

        var total = files.Count;
        var done = 0;
        progress?.Report((0, total));

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var fileName = Path.GetFileName(file);
                var destDir = Path.Combine(wavDir, GetDestinationCategory(fileName, characters));
                Directory.CreateDirectory(destDir);
                File.Move(file, Path.Combine(destDir, fileName), overwrite: true);
            }
            catch (Exception ex)
            {
                log?.Invoke($"Error ({Path.GetFileName(file)}): {ex.Message}");
            }

            done++;
            progress?.Report((done, total));
        }

        return Task.CompletedTask;
    }

    private static string GetDestinationCategory(string fileName, IReadOnlyList<string> characters)
    {
        if (fileName.StartsWith("bgm_", StringComparison.OrdinalIgnoreCase))
            return "bgm";
        if (fileName.StartsWith("CS_", StringComparison.OrdinalIgnoreCase))
            return "cs";
        if (fileName.StartsWith("se_", StringComparison.OrdinalIgnoreCase))
            return "se";
        if (ScenarioPrefix.IsMatch(fileName))
            return "scenario";

        return characters.FirstOrDefault(
            c => fileName.Contains(c, StringComparison.OrdinalIgnoreCase)) ?? "etc";
    }
}
