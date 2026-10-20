using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace Unictrl;
internal sealed record FileEntry(string Name, long Size);
internal sealed record FileOffer(string Id, string Mode, FileEntry[] Files)
{
    [JsonIgnore] public long Size => Files.Sum(f => f.Size);
    [JsonIgnore] public string Summary => $"{Files.Length} file(s) · {Size / 1048576.0:0.##} MiB";
}
internal sealed record FileCommand(string Kind, FileOffer? Offer = null, string? Id = null, string? Ticket = null,
    int? Rate = null, int? Index = null, string? Hash = null, string? Request = null, string? Reason = null);
internal static class FileProtocol
{
    public const long ClipboardLimit = 100L * 1048576, FileLimit = 1073741824, BatchLimit = 4L * 1073741824;
    public const int ChunkSize = 16300;
    public const string Context = "unictrl files v1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    public static bool ValidName(string name)
    {
        if (string.IsNullOrEmpty(name) || Encoding.UTF8.GetByteCount(name) > 240 || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ') || name.Any(c => char.IsControl(c) || "/\\:*?\"<>|".Contains(c))) return false;
        var stem = name.Split('.')[0].ToUpperInvariant();
        return stem is not ("CON" or "PRN" or "AUX" or "NUL") && !Enumerable.Range(1,9).Any(i => stem == "COM"+i || stem == "LPT"+i);
    }
    public static void Validate(FileOffer offer)
    {
        if (!Guid.TryParse(offer.Id, out _) || offer.Mode is not ("clipboard" or "drop") || offer.Files is not {Length: >= 1 and <= 32}) throw new InvalidDataException("Invalid file offer");
        long total = 0; var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var file in offer.Files)
        {
            if (!ValidName(file.Name) || file.Size < 0 || file.Size > FileLimit || !names.Add(file.Name.Normalize())) throw new InvalidDataException("Unsupported filename, duplicate name or file larger than 1 GiB");
            total += file.Size;
        }
        if (total > (offer.Mode == "clipboard" ? ClipboardLimit : BatchLimit))
            throw new InvalidDataException(offer.Mode == "clipboard" ? "Clipboard files exceed 100 MiB; use drag and drop (1 GiB per file / 4 GiB per batch)" : "File batch exceeds 4 GiB");
    }
    public static byte[] Encode(FileCommand command)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(command, Json);
        if(json.Length >= 16000) throw new InvalidDataException("File command too large"); return [0x35, ..json];
    }
    public static FileCommand Decode(byte[] bytes)
    {
        if(bytes.Length is < 2 or > 16000 || bytes[0]!=0x35) throw new InvalidDataException("Invalid file command");
        return JsonSerializer.Deserialize<FileCommand>(bytes.AsSpan(1), Json) ?? throw new InvalidDataException("Invalid file command");
    }
    public static byte[] Chunk(int index, long offset, byte[] bytes)
    {
        var data = new byte[11+bytes.Length]; data[0]=0x36; BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(1),(ushort)index);
        BinaryPrimitives.WriteInt64BigEndian(data.AsSpan(3),offset); bytes.CopyTo(data,11); return data;
    }
    public static (int Index,long Offset,byte[] Bytes) ParseChunk(byte[] data)
    {
        if(data.Length <= 11 || data.Length > ChunkSize+11 || data[0]!=0x36) throw new InvalidDataException("Invalid file chunk");
        var offset=BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(3)); if(offset<0) throw new InvalidDataException("Invalid file offset");
        return (BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(1)),offset,data[11..]);
    }
    public static int Rate(int local,int remote) => Math.Clamp(new[]{local,remote}.Where(v=>v>0).DefaultIfEmpty(100).Min(),1,100);
}
internal sealed class FileInbox : IDisposable
{
    private readonly FileOffer offer;
    private readonly string partial, finished;
    private FileStream? file;
    private IncrementalHash? hash;
    private int index;
    private long offset;
    private bool committed;
    public FileInbox(FileOffer offer,string directory)
    {
        FileProtocol.Validate(offer); this.offer=offer; Directory.CreateDirectory(directory);
        var drive=new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!);
        if(drive.AvailableFreeSpace < offer.Size+16*1048576) throw new IOException("Not enough free space");
        var id=Guid.NewGuid().ToString("N"); partial=Path.Combine(directory,".partial-"+id); finished=Path.Combine(directory,"Received-"+id);
        Directory.CreateDirectory(Path.Combine(partial,"parts")); Directory.CreateDirectory(Path.Combine(partial,"ready"));
    }
    public void Begin(int fileIndex)
    {
        if(file is not null || fileIndex!=index || index>=offer.Files.Length) throw new InvalidDataException("File order mismatch");
        file=new FileStream(Path.Combine(partial,"parts",".part-"+index),FileMode.CreateNew,FileAccess.Write,FileShare.None,65536);
        offset=0; hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    }
    public void Append(int fileIndex,long expected,byte[] bytes)
    {
        if(file is null || fileIndex!=index || expected!=offset || bytes.Length is <1 or >FileProtocol.ChunkSize || offset+bytes.Length>offer.Files[index].Size) throw new InvalidDataException("File chunk order or size mismatch");
        file.Write(bytes); hash!.AppendData(bytes); offset+=bytes.Length;
    }
    public void End(int fileIndex,string digest)
    {
        if(file is null || fileIndex!=index || offset!=offer.Files[index].Size || Convert.ToHexStringLower(hash!.GetHashAndReset())!=digest) throw new InvalidDataException("File integrity check failed");
        file.Flush(true); file.Dispose(); file=null; hash.Dispose(); hash=null;
        File.Move(Path.Combine(partial,"parts",".part-"+index),Path.Combine(partial,"ready",offer.Files[index].Name),false); index++;
    }
    public string[] Complete()
    {
        if(file is not null || index!=offer.Files.Length) throw new InvalidDataException("Incomplete file transfer");
        Directory.Move(Path.Combine(partial,"ready"),finished); committed=true; try{Directory.Delete(partial,true);}catch{} return offer.Files.Select(f=>Path.Combine(finished,f.Name)).ToArray();
    }
    public void Dispose(){file?.Dispose(); hash?.Dispose(); if(!committed){try{Directory.Delete(partial,true);}catch{}}}
}
