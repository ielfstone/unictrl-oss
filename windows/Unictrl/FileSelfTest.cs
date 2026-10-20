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
        RunCase(true,false); RunCase(false,false); RunCase(false,true); NativeFileClipboard();
        Log.Write("files_test","passed encrypted_bidirectional_1GiB acceptance cancellation native_file_list bounded_streaming");
    }
    private static void RunCase(bool fromWindows,bool cancelTransfer)
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
            var offer=new FileOffer(Guid.NewGuid().ToString("D").ToUpperInvariant(),"drop",[new("source.bin",small.Length)]);
            string? failure=null;using var finished=new ManualResetEventSlim();using var chunkSent=new ManualResetEventSlim();
            using var desktop=new DesktopClipboard(_=>{},_=>{});
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
                    if(cancelTransfer)
                    {
                        try{Read(stream,cipher);throw new Exception("Cancelled transfer stayed open");}catch(IOException){}return;
                    }
                    for(var offset=FileProtocol.ChunkSize;offset<small.Length;offset+=FileProtocol.ChunkSize)stream.Write(cipher.Seal(FileProtocol.Chunk(0,offset,small[offset..Math.Min(small.Length,offset+FileProtocol.ChunkSize)])));
                    Send(stream,cipher,new("end",Index:0,Hash:Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(small))));
                    Send(stream,cipher,new("complete",Id:offer.Id));Check(FileProtocol.Decode(Read(stream,cipher)).Kind=="received","native receive acknowledged");
                }
            });
            using var files=service=new WindowsFileTransfer(settings,desktop,state=>{if(state.Status.StartsWith("File transfer stopped:",StringComparison.Ordinal)){failure=state.Status;finished.Set();}if(state.Status is "Files sent" or "Files received · saved in Downloads/Unictrl Received")finished.Set();},received);
            files.Attach(data=>{
                if(data[0]==0x34){filesControl(MessageType.FileCapabilities,[1,1]);return;}
                var command=FileProtocol.Decode(data);
                if(command.Kind=="offer")offer=command.Offer!;
                if(command.Kind is "offer" or "request")filesControl(MessageType.FileCommand,FileProtocol.Encode(new("ticket",Id:offer.Id,Ticket:Convert.ToBase64String(ticket),Rate:100))[1..]);
            });
            void filesControl(MessageType type,byte[] data)=>service!.Control(type,data);
            if(fromWindows)files.Offer([source,big]);
            else{files.Control(MessageType.FileCommand,FileProtocol.Encode(new("offer",Offer:offer))[1..]);Thread.Sleep(50);Check(!chunkSent.IsSet,"offer must not start data transfer");files.Receive();}
            if(cancelTransfer)
            {
                Check(chunkSent.Wait(8000),"partial chunk sent");Thread.Sleep(100);files.Cancel();Check(mock.Wait(8000),"cancel closes network");
                for(var i=0;i<100&&Directory.Exists(received)&&Directory.GetFileSystemEntries(received).Length>0;i++)Thread.Sleep(20);
                Check(!Directory.Exists(received)||Directory.GetFileSystemEntries(received).Length==0,"cancel removes partial files");
            }
            else
            {
                Check(finished.Wait(90000),"file transport completed");Check(failure is null,failure??"file transport failed");Check(mock.Wait(8000),"peer completed");mock.GetAwaiter().GetResult();
                if(!fromWindows){var path=Directory.GetFiles(received,"source.bin",SearchOption.AllDirectories).Single();Check(File.ReadAllBytes(path).SequenceEqual(small),"Mac to Windows received bytes");}
            }
        }
        finally{listener.Stop();try{Directory.Delete(root,true);}catch{}}
    }
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
    private static void Check(bool value,string name){if(!value)throw new Exception("File self-test: "+name);}
}
