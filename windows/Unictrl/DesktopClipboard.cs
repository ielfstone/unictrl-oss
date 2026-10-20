using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
namespace Unictrl;

/// Dedicated STA window. Neither a busy clipboard owner nor conversion blocks input/UI threads.
internal sealed class DesktopClipboard : IDisposable
{
    private const uint Changed = 0x031D, Work = 0x8021, TimerMessage = 0x0113;
    private readonly ConcurrentQueue<Action> jobs = new();
    private readonly Action<string?> copied;
    private readonly Action<string> status;
    private Native.WndProc? proc;
    private nint window;
    private bool active, disposed;
    private uint seen;
    private int retries;
    private uint privateFormat;
    public DesktopClipboard(Action<string?> copied, Action<string> status)
    {
        this.copied = copied; this.status = status;
        var thread = new Thread(Run) { IsBackground = true, Name = "Unictrl clipboard STA" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
    }
    public void SetActive(bool value, Action? initialized = null) => Post(() => { active = value; seen = GetClipboardSequenceNumber(); retries = 0; initialized?.Invoke(); });
    public void Write(string text, Func<bool> stillCurrent) => Post(() => WriteNow(text, stillCurrent, 0));
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
        string? text = null;
        try
        {
            if (IsClipboardFormatAvailable(13) && !IsClipboardFormatAvailable(15) && !IsClipboardFormatAvailable(privateFormat))
            {
                var handle = GetClipboardData(13);
                var size = (ulong)GlobalSize(handle);
                if (handle != 0 && size >= 2 && size <= 2UL * ClipboardReplica.MaxBytes + 64)
                {
                    var pointer = GlobalLock(handle);
                    if (pointer != 0)
                    {
                        try
                        {
                            var bytes = new byte[(int)size]; Marshal.Copy(pointer, bytes, 0, bytes.Length);
                            var length = 0;
                            while (length + 1 < bytes.Length && (bytes[length] != 0 || bytes[length + 1] != 0)) length += 2;
                            if (length + 1 < bytes.Length) text = new UnicodeEncoding(false, false, true).GetString(bytes, 0, length);
                        }
                        catch (DecoderFallbackException) { }
                        finally { GlobalUnlock(handle); }
                    }
                }
            }
        }
        finally { CloseClipboard(); }
        retries = 0;
        if (GetClipboardSequenceNumber() != sequence) { SetTimer(window, 1, 30, 0); return; }
        seen = sequence; copied(text);
    }
    private void WriteNow(string text, Func<bool> stillCurrent, int attempt)
    {
        Poll(); // Publish/cancel any newer local copy before considering a remote write.
        if (!active || !stillCurrent()) return;
        if (!OpenClipboard(window))
        {
            if (attempt >= 10) { status("Clipboard busy; remote text not applied"); return; }
            _ = Task.Delay(50).ContinueWith(_ => Post(() => WriteNow(text, stillCurrent, attempt + 1)));
            return;
        }
        nint memory = 0;
        try
        {
            if (!stillCurrent() || GetClipboardSequenceNumber() != seen) return;
            var bytes = Encoding.Unicode.GetBytes(text + '\0');
            memory = GlobalAlloc(0x0042, (nuint)bytes.Length);
            if (memory == 0) { status("Clipboard allocation failed"); return; }
            var pointer = GlobalLock(memory);
            if (pointer == 0) return;
            Marshal.Copy(bytes, 0, pointer, bytes.Length); GlobalUnlock(memory);
            if (!EmptyClipboard() || SetClipboardData(13, memory) == 0) { status("Clipboard write failed"); return; }
            memory = 0; seen = GetClipboardSequenceNumber(); // OS owns memory; our write is not a local copy.
        }
        finally { if (memory != 0) GlobalFree(memory); CloseClipboard(); }
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
