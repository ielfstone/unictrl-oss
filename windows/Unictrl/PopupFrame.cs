using System.Runtime.InteropServices;
using Windows.Graphics;
namespace Unictrl;

/// Own the HWND frame as well as the XAML content. A rounded Border alone cannot remove the
/// Win32 non-client frame or its invisible resize margins.
internal sealed class PopupFrame
{
    private readonly nint hwnd;
    public PopupFrame(nint hwnd)
    {
        this.hwnd = hwnd;
        var style = GetWindowLongPtrW(hwnd, -16).ToInt64();
        SetWindowLongPtrW(hwnd, -16, new((style & ~0x00CF0000L) | 0x80000000L)); // WS_POPUP, no overlapped frame
        var extended = GetWindowLongPtrW(hwnd, -20).ToInt64();
        SetWindowLongPtrW(hwnd, -20, new((extended & ~0x00020301L) | 0x80L)); // tool window, no edge styles
        SetWindowPos(hwnd, 0, 0, 0, 0, 0, 0x37); // FRAMECHANGED, no move/size/activation
        var enabled = 2; DwmSetWindowAttribute(hwnd, 2, ref enabled, 4); // DWMNCRP_ENABLED
        var rounded = 2; DwmSetWindowAttribute(hwnd, 33, ref rounded, 4); // DWMWCP_ROUND, Windows 11
        var noBorder = unchecked((int)0xFFFFFFFE); DwmSetWindowAttribute(hwnd, 34, ref noBorder, 4);
    }
    public Native.Rect32 WindowBounds { get { GetWindowRect(hwnd, out var value); return value; } }
    public Native.Rect32 ClientBounds { get { GetClientRect(hwnd, out var value); return value; } }
    public Native.Rect32 VisibleBounds => DwmGetWindowAttribute(hwnd, 9, out Native.Rect32 value, Marshal.SizeOf<Native.Rect32>()) == 0 ? value : WindowBounds;
    public long Style => GetWindowLongPtrW(hwnd, -16).ToInt64();
    public int CornerPreference => DwmGetWindowAttributeInt(hwnd, 33, out var value, 4) == 0 ? value : -1;
    public void SetDark(bool dark) { var value = dark ? 1 : 0; DwmSetWindowAttribute(hwnd, 20, ref value, 4); }
    public void Align(RectInt32 workArea, int rightMargin)
    {
        var outer = WindowBounds; var visible = VisibleBounds;
        var x = outer.Left + Math.Max(workArea.X, workArea.X + workArea.Width - rightMargin - (visible.Right - visible.Left)) - visible.Left;
        var y = outer.Top + Math.Max(workArea.Y, workArea.Y + workArea.Height - (visible.Bottom - visible.Top)) - visible.Top;
        SetWindowPos(hwnd, 0, x, y, 0, 0, 0x15); // align visible edge with work area, not invisible resize bounds
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Native.Rect32 rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out Native.Rect32 rect);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out Native.Rect32 value, int size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static extern int DwmGetWindowAttributeInt(nint hwnd, int attribute, out int value, int size);
}
