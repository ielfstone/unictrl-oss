using System.Text.Json;
namespace Unictrl;

internal sealed record TransferRecord(DateTime Date, string[] Names, long Bytes, string Direction, string Result)
{
    public string Summary => $"{Date.ToLocalTime():g} · {Direction} · {string.Join(", ", Names)} · {Bytes / 1048576.0:0.##} MiB · {Result}";
}
internal sealed class TransferHistory
{
    private readonly string file;
    public string Cache { get; }
    public List<TransferRecord> Records { get; private set; }
    public TransferHistory(string? root = null)
    {
        var support = root ?? Settings.Directory;
        file = Path.Combine(support, "transfer-history.json");
        Cache = Path.Combine(root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Unictrl", "Cache"), "ClipboardFiles");
        try { Records = JsonSerializer.Deserialize<List<TransferRecord>>(File.ReadAllText(file)) ?? []; } catch { Records = []; }
        CleanCache();
    }
    public void Add(FileOffer offer, bool sent, string result)
    {
        Records.Insert(0, new(DateTime.UtcNow, offer.Files.Select(f => f.Name).ToArray(), offer.Size, sent ? "Sent" : "Received", result));
        Records = Records.Take(100).ToList(); Save();
    }
    public void Clear() { Records.Clear(); Save(); }
    private void Save()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(Records)); File.Move(file + ".tmp", file, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    public void CleanCache(bool all = false, DateTime? now = null)
    {
        try
        {
            if (!Directory.Exists(Cache)) return;
            foreach (var path in Directory.GetDirectories(Cache))
            {
                var name = Path.GetFileName(path);
                if (!(name.StartsWith("Received-", StringComparison.Ordinal) || name.StartsWith(".partial-", StringComparison.Ordinal)) || !Guid.TryParse(name[9..], out _) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                if (all || (now ?? DateTime.UtcNow) - Directory.GetLastWriteTimeUtc(path) >= TimeSpan.FromHours(1))
                    try { Directory.Delete(path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
