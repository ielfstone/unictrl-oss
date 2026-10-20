using System.Text;
using System.Text.Json;
using Xunit;
namespace Unictrl.Tests;
public class UpdateTests
{
    private static byte[] Fixture() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "updates", "signed-v1.json"));
    [Fact] public void SignedManifestAndVersionOrdering()
    {
        Assert.Equal("0.4.0", UpdateProtocol.Parse(Fixture()).Version);
        Assert.True(UpdateProtocol.VersionOf("0.4.0") > UpdateProtocol.VersionOf("0.3.0"));
        Assert.Null(UpdateProtocol.VersionOf("0.4.1-beta")); Assert.Null(UpdateProtocol.VersionOf("1.2"));
    }
    [Fact] public void TamperedManifestIsRejected()
    {
        using var json = JsonDocument.Parse(Fixture());
        var text = Encoding.UTF8.GetString(Convert.FromBase64String(json.RootElement.GetProperty("payload").GetString()!)).Replace("0.4.0", "9.9.9");
        var tampered = JsonSerializer.SerializeToUtf8Bytes(new {payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)), signature = json.RootElement.GetProperty("signature").GetString()});
        Assert.Throws<InvalidDataException>(() => UpdateProtocol.Parse(tampered));
    }
    [Fact] public void CorruptedPackageNeverVerifies()
    {
        var path = Path.GetTempFileName();
        try { File.WriteAllText(path, "bad"); Assert.Throws<InvalidDataException>(() => UpdateProtocol.Verify(path, new("", new string('0', 64), 3))); }
        finally { File.Delete(path); }
    }
}
