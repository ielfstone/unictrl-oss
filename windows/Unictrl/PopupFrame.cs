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
    public bool HasRoundedClip
    {
        get
        {
            var region = CreateRectRgn(0, 0, 0, 0);
            try
            {
                var bounds = WindowBounds;
                return GetWindowRgn(hwnd, region) > 0 && !PtInRegion(region, 0, 0)
                    && PtInRegion(region, (bounds.Right - bounds.Left) / 2, (bounds.Bottom - bounds.Top) / 2);
            }
            finally { DeleteObject(region); }
        }
    }
    public void SetDark(bool dark) { var value = dark ? 1 : 0; DwmSetWindowAttribute(hwnd, 20, ref value, 4); }
    public void Round(double scale)
    {
        // DWM's corner preference is ignored on Windows 10 and some desktop sessions.
        // Clip the actual HWND too, so no opaque square corners surround the content.
        var bounds = WindowBounds;
        var diameter = (int)Math.Round(24 * scale);
        var region = CreateRoundRectRgn(0, 0, bounds.Right - bounds.Left + 1, bounds.Bottom - bounds.Top + 1, diameter, diameter);
        if (region == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        if (SetWindowRgn(hwnd, region, true) == 0)
        {
            var error = Marshal.GetLastWin32Error(); DeleteObject(region);
            throw new System.ComponentModel.Win32Exception(error);
        }
        // Windows owns the region after a successful SetWindowRgn call.
    }
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
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool PtInRegion(nint region, int x, int y);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(nint hwnd, nint region);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowRgn(nint hwnd, nint region, bool redraw);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out Native.Rect32 value, int size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static extern int DwmGetWindowAttributeInt(nint hwnd, int attribute, out int value, int size);
}
