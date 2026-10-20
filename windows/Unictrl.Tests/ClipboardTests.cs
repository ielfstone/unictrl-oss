using System.Text.Json;
using Xunit;
namespace Unictrl.Tests;
public class ClipboardTests
{
    private const string Id = "11111111-2222-3333-4444-555555555555";
    private static List<ClipboardEffects> Send(IEnumerable<ClipboardPacket> packets, ClipboardReplica to) => packets.Select(to.Receive).ToList();
    [Fact] public void MacToWindowsUnicode()
    {
        var mac = new ClipboardReplica(true); var pc = new ClipboardReplica(false);
        const string text = "中文 🧩\nline 2\r\ntab\t";
        Assert.Equal(new[] { text }, Send(mac.Copy(text), pc).Where(e => e.Text != null).Select(e => e.Text));
    }
    [Fact] public void WindowsToMacAndAcknowledgement()
    {
        var mac = new ClipboardReplica(true); var pc = new ClipboardReplica(false);
        var effects = Send(pc.Copy("PC → Mac", Id), mac);
        Assert.Equal("PC → Mac", effects.Last().Text);
        Send(effects.SelectMany(e => e.Packets), pc);
        Assert.True(pc.IsCurrent(Id));
    }
    [Fact] public void SimultaneousCopiesConverge()
    {
        var mac = new ClipboardReplica(true); var pc = new ClipboardReplica(false);
        var older = mac.Copy("Mac"); var newer = pc.Copy("PC", Id);
        Assert.All(Send(older, pc), e => Assert.Null(e.Text));
        var effects = Send(newer, mac); Send(effects.SelectMany(e => e.Packets), pc);
        Assert.Equal("PC", effects.Last().Text); Assert.Equal(mac.LatestId, pc.LatestId);
    }
    [Fact] public void OldTransferCannotOverwriteNewCopy()
    {
        var mac = new ClipboardReplica(true); var pc = new ClipboardReplica(false);
        var old = mac.Copy("old"); Send(old.SkipLast(1), pc); pc.Copy("new");
        Assert.Null(pc.Receive(old.Last()).Text);
    }
    [Fact] public void UnsupportedCopyInvalidatesPendingWrite()
    {
        var mac = new ClipboardReplica(true); var pc = new ClipboardReplica(false);
        Send(mac.Copy("old", Id), pc); Assert.True(pc.IsCurrent(Id)); pc.InvalidateLocal(); Assert.False(pc.IsCurrent(Id));
    }
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void TruncatedCorruptedAndReorderedDataNeverApply(int kind)
    {
        var mac = new ClipboardReplica(true); var pc = new ClipboardReplica(false);
        var packets = mac.Copy("complete only");
        if (kind == 0) packets.RemoveAt(2);
        if (kind == 1) packets[2] = packets[2] with { Data = Convert.ToBase64String("tampered"u8.ToArray()) };
        if (kind == 2) packets[2] = packets[2] with { Offset = 99 };
        Assert.All(Send(packets, pc), e => Assert.Null(e.Text));
    }
    [Fact] public void LargeTextIsChunked()
    {
        var text = new string('中', 100_000); var mac = new ClipboardReplica(true); var pc = new ClipboardReplica(false);
        var packets = mac.Copy(text); Assert.True(packets.Count > 3);
        Assert.All(packets, p => Assert.True(ClipboardReplica.Encode(p).Length < 16368));
        Assert.Equal(text, Send(packets, pc).Last().Text);
    }
    [Fact] public void RejectsLimitsAndNul()
    {
        var mac = new ClipboardReplica(true);
        Assert.Empty(mac.Copy("")); Assert.Empty(mac.Copy("a\0b")); Assert.Empty(mac.Copy(new string('a', ClipboardReplica.MaxBytes + 1)));
    }
    [Fact] public void RemoteWriteDoesNotEcho()
    {
        var mac = new ClipboardReplica(true); var pc = new ClipboardReplica(false); var packets = mac.Copy("once");
        Assert.All(Send(packets, pc), e => Assert.Empty(e.Packets)); Assert.Null(pc.Receive(packets.Last()).Text);
    }
    [Fact] public void SharedCrossLanguageFixture()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "clipboard", "text-v1.json")));
        var packets = doc.RootElement.GetProperty("packets").EnumerateArray().Select(p => ClipboardReplica.Parse([0x33, .. System.Text.Encoding.UTF8.GetBytes(p.GetRawText())])!).ToList();
        Assert.All(packets, Assert.NotNull);
        var text = doc.RootElement.GetProperty("text").GetString()!;
        Assert.Equal(text, Send(packets, new ClipboardReplica(false)).Last().Text);
        var actual = new ClipboardReplica(true).Copy(text, Id);
        Assert.Equal(packets, actual);
    }
}
