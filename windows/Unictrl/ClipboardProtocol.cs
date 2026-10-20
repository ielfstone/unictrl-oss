using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace Unictrl;

internal sealed record ClipboardPacket(string Kind, string? Id = null, ulong? Revision = null, int? Count = null,
    string? Hash = null, int? Offset = null, string? Data = null);
internal sealed record ClipboardEffects(List<ClipboardPacket> Packets, string? Text = null, string? Id = null);

internal sealed class ClipboardReplica(bool authority)
{
    public const int MaxBytes = 1_048_576, ChunkBytes = 8_192;
    public const string Context = "unictrl clipboard v1";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    public ulong Revision { get; private set; }
    public string? LatestId { get; private set; }
    public string? LocalId { get; private set; }
    private string? pendingLocal;
    private sealed record Transfer(string Id, int Count, string Hash, MemoryStream Bytes);
    private Transfer? incoming;
    public void CancelIncoming() { incoming?.Bytes.Dispose(); incoming = null; }
    public void InvalidateLocal() { CancelIncoming(); LatestId = LocalId = pendingLocal = null; }
    public bool IsCurrent(string id) => LatestId == id && pendingLocal is null;
    private ClipboardPacket Order(string id)
    {
        Revision++; LatestId = id; CancelIncoming(); return new("order", id, Revision);
    }
    public List<ClipboardPacket> Copy(string text, string? id = null)
    {
        byte[] bytes;
        try { bytes = Utf8.GetBytes(text); } catch (EncoderFallbackException) { return []; }
        id ??= Guid.NewGuid().ToString("D").ToUpperInvariant();
        if (bytes.Length == 0 || bytes.Length > MaxBytes || text.Contains('\0') || !Guid.TryParse(id, out _)) return [];
        CancelIncoming(); LocalId = id;
        List<ClipboardPacket> result = [];
        if (authority) result.Add(Order(id)); else pendingLocal = id;
        result.Add(new("begin", id, Count: bytes.Length, Hash: Digest(bytes)));
        for (var offset = 0; offset < bytes.Length; offset += ChunkBytes)
            result.Add(new("chunk", id, Offset: offset, Data: Convert.ToBase64String(bytes.AsSpan(offset, Math.Min(ChunkBytes, bytes.Length - offset)))));
        result.Add(new("end", id)); return result;
    }
    public ClipboardEffects Receive(ClipboardPacket packet)
    {
        List<ClipboardPacket> replies = [];
        var id = packet.Id;
        if (!Guid.TryParse(id, out _)) return new(replies);
        switch (packet.Kind)
        {
            case "order":
                if (authority || packet.Revision is not { } next || next <= Revision) break;
                Revision = next; LatestId = id; CancelIncoming();
                if (pendingLocal == id) pendingLocal = null;
                break;
            case "begin":
                if (packet.Count is not (> 0 and <= MaxBytes) || packet.Hash is not { Length: 64 } hash || !hash.All(Uri.IsHexDigit)) break;
                if (authority) replies.Add(Order(id!));
                if (LatestId != id || pendingLocal is not null) break;
                incoming = new(id!, packet.Count.Value, hash, new MemoryStream());
                break;
            case "chunk":
                if (incoming is not { } transfer || transfer.Id != id) break;
                byte[] chunk;
                try { chunk = packet.Data is { Length: <= 11000 } data ? Convert.FromBase64String(data) : []; }
                catch (FormatException) { CancelIncoming(); break; }
                if (packet.Offset != transfer.Bytes.Length || chunk.Length is <= 0 or > ChunkBytes || transfer.Bytes.Length + chunk.Length > transfer.Count)
                { CancelIncoming(); break; }
                transfer.Bytes.Write(chunk); break;
            case "end":
                if (incoming is not { } end || end.Id != id) break;
                var bytes = end.Bytes.ToArray(); CancelIncoming();
                if (!IsCurrent(id!) || bytes.Length != end.Count || Digest(bytes) != end.Hash) break;
                try
                {
                    var text = Utf8.GetString(bytes);
                    if (!text.Contains('\0')) return new(replies, text, id);
                }
                catch (DecoderFallbackException) { }
                break;
        }
        return new(replies);
    }
    public static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static byte[] Encode(ClipboardPacket packet)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(packet, Options);
        if (json.Length >= 16367) throw new InvalidDataException("Clipboard record too large");
        return [0x33, .. json];
    }
    public static ClipboardPacket? Parse(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length >= 16368 || bytes[0] != 0x33) return null;
        try { return JsonSerializer.Deserialize<ClipboardPacket>(bytes.AsSpan(1), Options); }
        catch (JsonException) { return null; }
    }
}
