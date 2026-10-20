using Xunit;
namespace Unictrl.Tests;
public class TransferHistoryTests
{
    [Fact] public void MetadataIsBoundedAndCleanupPreservesUserFiles()
    {
        var root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString());
        try
        {
            var store=new TransferHistory(root);var now=DateTime.UtcNow;
            var old=Path.Combine(store.Cache,"Received-"+Guid.NewGuid());var fresh=Path.Combine(store.Cache,"Received-"+Guid.NewGuid());var user=Path.Combine(store.Cache,"my-files");
            foreach(var folder in new[]{old,fresh,user}){Directory.CreateDirectory(folder);File.WriteAllText(Path.Combine(folder,"payload"),"data");}
            Directory.SetLastWriteTimeUtc(old,now-TimeSpan.FromHours(2));
            var offer=new FileOffer(Guid.NewGuid().ToString(),"clipboard",[new("name.txt",33)]);
            for(var i=0;i<105;i++)store.Add(offer,false,"Completed");
            Assert.Equal(100,new TransferHistory(root).Records.Count);
            store.CleanCache(now:now);Assert.False(Directory.Exists(old));Assert.True(Directory.Exists(fresh));
            store.CleanCache(true);Assert.False(Directory.Exists(fresh));Assert.True(Directory.Exists(user));Assert.Equal(100,store.Records.Count);
            store.Clear();Assert.Empty(new TransferHistory(root).Records);
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
