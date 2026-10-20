using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
namespace Unictrl;

/// Clipboard transport has independent encryption counters and socket; keyboard traffic never waits.
internal sealed class ClipboardSync : IDisposable
{
    private readonly Settings settings;
    private readonly Action<string> status;
    private readonly DesktopClipboard desktop;
    private readonly object gate = new();
    private readonly SemaphoreSlim signal = new(0);
    private Queue<ClipboardPacket> output = new();
    private ClipboardReplica replica = new(false);
    private object? owner;
    private Action<byte[]>? sendControl;
    private bool remoteEnabled, ready, disposed;
    private int generation;
    private TcpClient? client;
    private CancellationTokenSource? transfer;
    private DateTime incomingAt;
    public ClipboardSync(Settings settings, Action<string> status)
    {
        this.settings = settings; this.status = status;
        desktop = new DesktopClipboard(LocalCopy, status);
    }
    private bool Enabled => settings.ClipboardEnabled && settings.Enabled && !disposed;
    public void Attach(object sessionOwner, Action<byte[]> send)
    {
        lock (gate) { Stop(); owner = sessionOwner; sendControl = send; remoteEnabled = false; }
        Announce();
    }
    public void Detach(object sessionOwner)
    {
        lock (gate)
        {
            if (!ReferenceEquals(owner, sessionOwner)) return;
            Stop(); owner = null; sendControl = null; remoteEnabled = false;
        }
        status(Enabled ? "Waiting for Mac" : "Off");
    }
    public void Refresh()
    {
        lock (gate) Stop();
        Announce();
    }
    private void Announce()
    {
        Action<byte[]>? send;
        lock (gate) send = sendControl;
        if (send is not null) _ = Task.Run(() => { try { send([(byte)MessageType.ClipboardCapabilities, 1, Enabled ? (byte)1 : (byte)0]); } catch { } });
        status(Enabled ? "Waiting for Mac to enable text clipboard" : "Off");
    }
    public void Control(object sessionOwner, MessageType type, byte[] data)
    {
        lock (gate)
        {
            if (!ReferenceEquals(owner, sessionOwner)) return;
            if (type == MessageType.ClipboardCapabilities && data.Length == 2 && data[0] == 1 && data[1] <= 1)
            {
                remoteEnabled = data[1] == 1;
                if (!remoteEnabled) { Stop(); status(Enabled ? "Enable text clipboard on Mac" : "Off"); }
            }
            else if (type == MessageType.ClipboardTicket && data.Length == 16 && Enabled && remoteEnabled)
            {
                Stop(); var current = generation; var token = transfer = new CancellationTokenSource();
                var key = settings.PairingKey; var address = settings.MacAddress; var port = settings.Port;
                var id = settings.DeviceIdBytes;
                _ = Task.Run(() => Run(data, key, address, port, id, current, token.Token));
            }
        }
    }
    private void Stop()
    {
        generation++; ready = false; transfer?.Cancel(); transfer?.Dispose(); transfer = null;
        client?.Dispose(); client = null; desktop.SetActive(false);
        output.Clear(); replica = new(false);
    }
    private void Run(byte[] ticket, string key, string address, int port, byte[] id, int current, CancellationToken cancel)
    {
        try
        {
            using var socket = new TcpClient { NoDelay = true };
            lock (gate) { if (current != generation) return; client = socket; }
            socket.ConnectAsync(address, port, cancel).AsTask().Wait(cancel);
            var stream = socket.GetStream(); stream.ReadTimeout = 5000; stream.WriteTimeout = 5000;
            var salt = RandomNumberGenerator.GetBytes(32);
            stream.Write([.. "UNICTRC1"u8.ToArray(), .. salt, .. id, .. ticket]);
            var reply = new byte[40]; stream.ReadExactly(reply);
            if (!reply.AsSpan(0, 8).SequenceEqual("CLIPOK01"u8)) throw new InvalidDataException("Clipboard handshake");
            using var channel = new SecureChannel(key, salt, reply[8..], false, ClipboardReplica.Context);
            var first = ClipboardReplica.Parse(Read(stream, channel));
            if (first?.Kind != "ready") throw new InvalidDataException("Clipboard authentication");
            stream.Write(channel.Seal(ClipboardReplica.Encode(new("ready"))));
            stream.ReadTimeout = Timeout.Infinite;
            lock (gate)
            {
                if (current != generation || !Enabled) return;
                ready = true; desktop.SetActive(true, () => { lock (gate) { if (current == generation && ready) status("Ready · text only"); } });
            }
            var writer = Task.Run(() => WriteLoop(stream, channel, current, cancel), cancel);
            while (!cancel.IsCancellationRequested)
            {
                var packet = ClipboardReplica.Parse(Read(stream, channel));
                if (packet is null) throw new InvalidDataException("Clipboard record");
                lock (gate)
                {
                    if (current != generation || !ready) break;
                    if (DateTime.UtcNow - incomingAt > TimeSpan.FromSeconds(10)) replica.CancelIncoming();
                    if (packet.Kind == "begin") incomingAt = DateTime.UtcNow;
                    if (packet.Kind == "ack" && packet.Id == replica.LocalId) { status("Ready · text only"); continue; }
                    var effects = replica.Receive(packet);
                    Enqueue(effects.Packets);
                    if (effects.Text is { } text && effects.Id is { } copyId)
                        desktop.Write(text, () => { lock (gate) return current == generation && ready && replica.IsCurrent(copyId); },
                            () => { lock (gate) { if (current == generation && ready) { Enqueue([new("ack", copyId)]); status("Ready · text only"); } } });
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or InvalidDataException or CryptographicException or AggregateException)
        { /* A clipboard error does not end the keyboard session. Never log clipboard contents. */ }
        finally
        {
            bool retry;
            lock (gate)
            {
                retry = current == generation && Enabled && remoteEnabled && owner is not null;
                if (current == generation) Stop();
            }
            if (retry) { status("Clipboard reconnecting"); _ = Task.Delay(1000).ContinueWith(_ => Announce()); }
        }
    }
    private static byte[] Read(NetworkStream stream, SecureChannel channel)
    {
        var header = new byte[2]; stream.ReadExactly(header);
        var length = BinaryPrimitives.ReadUInt16BigEndian(header);
        if (length < 17 || length > SecureChannel.MaxFrame) throw new InvalidDataException("Clipboard frame size");
        var body = new byte[length]; stream.ReadExactly(body); return channel.Open(body);
    }
    private void WriteLoop(NetworkStream stream, SecureChannel channel, int current, CancellationToken cancel)
    {
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                signal.Wait(cancel);
                while (true)
                {
                    ClipboardPacket packet;
                    lock (gate)
                    {
                        if (current != generation || !ready) return;
                        if (!output.TryDequeue(out packet!)) break;
                    }
                    stream.Write(channel.Seal(ClipboardReplica.Encode(packet)));
                }
            }
        }
        catch { lock (gate) { if (current == generation) client?.Dispose(); } }
    }
    private void LocalCopy(string? text)
    {
        lock (gate)
        {
            if (!ready || !Enabled) return;
            replica.CancelIncoming();
            if (string.IsNullOrEmpty(text)) { replica.InvalidateLocal(); output = new Queue<ClipboardPacket>(output.Where(p => p.Kind == "order")); return; }
            var packets = replica.Copy(text);
            if (packets.Count == 0) { replica.InvalidateLocal(); status("Text exceeds 1 MiB or is unsupported"); return; }
            output = new Queue<ClipboardPacket>(output.Where(p => p.Kind == "order"));
            status("Syncing text…");
            Enqueue(packets);
        }
    }
    private void Enqueue(List<ClipboardPacket> packets)
    {
        if (output.Count + packets.Count > 512) { client?.Dispose(); return; }
        foreach (var packet in packets) output.Enqueue(packet);
        if (packets.Count != 0) signal.Release();
    }
    public void Dispose() { lock (gate) { disposed = true; Stop(); owner = null; sendControl = null; } desktop.Dispose(); }
}
