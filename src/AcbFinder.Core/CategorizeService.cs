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
        "ritsuko", "azusa", "iori", "yayoi", "makoto", "ami", "mami", "takane", "hibiki",
    ];

    private static readonly Regex ScenarioPrefix = new(@"^s\d+_", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex StrongSpeakerMarker = new(
        @"_(?<id>\d{2})(?<label>[A-Za-z]+)",
        RegexOptions.CultureInvariant);

    private static readonly IReadOnlyDictionary<string, string> CharacterNamesById =
        new Dictionary<string, string>
        {
            ["01"] = "mano",
            ["02"] = "hiori",
            ["03"] = "meguru",
            ["04"] = "kogane",
            ["05"] = "mamimi",
            ["06"] = "sakuya",
            ["07"] = "yuika",
            ["08"] = "kiriko",
            ["09"] = "kaho",
            ["10"] = "chiyoko",
            ["11"] = "juri",
            ["12"] = "rinze",
            ["13"] = "natsuha",
            ["14"] = "amana",
            ["15"] = "tenka",
            ["16"] = "chiyuki",
            ["17"] = "asahi",
            ["18"] = "fuyuko",
            ["19"] = "mei",
            ["20"] = "toru",
            ["21"] = "madoka",
            ["22"] = "koito",
            ["23"] = "hinana",
            ["24"] = "nichika",
            ["25"] = "mikoto",
            ["26"] = "luca",
            ["27"] = "hana",
            ["28"] = "haruki",
            ["83"] = "iori",
            ["84"] = "yayoi",
            ["85"] = "miki",
            ["86"] = "chihaya",
            ["87"] = "takane",
            ["88"] = "makoto",
            ["89"] = "haruka",
        };

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

        foreach (Match marker in StrongSpeakerMarker.Matches(fileName))
        {
            if (!CharacterNamesById.TryGetValue(marker.Groups["id"].Value, out var mappedCharacter) ||
                !marker.Groups["label"].Value.StartsWith(mappedCharacter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var suppliedCharacter = characters.FirstOrDefault(
                character => string.Equals(character, mappedCharacter, StringComparison.OrdinalIgnoreCase));
            if (suppliedCharacter is not null)
                return suppliedCharacter;
        }

        return characters.FirstOrDefault(character => ContainsCharacterToken(fileName, character)) ?? "etc";
    }

    private static bool ContainsCharacterToken(string fileName, string character)
    {
        if (string.IsNullOrEmpty(character))
            return false;

        var searchStart = 0;
        while (true)
        {
            var index = fileName.IndexOf(character, searchStart, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                return false;

            var afterIndex = index + character.Length;
            if ((index == 0 || !IsAsciiLetter(fileName[index - 1])) &&
                (afterIndex == fileName.Length || !IsAsciiLetter(fileName[afterIndex])))
            {
                return true;
            }

            searchStart = index + 1;
        }
    }

    private static bool IsAsciiLetter(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
}
