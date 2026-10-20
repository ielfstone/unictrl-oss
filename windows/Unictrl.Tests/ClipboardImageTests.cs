using System.Text.Json;
using Xunit;
namespace Unictrl.Tests;
public class ClipboardImageTests
{
    private static (byte[] Png, List<ClipboardPacket> Packets) Fixture()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "clipboard", "image-v2.json")));
        return (Convert.FromBase64String(doc.RootElement.GetProperty("png").GetString()!),
            doc.RootElement.GetProperty("packets").EnumerateArray().Select(p => ClipboardReplica.Parse([0x33, .. System.Text.Encoding.UTF8.GetBytes(p.GetRawText())])!).ToList());
    }
    [Fact] public void SharedImageFixtureAndBidirectionalTransfer()
    {
        var (png, packets) = Fixture(); var pc = new ClipboardReplica(false) { ImagesAllowed = true }; var mac = new ClipboardReplica(true) { ImagesAllowed = true };
        Assert.Equal(png, packets.Select(pc.Receive).Last().Png);
        Assert.Equal(png, pc.CopyImage(png).Select(mac.Receive).Last().Png);
        Assert.All(mac.CopyImage(png), p => Assert.True(ClipboardReplica.Encode(p).Length < 16368));
    }
    [Fact] public void ImagesRequireOptInAndTextStillWorks()
    {
        var (png, packets) = Fixture(); var pc = new ClipboardReplica(false);
        Assert.All(packets.Select(pc.Receive), e => Assert.Null(e.Png)); Assert.Empty(pc.CopyImage(png));
        Assert.Equal("text", new ClipboardReplica(true).Copy("text").Select(new ClipboardReplica(false).Receive).Last().Text);
    }
    [Fact] public void CorruptImageAndNewerLocalTextNeverApply()
    {
        var (_, packets) = Fixture(); var pc = new ClipboardReplica(false) { ImagesAllowed = true };
        var changed = packets.ToList(); changed[2] = changed[2] with { Data = Convert.ToBase64String(new byte[77]) };
        Assert.All(changed.Select(pc.Receive), e => Assert.Null(e.Png));
        foreach (var packet in packets.SkipLast(1)) pc.Receive(packet);
        pc.Copy("new local text"); Assert.Null(pc.Receive(packets.Last()).Png);
    }
    [Fact] public void LimitsAndUnknownMime()
    {
        var (png, packets) = Fixture(); new byte[] {0,0,39,16,0,0,39,16}.CopyTo(png, 16);
        Assert.Null(ClipboardReplica.PngSize(png)); Assert.False(ClipboardReplica.ValidSize(0, 4));
        Assert.Null(ClipboardReplica.PngSize(new byte[ClipboardReplica.MaxImageBytes + 1]));
        packets[1] = packets[1] with { Mime = "image/svg+xml" }; var pc = new ClipboardReplica(false) { ImagesAllowed = true };
        Assert.All(packets.Select(pc.Receive), e => Assert.Null(e.Png));
    }
}
