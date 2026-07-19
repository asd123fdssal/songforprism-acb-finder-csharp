namespace AcbFinder.Core;

/// <summary>
/// Typed view over the top-level ACB @UTF table (single row expected).
/// </summary>
public sealed class AcbFile
{
    public string? Name { get; private init; }
    public IReadOnlyList<string> CueNames { get; private init; } = [];
    public byte[]? InternalAwb { get; private init; }
    public bool HasStreamAwb { get; private init; }

    public static bool TryParse(byte[] bytes, out AcbFile? acb)
    {
        acb = null;
        try
        {
            var table = CriTable.Parse(bytes);
            if (table.Rows.Count == 0)
                return false;
            var row = table.Rows[0];

            var cueNames = new List<string>();
            if (row.GetValueOrDefault("CueNameTable") is byte[] { Length: > 0 } cueBlob)
            {
                // CueNameTable is itself a @UTF table: CueName (string) + CueIndex (u16).
                cueNames = CriTable.Parse(cueBlob).Rows
                    .Select(r => (Name: r.GetValueOrDefault("CueName") as string,
                                  Index: Convert.ToInt32(r.GetValueOrDefault("CueIndex") ?? 0)))
                    .Where(x => x.Name is not null)
                    .OrderBy(x => x.Index)
                    .Select(x => x.Name!)
                    .ToList();
            }

            var awb = row.GetValueOrDefault("AwbFile") as byte[];

            acb = new AcbFile
            {
                Name = row.GetValueOrDefault("Name") as string,
                CueNames = cueNames,
                InternalAwb = awb is { Length: > 0 } ? awb : null,
                HasStreamAwb = row.GetValueOrDefault("StreamAwbAfs2Header") is byte[] { Length: > 0 },
            };
            return true;
        }
        catch
        {
            // Any malformed/unsupported table → caller falls back to pattern search.
            return false;
        }
    }
}
