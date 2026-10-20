using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
namespace Unictrl;

/// Explicit CI diagnostic. Uses only generated text; overwrites the runner's clipboard.
internal static class ClipboardSelfTest
{
    public static void Run()
    {
        const string fromPc = "Windows → Mac：中文 🧩\nline 2\r\nTab\t";
        const string fromMac = "Mac → Windows：双向文本 ✓";
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var settings = new Settings { Enabled = true, ClipboardEnabled = true, MacAddress = "127.0.0.1",
            Port = ((IPEndPoint)listener.LocalEndpoint).Port, PairingKey = "ci-clipboard-only-not-a-real-key", DeviceId = Guid.NewGuid().ToString("D") };
        var ticket = Enumerable.Repeat((byte)4, 16).ToArray();
        using var ready = new ManualResetEventSlim();
        using var sentBack = new ManualResetEventSlim();
        var mockMac = Task.Run(() =>
        {
            using var accept = listener.AcceptTcpClient();
            using var stream = accept.GetStream(); stream.ReadTimeout = 8000; stream.WriteTimeout = 8000;
            var hello = new byte[72]; stream.ReadExactly(hello);
            Check(hello.AsSpan(0, 8).SequenceEqual("UNICTRC1"u8), "handshake magic");
            Check(hello.AsSpan(40, 16).SequenceEqual(settings.DeviceIdBytes), "bound peer id");
            Check(hello.AsSpan(56, 16).SequenceEqual(ticket), "control grant");
            var salt = Enumerable.Repeat((byte)8, 32).ToArray();
            stream.Write([.. "CLIPOK01"u8.ToArray(), .. salt]);
            using var secure = new SecureChannel(settings.PairingKey, hello[8..40], salt, true, ClipboardReplica.Context);
            stream.Write(secure.Seal(ClipboardReplica.Encode(new("ready"))));
            Check(ClipboardReplica.Parse(Read(stream, secure))?.Kind == "ready", "encrypted client proof");
            var mac = new ClipboardReplica(true);
            string? received = null;
            while (received is null)
            {
                var packet = ClipboardReplica.Parse(Read(stream, secure)) ?? throw new InvalidDataException("test packet");
                var effect = mac.Receive(packet);
                foreach (var reply in effect.Packets) stream.Write(secure.Seal(ClipboardReplica.Encode(reply)));
                received = effect.Text;
            }
            Check(received == fromPc, "Windows native copy to Mac peer");
            foreach (var packet in mac.Copy(fromMac)) stream.Write(secure.Seal(ClipboardReplica.Encode(packet)));
            sentBack.Set();
            stream.ReadTimeout = 1500;
            try { Read(stream, secure); throw new InvalidDataException("remote write echoed"); }
            catch (IOException error) when (error.InnerException is SocketException socket && socket.SocketErrorCode == SocketError.TimedOut) { }
        });
        using var clipboard = new ClipboardSync(settings, text => { if (text.StartsWith("Ready", StringComparison.Ordinal)) ready.Set(); });
        var owner = new object();
        clipboard.Attach(owner, packet =>
        {
            if (packet.Length == 3 && packet[0] == (byte)MessageType.ClipboardCapabilities && packet[2] == 1)
            {
                clipboard.Control(owner, MessageType.ClipboardCapabilities, [1, 1]);
                clipboard.Control(owner, MessageType.ClipboardTicket, ticket);
            }
        });
        Check(ready.Wait(8000), "native listener ready");
        using var editorReady = new ManualResetEventSlim();
        using var editor = new DesktopClipboard(_ => { }, _ => { });
        editor.SetActive(true, editorReady.Set);
        Check(editorReady.Wait(3000), "test clipboard writer ready");
        editor.Write(fromPc, () => true);
        Check(sentBack.Wait(8000), "bidirectional transport");
        var applied = false;
        for (var i = 0; i < 120; i++) { if (GetText() == fromMac) { applied = true; break; } Thread.Sleep(25); }
        Check(applied, "Mac peer to Windows native pasteboard");
        Check(mockMac.Wait(8000), "no echo and bounded shutdown"); mockMac.GetAwaiter().GetResult();
        clipboard.Detach(owner);
        listener.Stop();
        Log.Write("clipboard_test", "passed bidirectional_native_text authenticated_transport no_echo");
    }
    private static void Check(bool value, string what) { if (!value) throw new InvalidDataException("Clipboard diagnostic failed: " + what); }
    private static byte[] Read(NetworkStream stream, SecureChannel secure)
    {
        var prefix = new byte[2]; stream.ReadExactly(prefix);
        var size = BinaryPrimitives.ReadUInt16BigEndian(prefix);
        Check(size is >= 17 and <= 16384, "frame limit");
        var body = new byte[size]; stream.ReadExactly(body); return secure.Open(body);
    }
    private static void Open()
    {
        for (var i = 0; i < 40; i++) { if (OpenClipboard(0)) return; Thread.Sleep(25); }
        throw new IOException("test clipboard busy");
    }
    private static string? GetText()
    {
        Open();
        try
        {
            var memory = GetClipboardData(13); if (memory == 0 || (ulong)GlobalSize(memory) > 2_097_154) return null;
            var data = GlobalLock(memory); if (data == 0) return null;
            try { return Marshal.PtrToStringUni(data); } finally { GlobalUnlock(memory); }
        }
        finally { CloseClipboard(); }
    }
    [DllImport("user32.dll")] private static extern bool OpenClipboard(nint hwnd);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll")] private static extern nint SetClipboardData(uint format, nint memory);
    [DllImport("kernel32.dll")] private static extern nint GlobalAlloc(uint flags, nuint size);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll")] private static extern nint GlobalFree(nint memory);
    [DllImport("kernel32.dll")] private static extern nuint GlobalSize(nint memory);
}
