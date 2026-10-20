using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
namespace Unictrl;

internal sealed record FileViewState(string Status, string Incoming = "", bool Busy = false, double Progress = 0, bool HasReceived = false);
internal sealed class WindowsFileTransfer : IDisposable
{
    private readonly Settings settings;
    private readonly Action<FileViewState> changed;
    private readonly DesktopClipboard desktop;
    private readonly string receiveDirectory;
    private readonly object gate = new();
    private Action<byte[]>? send;
    private bool remoteEnabled, busy;
    private readonly Dictionary<string, Source> sources = new();
    private readonly Dictionary<string, FileOffer> offers = new();
    private sealed record Source(FileOffer Offer, string[] Paths, DateTime[] Dates, DateTime Created);
    private sealed record Paste(uint Sequence, nint Window, long Intent);
    private long clipboardIntent, connectionGeneration;
    private CancellationTokenSource? transfer;
    private string? activeId, pasteId, pendingRequest;
    private uint pasteSequence;
    private bool pasteScheduled;
    private Paste? pendingPaste;
    private string[] received = [];
    private readonly FilePasteHook hook;
    public WindowsFileTransfer(Settings settings, DesktopClipboard desktop, Action<FileViewState> changed, string? receiveDirectory = null)
    { this.settings=settings; this.desktop=desktop; this.changed=changed; this.receiveDirectory=receiveDirectory??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Downloads","Unictrl Received"); hook=new FilePasteHook(TryPaste); }
    private bool Ready => settings.Enabled && settings.ClipboardEnabled && settings.ClipboardFilesEnabled && remoteEnabled && send is not null;
    private void Send(FileCommand command) { send?.Invoke(FileProtocol.Encode(command)); }
    private void Report(string text,double progress=0)
    {
        var offer=offers.Values.OrderByDescending(o=>o.Mode).FirstOrDefault();
        changed(new(text,offer is null ? "" : offer.Summary+" · "+string.Join(", ",offer.Files.Take(3).Select(f=>f.Name)),busy,progress,received.Length>0));
    }
    public void Attach(Action<byte[]> control) { lock(gate){Reset(); send=control; Announce();} }
    public void Detach(){lock(gate){Reset();send=null;remoteEnabled=false;Report("File connection offline");}}
    public void Refresh(){lock(gate){Reset();Announce();}}
    private void Announce()
    {
        send?.Invoke([(byte)MessageType.FileCapabilities,1,settings.Enabled&&settings.ClipboardEnabled&&settings.ClipboardFilesEnabled?(byte)1:(byte)0]);
        Report(Ready?"Files ready · copy ≤100 MiB · drag ≤1 GiB/file":"Enable files on both computers");
    }
    public void Control(MessageType type,byte[] bytes)
    {
        lock(gate)
        {
            if(type==MessageType.FileCapabilities)
            {
                if(bytes.Length!=2||bytes[0]!=1||bytes[1]>1)return;
                remoteEnabled=bytes[1]==1;if(!Ready)Reset();Report(Ready?"Files ready · copy ≤100 MiB · drag ≤1 GiB/file":"Enable files on both computers");return;
            }
            if(!Ready)return;
            FileCommand command;
            try{command=FileProtocol.Decode([(byte)MessageType.FileCommand,..bytes]);}catch{return;}
            switch(command.Kind)
            {
                case "offer":
                    if(command.Offer is not {} offer)return;
                    try{FileProtocol.Validate(offer);}catch{return;}
                    foreach(var id in offers.Where(p=>p.Value.Mode==offer.Mode).Select(p=>p.Key).ToArray())offers.Remove(id);
                    offers[offer.Id]=offer;
                    if(offer.Mode=="clipboard"){Interlocked.Increment(ref clipboardIntent);pasteId=offer.Id;pasteSequence=GetClipboardSequenceNumber();pasteScheduled=false;}
                    Report("Files offered · paste or click Receive to transfer");break;
                case "clear":
                    foreach(var id in offers.Where(p=>p.Value.Mode=="clipboard").Select(p=>p.Key).ToArray())offers.Remove(id);
                    Interlocked.Increment(ref clipboardIntent);pasteId=null;pasteScheduled=false;Report("Clipboard file offer cleared");break;
                case "ticket":
                    if(command.Id is not {} ticketId || command.Ticket is null || (busy&&activeId!=ticketId))return;
                    byte[] ticket;try{ticket=Convert.FromBase64String(command.Ticket);}catch{return;}
                    if(ticket.Length!=16)return;
                    var source=sources.GetValueOrDefault(ticketId);
                    var incoming=offers.GetValueOrDefault(ticketId);
                    if(source is null && (incoming is null || !busy || activeId!=ticketId || command.Request!=pendingRequest))return;
                    if(source is not null && DateTime.UtcNow-source.Created>TimeSpan.FromMinutes(30))return;
                    var selectedOffer=source?.Offer??incoming!;
                    busy=true;activeId=ticketId;transfer?.Cancel();transfer?.Dispose();transfer=new();
                    var cancel=transfer.Token;var current=++connectionGeneration;var paste=pendingPaste;var rate=FileProtocol.Rate(settings.FileRateMiB,command.Rate??10);
                    Report("Connecting file transfer…");
                    _=Task.Run(()=>Run(ticket,selectedOffer,source,rate,paste,current,cancel));break;
                case "withdraw":
                    if(command.Id is not {} withdrawn)return;
                    offers.Remove(withdrawn);if(pasteId==withdrawn){Interlocked.Increment(ref clipboardIntent);pasteId=null;pasteScheduled=false;}
                    if(activeId==withdrawn&&!sources.ContainsKey(withdrawn))Cancel(false);else Report("File offer withdrawn");break;
                case "cancel":if(command.Id==activeId)Cancel(false);break;
            }
        }
    }
    public void RemoteClipboardWritten(){lock(gate){if(pasteId is not null)pasteSequence=GetClipboardSequenceNumber();}}
    public void LocalClipboardChanged()
    {
        lock(gate)
        {
            Interlocked.Increment(ref clipboardIntent);var old=sources.Where(p=>p.Value.Offer.Mode=="clipboard").Select(p=>p.Key).ToArray();
            foreach(var id in old)sources.Remove(id);pasteId=null;pasteScheduled=false;
            foreach(var id in offers.Where(p=>p.Value.Mode=="clipboard").Select(p=>p.Key).ToArray())offers.Remove(id);
            if(old.Length>0)Send(new("clear"));
        }
    }
    public void Offer(string[] paths,bool clipboard=false)
    {
        lock(gate)
        {
            if(!Ready){Report("Enable files on both computers");return;}
            try
            {
                if(paths.Length is <1 or >32)throw new InvalidDataException("Choose 1–32 regular files; zip folders first");
                var infos=paths.Select(p=>new FileInfo(p)).ToArray();
                if(infos.Any(f=>!f.Exists||(f.Attributes&(FileAttributes.Directory|FileAttributes.ReparsePoint))!=0))throw new InvalidDataException("Folders and links are not supported; zip folders first");
                var offer=new FileOffer(Guid.NewGuid().ToString("D").ToUpperInvariant(),clipboard?"clipboard":"drop",infos.Select(f=>new FileEntry(f.Name,f.Length)).ToArray());
                FileProtocol.Validate(offer);
                foreach(var id in sources.Where(p=>p.Value.Offer.Mode==offer.Mode).Select(p=>p.Key).ToArray())sources.Remove(id);
                sources[offer.Id]=new(offer,paths,infos.Select(f=>f.LastWriteTimeUtc).ToArray(),DateTime.UtcNow);
                Send(new("offer",Offer:offer));Report("Offered "+offer.Summary+" · waiting for receiver");
            }
            catch(Exception error){Report(error.Message);}
        }
    }
    public void Receive()
    {
        lock(gate){var offer=offers.Values.OrderByDescending(o=>o.Mode).FirstOrDefault();if(offer is not null)Request(offer,null);}
    }
    internal bool TryPaste()
    {
        lock(gate)
        {
            if(!Ready||pasteId is null||GetClipboardSequenceNumber()!=pasteSequence||!offers.TryGetValue(pasteId,out var offer))return false;
            if(pasteScheduled||busy)return true;
            pasteScheduled=true;
            var context=new Paste(GetClipboardSequenceNumber(),GetForegroundWindow(),Volatile.Read(ref clipboardIntent));
            _=Task.Run(()=>{lock(gate){if(activeId is null && pasteScheduled && context.Intent==Volatile.Read(ref clipboardIntent) && pasteId==offer.Id)Request(offer,context);}});return true;
        }
    }
    private void Request(FileOffer offer,Paste? paste)
    {
        if(!Ready||busy||paste is not null&&paste.Intent!=Volatile.Read(ref clipboardIntent)){pasteScheduled=false;return;}
        busy=true;activeId=offer.Id;pendingPaste=paste;pendingRequest=Guid.NewGuid().ToString("D").ToUpperInvariant();Send(new("request",Id:offer.Id,Rate:settings.FileRateMiB,Request:pendingRequest));Report("Requesting files…");
        var id=activeId;var request=pendingRequest;
        _=Task.Delay(10000).ContinueWith(_=>{lock(gate){if(activeId==id&&pendingRequest==request&&transfer is null){busy=false;activeId=null;pasteScheduled=false;Report("File connection timed out; try Receive again");}}});
    }
    private async Task Run(byte[] ticket,FileOffer offer,Source? source,int rate,Paste? paste,long current,CancellationToken cancel)
    {
        try
        {
            using var client=new TcpClient{NoDelay=true};using var registration=cancel.Register(client.Dispose);
            using var handshake=CancellationTokenSource.CreateLinkedTokenSource(cancel);handshake.CancelAfter(8000);
            await client.ConnectAsync(settings.MacAddress,settings.Port,handshake.Token);
            using var stream=client.GetStream();stream.ReadTimeout=30000;stream.WriteTimeout=30000;
            var salt=RandomNumberGenerator.GetBytes(32);
            byte[] hello=[.."UNICTRF1"u8.ToArray(),..salt,..settings.DeviceIdBytes,..ticket];
            await stream.WriteAsync(hello,cancel);
            var reply=new byte[40];await stream.ReadExactlyAsync(reply,handshake.Token);
            if(!reply.AsSpan(0,8).SequenceEqual("FILEOK01"u8))throw new InvalidDataException("File handshake failed");
            using var cipher=new SecureChannel(settings.PairingKey,salt,reply[8..],false,FileProtocol.Context);
            var proof=FileProtocol.Decode(await Read(stream,cipher,handshake.Token));
            if(proof.Kind!="ready"||proof.Id!=offer.Id)throw new InvalidDataException("File authentication failed");
            await Write(stream,cipher,FileProtocol.Encode(new("ready",Id:offer.Id)),cancel);
            var notifyAt=Stopwatch.StartNew();
            void Progress(long count)
            {
                if(notifyAt.ElapsedMilliseconds<200)return;notifyAt.Restart();
                lock(gate){if(activeId==offer.Id&&current==connectionGeneration)Report($"{(source is null?"Receiving":"Sending")} files · {(offer.Size==0?100:count*100/offer.Size)}%",offer.Size==0?1:(double)count/offer.Size);}
            }
            string[] paths=[];
            if(source is not null)await SendFiles(source,stream,cipher,rate,Progress,cancel);
            else paths=await ReceiveFiles(offer,stream,cipher,receiveDirectory,Progress,cancel);
            lock(gate)
            {
                if(activeId!=offer.Id||current!=connectionGeneration)return;
                transfer?.Dispose();transfer=null;activeId=null;busy=false;pendingPaste=null;
                if(paths.Length>0)
                {
                    received=paths;offers.Remove(offer.Id);if(pasteId==offer.Id){pasteId=null;pasteScheduled=false;}
                    if(paste is not null && Volatile.Read(ref clipboardIntent)==paste.Intent && GetClipboardSequenceNumber()==paste.Sequence)
                    {
                        desktop.Write(new ClipboardContent(Files:paths),()=>Volatile.Read(ref clipboardIntent)==paste.Intent && GetClipboardSequenceNumber()==paste.Sequence,
                            ()=>{if(GetForegroundWindow()==paste.Window)FilePasteHook.Replay();});
                    }
                }
                Report(source is null?"Files received · saved in Downloads/Unictrl Received":"Files sent",1);
            }
        }
        catch(Exception error)
        {
            lock(gate){if(activeId!=offer.Id||current!=connectionGeneration)return;transfer?.Dispose();transfer=null;activeId=null;busy=false;pasteScheduled=false;pendingPaste=null;Report(cancel.IsCancellationRequested?"File transfer cancelled":"File transfer stopped: "+error.Message);}
        }
    }
    private static async Task Write(NetworkStream stream,SecureChannel cipher,byte[] data,CancellationToken cancel)
    {if(data.Length+16>SecureChannel.MaxFrame)throw new InvalidDataException("File frame too large");await stream.WriteAsync(cipher.Seal(data),cancel);}
    private static async Task<byte[]> Read(NetworkStream stream,SecureChannel cipher,CancellationToken cancel)
    {
        var prefix=new byte[2];using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancel);timeout.CancelAfter(30000);
        await stream.ReadExactlyAsync(prefix,timeout.Token);var n=BinaryPrimitives.ReadUInt16BigEndian(prefix);
        if(n is <17 or >SecureChannel.MaxFrame)throw new InvalidDataException("Invalid file frame");
        var bytes=new byte[n];await stream.ReadExactlyAsync(bytes,timeout.Token);return cipher.Open(bytes);
    }
    private static async Task SendFiles(Source source,NetworkStream stream,SecureChannel cipher,int rate,Action<long> progress,CancellationToken cancel)
    {
        long total=0;var clock=Stopwatch.StartNew();var buffer=new byte[FileProtocol.ChunkSize];
        for(var index=0;index<source.Paths.Length;index++)
        {
            var info=new FileInfo(source.Paths[index]);
            if(!info.Exists||info.Length!=source.Offer.Files[index].Size||info.LastWriteTimeUtc!=source.Dates[index]||(info.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Source file changed; copy again");
            using var file=new FileStream(info.FullName,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.SequentialScan|FileOptions.Asynchronous);
            using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);long offset=0;int count;
            await Write(stream,cipher,FileProtocol.Encode(new("begin",Index:index)),cancel);
            while((count=await file.ReadAsync(buffer,cancel))>0)
            {
                var bytes=buffer.AsSpan(0,count).ToArray();hash.AppendData(bytes);
                await Write(stream,cipher,FileProtocol.Chunk(index,offset,bytes),cancel);offset+=count;total+=count;
                var delay=(double)total/(rate*1048576)-clock.Elapsed.TotalSeconds;
                if(delay>0.005)await Task.Delay(TimeSpan.FromSeconds(Math.Min(delay,0.5)),cancel);progress(total);
            }
            if(offset!=source.Offer.Files[index].Size||File.GetLastWriteTimeUtc(info.FullName)!=source.Dates[index])throw new IOException("Source file changed during transfer");
            await Write(stream,cipher,FileProtocol.Encode(new("end",Index:index,Hash:Convert.ToHexStringLower(hash.GetHashAndReset()))),cancel);
        }
        await Write(stream,cipher,FileProtocol.Encode(new("complete",Id:source.Offer.Id)),cancel);
        var ack=FileProtocol.Decode(await Read(stream,cipher,cancel));if(ack.Kind!="received"||ack.Id!=source.Offer.Id)throw new IOException("Receiver did not confirm files");
    }
    private static async Task<string[]> ReceiveFiles(FileOffer offer,NetworkStream stream,SecureChannel cipher,string downloads,Action<long> progress,CancellationToken cancel)
    {
        using var inbox=new FileInbox(offer,downloads);long total=0;
        while(true)
        {
            var data=await Read(stream,cipher,cancel);
            if(data[0]==0x36){var c=FileProtocol.ParseChunk(data);inbox.Append(c.Index,c.Offset,c.Bytes);total+=c.Bytes.Length;progress(total);continue;}
            var command=FileProtocol.Decode(data);
            switch(command.Kind)
            {
                case "begin":inbox.Begin(command.Index??-1);break;
                case "end":inbox.End(command.Index??-1,command.Hash??"");break;
                case "complete":
                    if(command.Id!=offer.Id)throw new IOException("File offer mismatch");
                    cancel.ThrowIfCancellationRequested();var paths=inbox.Complete();await Write(stream,cipher,FileProtocol.Encode(new("received",Id:offer.Id)),cancel);return paths;
                default:throw new IOException("Unexpected file record");
            }
        }
    }
    public void CopyReceived(){lock(gate){if(received.Length>0)desktop.Write(new ClipboardContent(Files:received),()=>true);}}
    public void ShowReceived(){lock(gate){if(received.Length>0)Process.Start(new ProcessStartInfo("explorer.exe", "/select,\""+received[0]+"\""){UseShellExecute=true});}}
    public void Cancel(bool notify=true)
    {
        lock(gate){if(notify&&activeId is {} id){if(sources.Remove(id))Send(new("withdraw",Id:id));Send(new("cancel",Id:id));}ResetTransfer();Report("File transfer cancelled");}
    }
    private void ResetTransfer(){connectionGeneration++;Interlocked.Increment(ref clipboardIntent);pendingRequest=null;activeId=null;transfer?.Cancel();transfer?.Dispose();transfer=null;busy=false;pendingPaste=null;pasteScheduled=false;}
    private void Reset(){ResetTransfer();sources.Clear();offers.Clear();pasteId=null;pasteScheduled=false;}
    public void Dispose(){lock(gate){Reset();send=null;}hook.Dispose();}
    [DllImport("user32.dll")]private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")]private static extern nint GetForegroundWindow();
}
