using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
namespace Unictrl;

/// Runs only on the isolated Windows CI desktop; uses generated files and a mock paired Mac.
internal static class FileSelfTest
{
    public static void Run()
    {
        RunCase(true,false); RunCase(false,false); RunCase(false,true); RunCase(false,false,true); LateTicketAfterCancellation(); NativeFileClipboard(); RepeatedNativeIntents();
        Log.Write("files_test","passed encrypted_bidirectional_1GiB acceptance cancellation native_file_list bounded_streaming");
    }
    private static void RunCase(bool fromWindows,bool cancelTransfer,bool supersede=false)
    {
        var root=Path.Combine(Path.GetTempPath(),"Unictrl file test "+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        try
        {
            var small=Enumerable.Range(0,35072).Select(i=>(byte)i).ToArray();
            var source=Path.Combine(root,"source.bin");File.WriteAllBytes(source,small);
            var big=Path.Combine(root,"one-gib.bin");using(var f=File.Create(big))f.SetLength(FileProtocol.FileLimit);
            var received=Path.Combine(root,"received");
            var settings=new Settings{Enabled=true,ClipboardEnabled=true,ClipboardFilesEnabled=true,FileRateMiB=100,
                DeviceId=Guid.NewGuid().ToString("D"),MacAddress="127.0.0.1",Port=((IPEndPoint)listener.LocalEndpoint).Port,PairingKey="ci-files-not-a-real-key"};
            var ticket=Enumerable.Repeat((byte)7,16).ToArray();
            var offer=new FileOffer(Guid.NewGuid().ToString("D").ToUpperInvariant(),supersede?"clipboard":"drop",[new("source.bin",small.Length)]);
            string? failure=null;using var finished=new ManualResetEventSlim();using var chunkSent=new ManualResetEventSlim();
            using var desktop=new DesktopClipboard(_=>{},_=>{});
            using var nativeReady=new ManualResetEventSlim();desktop.SetActive(true,nativeReady.Set);Check(nativeReady.Wait(3000),"native pasteboard ready");
            nativeReady.Reset();desktop.Write("original clipboard text",()=>true,nativeReady.Set);Check(nativeReady.Wait(3000),"native paste baseline");
            var beforeClipboard=GetClipboardSequenceNumber();
            FileViewState? lastState=null;using var requestedLatest=new ManualResetEventSlim();
            WindowsFileTransfer? service=null;
            var mock=Task.Run(async ()=>{
                using var socket=await listener.AcceptTcpClientAsync();using var stream=socket.GetStream();stream.ReadTimeout=60000;stream.WriteTimeout=60000;
                var hello=new byte[72];stream.ReadExactly(hello);
                Check(hello.AsSpan(0,8).SequenceEqual("UNICTRF1"u8)&&hello.AsSpan(40,16).SequenceEqual(settings.DeviceIdBytes)&&hello.AsSpan(56).SequenceEqual(ticket),"bound handshake");
                var salt=Enumerable.Repeat((byte)9,32).ToArray();stream.Write([.."FILEOK01"u8.ToArray(),..salt]);
                using var cipher=new SecureChannel(settings.PairingKey,hello[8..40],salt,true,FileProtocol.Context);
                Send(stream,cipher,new("ready",Id:offer.Id));Check(FileProtocol.Decode(Read(stream,cipher)).Kind=="ready","encrypted proof");
                if(fromWindows)
                {
                    using var inbox=new FileInbox(offer,Path.Combine(root,"mock-mac"));
                    while(true)
                    {
                        var data=Read(stream,cipher);
                        if(data[0]==0x36){var c=FileProtocol.ParseChunk(data);inbox.Append(c.Index,c.Offset,c.Bytes);continue;}
                        var command=FileProtocol.Decode(data);
                        if(command.Kind=="begin")inbox.Begin(command.Index!.Value);
                        else if(command.Kind=="end")inbox.End(command.Index!.Value,command.Hash!);
                        else if(command.Kind=="complete")break;
                    }
                    var paths=inbox.Complete();Check(File.ReadAllBytes(paths[0]).SequenceEqual(small),"small file bytes");
                    Check(new FileInfo(paths[1]).Length==FileProtocol.FileLimit,"1 GiB file size");
                    using(var file=File.OpenRead(paths[1])){file.Position=FileProtocol.FileLimit/2;Check(file.ReadByte()==0,"1 GiB middle byte");}
                    Send(stream,cipher,new("received",Id:offer.Id));
                }
                else
                {
                    Send(stream,cipher,new("begin",Index:0));
                    var first=small[..FileProtocol.ChunkSize];stream.Write(cipher.Seal(FileProtocol.Chunk(0,0,first)));chunkSent.Set();
                    if(supersede)
                    {
                        var replacement=new FileOffer(Guid.NewGuid().ToString("D").ToUpperInvariant(),"clipboard",[new("newest.bin",small.Length)]);
                        service!.Control(MessageType.FileCommand,FileProtocol.Encode(new("offer",Offer:replacement))[1..]);
                        Thread.Sleep(50); beforeClipboard=GetClipboardSequenceNumber();
                        Check(service.TryPaste(),"repeat paste while transferring consumed");
                    }
                    if(cancelTransfer)
                    {
                        try{Read(stream,cipher);throw new Exception("Cancelled transfer stayed open");}catch(IOException){}return;
                    }
                    for(var offset=FileProtocol.ChunkSize;offset<small.Length;offset+=FileProtocol.ChunkSize)stream.Write(cipher.Seal(FileProtocol.Chunk(0,offset,small[offset..Math.Min(small.Length,offset+FileProtocol.ChunkSize)])));
                    Send(stream,cipher,new("end",Index:0,Hash:Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(small))));
                    Send(stream,cipher,new("complete",Id:offer.Id));Check(FileProtocol.Decode(Read(stream,cipher)).Kind=="received","native receive acknowledged");
                }
            });
            using var files=service=new WindowsFileTransfer(settings,desktop,state=>{lastState=state;if(state.Status.StartsWith("File transfer stopped:",StringComparison.Ordinal)){failure=state.Status;finished.Set();}if(state.Status is "Files sent" or "Files received in your chosen folder" or "Files ready to paste · temporary cache (1 hour)")finished.Set();},received);
            files.Attach(data=>{
                if(data[0]==0x34){filesControl(MessageType.FileCapabilities,[1,1]);return;}
                var command=FileProtocol.Decode(data);
                if(command.Kind=="offer")offer=command.Offer!;
                if(command.Kind is "offer" or "request")
                {
                    var id=command.Kind=="offer"?offer.Id:command.Id;
                    filesControl(MessageType.FileCommand,FileProtocol.Encode(new("ticket",Id:id,Ticket:Convert.ToBase64String(ticket),Rate:100,Request:command.Request))[1..]);
                    if(command.Kind=="request"&&id!=offer.Id)requestedLatest.Set();
                }
            });
            void filesControl(MessageType type,byte[] data)=>service!.Control(type,data);
            if(fromWindows)files.Offer([source,big]);
            else{files.Control(MessageType.FileCommand,FileProtocol.Encode(new("offer",Offer:offer))[1..]);Thread.Sleep(50);Check(!chunkSent.IsSet,"offer must not start data transfer");if(supersede)Check(files.TryPaste(),"native paste starts download");else files.Receive();}
            if(cancelTransfer)
            {
                Check(chunkSent.Wait(8000),"partial chunk sent");Thread.Sleep(100);files.Cancel();
                files.Receive();Thread.Sleep(100);Check(lastState?.Busy==true,"old cancellation cannot reset immediate retry");files.Cancel();
                Check(mock.Wait(8000),"cancel closes network");
                for(var i=0;i<100&&Directory.Exists(received)&&Directory.GetDirectories(received,".partial-*").Length>0;i++)Thread.Sleep(20);
                Check(!Directory.Exists(received)||Directory.GetDirectories(received,".partial-*").Length==0,"cancel removes partial files");
            }
            else
            {
                Check(finished.Wait(90000),"file transport completed");Check(failure is null,failure??"file transport failed");Check(mock.Wait(8000),"peer completed");mock.GetAwaiter().GetResult();
                if(!fromWindows){var path=Directory.GetFiles(received,"source.bin",SearchOption.AllDirectories).Single();Check(File.ReadAllBytes(path).SequenceEqual(small),"Mac to Windows received bytes");}
                if(supersede)
                {
                    Check(GetClipboardSequenceNumber()==beforeClipboard,"older transfer did not overwrite clipboard");
                    Check(lastState?.Incoming.Contains("newest.bin")==true,"new offer survived old completion");
                    Check(files.TryPaste(),"new offer remains pasteable");Check(requestedLatest.Wait(3000),"new offer requested after old completion");files.Cancel();
                }
            }
        }
        finally{listener.Stop();try{Directory.Delete(root,true);}catch{}}
    }
    private static void LateTicketAfterCancellation()
    {
        using var desktop=new DesktopClipboard(_=>{},_=>{});var requests=new List<FileCommand>();var connections=0;
        var settings=new Settings{Enabled=true,ClipboardEnabled=true,ClipboardFilesEnabled=true,MacAddress="127.0.0.1",Port=1,DeviceId=Guid.NewGuid().ToString(),PairingKey="ci-late-ticket-only"};
        using var files=new WindowsFileTransfer(settings,desktop,state=>{if(state.Status=="Connecting file transfer…")Interlocked.Increment(ref connections);});
        files.Attach(data=>{if(data[0]==0x35){var command=FileProtocol.Decode(data);if(command.Kind=="request")requests.Add(command);}});
        files.Control(MessageType.FileCapabilities,[1,1]);
        var offer=new FileOffer(Guid.NewGuid().ToString(),"drop",[new("late.bin",1)]);
        files.Control(MessageType.FileCommand,FileProtocol.Encode(new("offer",Offer:offer))[1..]);files.Receive();
        var first=requests.Single();files.Cancel();
        var late=FileProtocol.Encode(new("ticket",Id:offer.Id,Ticket:Convert.ToBase64String(new byte[16]),Request:first.Request));
        files.Control(MessageType.FileCommand,late[1..]);Check(connections==0,"cancelled ticket ignored");
        files.Receive();Check(requests.Count==2&&requests[1].Request!=first.Request,"retry gets a fresh request ID");
        files.Control(MessageType.FileCommand,late[1..]);Check(connections==0,"old ticket cannot bind to retry");files.Cancel();
    }
    private static void RepeatedNativeIntents()
    {
        WindowsFileTransfer? files=null; using var ready=new ManualResetEventSlim(); using var requested=new ManualResetEventSlim();
        using var desktop=new DesktopClipboard(_=>files?.LocalClipboardChanged(),_=>{});
        var settings=new Settings{Enabled=true,ClipboardEnabled=true,ClipboardFilesEnabled=true};
        FileViewState? state=null; var requests=new List<string>();
        using var service=files=new WindowsFileTransfer(settings,desktop,s=>state=s);
        desktop.RemoteFilesChanged=files.RemoteClipboardWritten;
        desktop.SetActive(true,ready.Set);Check(ready.Wait(3000),"repeat test native ready");
        files.Attach(data=>{if(data[0]!=0x35)return;var command=FileProtocol.Decode(data);if(command.Kind=="request"){requests.Add(command.Id!);requested.Set();}});
        files.Control(MessageType.FileCapabilities,[1,1]);
        for(var i=0;i<12;i++)
        {
            ready.Reset(); requested.Reset();
            var path=Path.Combine(Path.GetTempPath(),"old-file.bin");
            desktop.Write(new ClipboardContent(Files:[path]),()=>true,()=>{
                var offer=new FileOffer(Guid.NewGuid().ToString(),"clipboard",[new($"latest-{i}.bin",1)]);
                desktop.BeforeRemoteFileCommand(()=>{files.Control(MessageType.FileCommand,FileProtocol.Encode(new("offer",Offer:offer))[1..]);ready.Set();});
            });
            Check(ready.Wait(3000),"new intent installed");Check(!IsClipboardFormatAvailable(15),"pending intent cleared stale CF_HDROP");
            Check(files.TryPaste(),"new intent captures paste");Check(requested.Wait(3000),"new intent requested");
            Check(state?.Incoming.Contains($"latest-{i}.bin")==true,"latest offer selected");files.Cancel();
        }
        Check(requests.Distinct().Count()==12,"all repeated copies have independent intents");
        files.Control(MessageType.FileCommand,FileProtocol.Encode(new("blocked",Reason:"too-large"))[1..]);Thread.Sleep(100);
        Check(state?.Notice.Contains("100 MiB")==true,"oversize explanation visible");Check(files.TryPaste(),"oversize paste cannot paste old file");
        desktop.Write("repeat file test complete",()=>true);Thread.Sleep(50);
    }
    [DllImport("user32.dll")]private static extern bool IsClipboardFormatAvailable(uint format);
    private static void NativeFileClipboard()
    {
        var root=Path.Combine(Path.GetTempPath(),"Unictrl native files "+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var path=Path.Combine(root,"test.bin");File.WriteAllBytes(path,[1,2,3]);
            using var initialized=new ManualResetEventSlim();using var copied=new ManualResetEventSlim();
            using var observer=new DesktopClipboard(content=>{if(content?.Files?.SequenceEqual(new[]{path})==true)copied.Set();},_=>{});
            using var writer=new DesktopClipboard(_=>{},_=>{});
            observer.SetActive(true,initialized.Set);Check(initialized.Wait(3000),"file clipboard observer");
            using var writerReady=new ManualResetEventSlim();writer.SetActive(true,writerReady.Set);Check(writerReady.Wait(3000),"file clipboard writer");
            writer.Write(new ClipboardContent(Files:[path]),()=>true);Check(copied.Wait(3000),"native file list roundtrip");writer.Write("file test complete",()=>true);Thread.Sleep(50);
        }
        finally{try{Directory.Delete(root,true);}catch{}}
    }
    private static void Send(NetworkStream stream,SecureChannel cipher,FileCommand command)=>stream.Write(cipher.Seal(FileProtocol.Encode(command)));
    private static byte[] Read(NetworkStream stream,SecureChannel cipher)
    {var prefix=new byte[2];stream.ReadExactly(prefix);var length=BinaryPrimitives.ReadUInt16BigEndian(prefix);if(length is <17 or >SecureChannel.MaxFrame)throw new IOException("Bad frame");var bytes=new byte[length];stream.ReadExactly(bytes);return cipher.Open(bytes);}
    [DllImport("user32.dll")]private static extern uint GetClipboardSequenceNumber();
    private static void Check(bool value,string name){if(!value)throw new Exception("File self-test: "+name);}
}
