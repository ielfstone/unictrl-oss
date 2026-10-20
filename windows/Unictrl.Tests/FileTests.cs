using System.Text.Json;
using Xunit;
namespace Unictrl.Tests;
public sealed class FileTests
{
    private sealed record Fixture(FileOffer Offer,string Data,string Sha256,string FirstChunk);
    private static Fixture Sample()=>JsonSerializer.Deserialize<Fixture>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"files","v1.json")),new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    [Fact]public void SeparateClipboardAndDropLimits()
    {
        var id=Guid.NewGuid().ToString();
        FileProtocol.Validate(new(id,"clipboard",[new("a",FileProtocol.ClipboardLimit)]));
        Assert.Throws<InvalidDataException>(()=>FileProtocol.Validate(new(id,"clipboard",[new("a",FileProtocol.ClipboardLimit+1)])));
        FileProtocol.Validate(new(id,"drop",Enumerable.Range(0,4).Select(i=>new FileEntry("file"+i,FileProtocol.FileLimit)).ToArray()));
        Assert.Throws<InvalidDataException>(()=>FileProtocol.Validate(new(id,"drop",[new("a",FileProtocol.FileLimit+1)])));
        Assert.Throws<InvalidDataException>(()=>FileProtocol.Validate(new(id,"drop",Enumerable.Range(0,5).Select(i=>new FileEntry("file"+i,FileProtocol.FileLimit)).ToArray())));
    }
    [Fact]public void UnsafeNamesAndDuplicatesRejected()
    {
        foreach(var name in new[]{"../escape","a/b","a\\b","x:y","CON.txt","a.","a ","","..","LPT9","a\0b"})Assert.False(FileProtocol.ValidName(name));
        Assert.True(FileProtocol.ValidName("测试文件.bin"));
        Assert.Throws<InvalidDataException>(()=>FileProtocol.Validate(new(Guid.NewGuid().ToString(),"drop",[new("A",0),new("a",0)])));
    }
    [Fact]public void SharedFixtureStreamsAndPublishesAtomically()
    {
        var f=Sample();var data=Convert.FromBase64String(f.Data);var root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString());
        try
        {
            using var inbox=new FileInbox(f.Offer,root);inbox.Begin(0);
            Assert.Equal(Convert.FromBase64String(f.FirstChunk),FileProtocol.Chunk(0,0,data[..FileProtocol.ChunkSize]));
            for(var offset=0;offset<data.Length;offset+=FileProtocol.ChunkSize)inbox.Append(0,offset,data[offset..Math.Min(data.Length,offset+FileProtocol.ChunkSize)]);
            Assert.All(Directory.GetDirectories(root),p=>Assert.StartsWith(".partial-",Path.GetFileName(p)));
            inbox.End(0,f.Sha256);var files=inbox.Complete();Assert.Equal(data,File.ReadAllBytes(files[0]));Assert.Single(Directory.GetDirectories(root));
        }finally{Directory.Delete(root,true);}
    }
    [Fact]public void CancellationAndReorderingNeverPublish()
    {
        var root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString());
        try
        {
            using(var inbox=new FileInbox(Sample().Offer,root))
            {inbox.Begin(0);Assert.Throws<InvalidDataException>(()=>inbox.Append(0,1,[1]));Assert.Throws<InvalidDataException>(()=>inbox.End(0,"bad"));Assert.Throws<InvalidDataException>(()=>inbox.Complete());}
            Assert.Empty(Directory.GetFileSystemEntries(root));
        }finally{Directory.Delete(root,true);}
    }
}
