using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
namespace Unictrl;

/// Dedicated STA window. Neither a busy clipboard owner nor conversion blocks input/UI threads.
internal sealed record ClipboardContent(string? Text = null, byte[]? Png = null);

internal sealed class DesktopClipboard : IDisposable
{
    private const uint Changed = 0x031D, Work = 0x8021, TimerMessage = 0x0113;
    private readonly ConcurrentQueue<Action> jobs = new();
    private readonly Action<ClipboardContent?> copied;
    private readonly Action<string> status;
    private Native.WndProc? proc;
    private nint window;
    private bool active, disposed;
    private uint seen;
    private int retries;
    private uint privateFormat, pngFormat;
    private bool imagesAllowed;
    public DesktopClipboard(Action<ClipboardContent?> copied, Action<string> status)
    {
        this.copied = copied; this.status = status;
        var thread = new Thread(Run) { IsBackground = true, Name = "Unictrl clipboard STA" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
    }
    public void SetActive(bool value, Action? initialized = null) => Post(() => { active = value; seen = GetClipboardSequenceNumber(); retries = 0; initialized?.Invoke(); });
    public void SetImagesAllowed(bool value) => Post(() => imagesAllowed = value);
    public void Write(string text, Func<bool> stillCurrent, Action? completed = null) => Write(new ClipboardContent(Text: text), stillCurrent, completed);
    public void Write(ClipboardContent content, Func<bool> stillCurrent, Action? completed = null) => Post(() =>
    {
        if (!active || !stillCurrent()) return;
        try
        {
            List<(uint Format, byte[] Bytes)> formats;
            if (content.Png is { } png)
            {
                if (!imagesAllowed) return;
                using var image = ClipboardImage.Decode(png);
                formats = [(pngFormat, png), (17, ClipboardImage.ToDib(image, true)), (8, ClipboardImage.ToDib(image, false))];
            }
            else if (content.Text is { } text) formats = [(13, Encoding.Unicode.GetBytes(text + '\0'))];
            else return;
            WriteNow(formats, stillCurrent, 0, completed);
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or InvalidDataException or OutOfMemoryException)
        { status("Image is invalid or exceeds limits"); }
    });
    private void Post(Action action)
    {
        if (disposed) return;
        jobs.Enqueue(action); var hwnd = window;
        if (hwnd != 0) Native.PostMessageW(hwnd, Work, 0, 0);
    }
    private void Run()
    {
        proc = WindowProc;
        var name = "UnictrlClipboard" + Guid.NewGuid().ToString("N");
        var wc = new Native.WndClass { ClassName = name, WndProc = Marshal.GetFunctionPointerForDelegate(proc) };
        Native.RegisterClassW(ref wc);
        window = Native.CreateWindowExW(0, name, "", 0, 0, 0, 0, 0, new nint(-3), 0, 0, 0);
        if (window == 0) { status("Clipboard window unavailable"); return; }
        pngFormat = RegisterClipboardFormatW("PNG");
        privateFormat = RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing");
        seen = GetClipboardSequenceNumber();
        if (!AddClipboardFormatListener(window)) { status("Clipboard listener unavailable"); return; }
        Native.PostMessageW(window, Work, 0, 0);
        while (Native.GetMessageW(out var msg, 0, 0, 0) > 0) { Native.TranslateMessage(ref msg); Native.DispatchMessageW(ref msg); }
        RemoveClipboardFormatListener(window); DestroyWindow(window); window = 0;
    }
    private nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message == Changed || message == TimerMessage) { Poll(); return 0; }
        if (message == Work) { while (jobs.TryDequeue(out var work)) work(); return 0; }
        return Native.DefWindowProcW(hwnd, message, wParam, lParam);
    }
    private void Poll()
    {
        KillTimer(window, 1);
        var sequence = GetClipboardSequenceNumber();
        if (!active || sequence == seen) { seen = sequence; return; }
        if (!OpenClipboard(window))
        {
            if (++retries <= 10) SetTimer(window, 1, 50, 0);
            else { seen = sequence; retries = 0; copied(null); status("Clipboard busy; copy again"); }
            return;
        }
        ClipboardContent? content = null;
        byte[]? imageBytes = null; uint imageFormat = 0;
        try
        {
            if (!IsClipboardFormatAvailable(15) && !IsClipboardFormatAvailable(privateFormat))
            {
                // Images take precedence over a browser's text/URL fallback, even when image sync is off.
                imageFormat = IsClipboardFormatAvailable(pngFormat) ? pngFormat : IsClipboardFormatAvailable(17) ? 17u : IsClipboardFormatAvailable(8) ? 8u : 0;
                if (imageFormat != 0)
                {
                    if (imagesAllowed) imageBytes = ReadGlobal(imageFormat, imageFormat == pngFormat ? ClipboardReplica.MaxImageBytes + 64 : 100_000_000);
                }
                else if (IsClipboardFormatAvailable(13) && ReadGlobal(13, 2 * ClipboardReplica.MaxBytes + 64) is { } bytes)
                {
                    var length = 0;
                    while (length + 1 < bytes.Length && (bytes[length] != 0 || bytes[length + 1] != 0)) length += 2;
                    if (length + 1 < bytes.Length)
                        try { content = new(Text: new UnicodeEncoding(false, false, true).GetString(bytes, 0, length)); }
                        catch (DecoderFallbackException) { }
                }
            }
        }
        finally { CloseClipboard(); }
        if (imageBytes is not null)
        {
            try
            {
                byte[] png;
                if (imageFormat == pngFormat) { using var image = ClipboardImage.Decode(imageBytes); png = ClipboardImage.Encode(image); }
                else png = ClipboardImage.FromDib(imageBytes);
                content = new(Png: png);
            }
            catch (Exception ex) when (ex is ArgumentException or ExternalException or InvalidDataException or OutOfMemoryException)
            { status("Image is invalid, unsupported or exceeds 10 MiB / 20 MP"); }
        }
        else if (imageFormat != 0 && imagesAllowed) status("Image unavailable or exceeds limits");
        retries = 0;
        if (GetClipboardSequenceNumber() != sequence) { SetTimer(window, 1, 30, 0); return; }
        seen = sequence; copied(content);
    }
    private static byte[]? ReadGlobal(uint format, int max)
    {
        var handle = GetClipboardData(format); var size = (ulong)GlobalSize(handle);
        if (handle == 0 || size < 2 || size > (ulong)max) return null;
        var pointer = GlobalLock(handle); if (pointer == 0) return null;
        try { var bytes = new byte[(int)size]; Marshal.Copy(pointer, bytes, 0, bytes.Length); return bytes; }
        finally { GlobalUnlock(handle); }
    }
    private void WriteNow(List<(uint Format, byte[] Bytes)> formats, Func<bool> stillCurrent, int attempt, Action? completed)
    {
        Poll(); // Publish/cancel any newer local copy before considering a remote write.
        if (!active || !stillCurrent()) return;
        if (!OpenClipboard(window))
        {
            if (attempt >= 10) { status("Clipboard busy; remote content not applied"); return; }
            _ = Task.Delay(50).ContinueWith(_ => Post(() => WriteNow(formats, stillCurrent, attempt + 1, completed)));
            return;
        }
        // Allocate every format before EmptyClipboard so allocation failure preserves the old contents.
        List<(uint Format, nint Memory)> allocated = [];
        var applied = false;
        try
        {
            foreach (var format in formats)
            {
                var memory = GlobalAlloc(0x0042, (nuint)format.Bytes.Length);
                if (memory == 0) { status("Clipboard allocation failed"); return; }
                allocated.Add((format.Format, memory));
                var pointer = GlobalLock(memory); if (pointer == 0) return;
                Marshal.Copy(format.Bytes, 0, pointer, format.Bytes.Length); GlobalUnlock(memory);
            }
            if (!stillCurrent() || GetClipboardSequenceNumber() != seen) return;
            if (!EmptyClipboard()) { status("Clipboard write failed"); return; }
            for (var i = 0; i < allocated.Count; i++)
            {
                var format = allocated[i];
                if (SetClipboardData(format.Format, format.Memory) == 0) { status("Clipboard write failed"); return; }
                allocated[i] = (format.Format, 0); // OS owns successfully published memory.
            }
            applied = true;
        }
        finally
        {
            foreach (var format in allocated) if (format.Memory != 0) GlobalFree(format.Memory);
            CloseClipboard();
            seen = GetClipboardSequenceNumber();
            if (applied) completed?.Invoke();
        }
    }
    public void Dispose() { disposed = true; active = false; if (window != 0) Native.PostMessageW(window, 0x0012, 0, 0); }
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool AddClipboardFormatListener(nint hwnd);
    [DllImport("user32.dll")] private static extern bool RemoveClipboardFormatListener(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern bool OpenClipboard(nint hwnd);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] private static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll")] private static extern nint SetClipboardData(uint format, nint memory);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormatW(string format);
    [DllImport("kernel32.dll")] private static extern nint GlobalAlloc(uint flags, nuint size);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll")] private static extern nuint GlobalSize(nint memory);
    [DllImport("kernel32.dll")] private static extern nint GlobalFree(nint memory);
    [DllImport("user32.dll")] private static extern nuint SetTimer(nint hwnd, nuint id, uint milliseconds, nint callback);
    [DllImport("user32.dll")] private static extern bool KillTimer(nint hwnd, nuint id);
}
