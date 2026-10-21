using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Foundation;
namespace Unictrl;

/// Actual WinUI rendering on the isolated Windows VM. All device/transfer data below is synthetic.
internal static class PanelSelfTest
{
    private static QuickPanel? panel;
    private static DesktopClipboard? desktop;
    private static WindowsFileTransfer? files;
    private static DispatcherTimer? timer;
    private static readonly string pc = "preview-pc";
    public static void Start()
    {
        try
        {
            desktop = new DesktopClipboard(_ => { }, _ => { });
            var settings = new Settings { DeviceId = pc, MacName = "MacBook Pro", ClipboardEnabled = true, ClipboardFilesEnabled = true };
            files = new WindowsFileTransfer(settings, desktop, _ => { });
            panel = new QuickPanel(settings, files, new QuickPanelActions(() => { }, _ => { }, () => { }, () => { }, _ => { }, () => { }));
            panel.PanelRoot.RequestedTheme = ElementTheme.Light;
            var layout = new ArrangementInfo(pc, [
                new("mac", "MacBook Pro", 320, 200, 0, true, 0, 0, 2),
                new(pc, "Windows PC", 520, 300, -250, true, 0, -250, 2)]);
            var quality = new QualityInfo("smooth", 3, 5, 20, null, null);
            panel.UpdatePeer(new("Connected to MacBook Pro", true, layout, quality, true));
            panel.Update(new("Files ready · copy ≤100 MiB · drag ≤1 GiB/file"));
            panel.Show();
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) }; timer.Tick += Verify; timer.Start();
        }
        catch (Exception error) { Finish(error); }
    }
    private static async void Verify(object? sender, object e)
    {
        timer!.Stop();
        try
        {
            var root = (FrameworkElement)panel!.Content; root.UpdateLayout();
            await Save(root, "panel-connected.png");
            Check(root.ActualWidth is >= 340 and <= 362 && root.ActualHeight <= 682, "compact panel dimensions");
            var controls = Walk(root).ToArray();
            Check(controls.OfType<Border>().Any(b => b.AllowDrop), "drop target");
            var information = controls.OfType<Button>().Single(b => Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(b) == "File transfer information");
            Check(ToolTipService.GetToolTip(information) is ToolTip { Content: TextBlock help } && help.Text.Contains("100 MiB") && help.Text.Contains("1 GiB") && help.Text.Contains("1 hour"), "limits available as hover help");
            Check(!controls.OfType<TextBlock>().Any(t => t.Text.Contains("cache clears after") || t.Text.Contains("recipient accepts first")), "instructions do not occupy panel space");
            Check(controls.OfType<TextBlock>().Any(t => t.Text.Contains("3 ms")), "connection latency");
            Check(controls.OfType<TextBlock>().Any(t => t.Text == "MacBook Pro") && controls.OfType<TextBlock>().Any(t => t.Text == "Windows PC"), "device rows");
            Check(!controls.OfType<TextBlock>().Any(t => t.Text is "Reverse Wheel" or "Smooth Scroll"), "infrequent wheel controls stay in settings");
            Check(panel.MiddleScroll.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled && panel.MiddleScroll.ScrollableHeight < 1, "no unnecessary scrolling in normal panel");
            VerifyNativeWindow("panel-native-desktop.png");
            Check(controls.OfType<TextBlock>().Any(t => t.Text == "Pair a Mac…"), "pair action");
            Check(!panel.AppWindow.IsShownInSwitchers, "tray panel stays out of taskbar");
            var tiles = panel.ScreenCanvas.Children.OfType<Border>().ToArray();
            Check(tiles.Length == 2 && Canvas.GetTop(tiles.Single(t => Equals(t.Tag, pc))) < Canvas.GetTop(tiles.Single(t => Equals(t.Tag, "mac"))), "Windows above Mac layout");
            AssertFooter(root);
            panel.Update(new("Files offered · Receive saves to Downloads", "2 files · 640 MiB · presentation.zip, video.mp4"));
            panel.Show(); await Task.Delay(250); root.UpdateLayout();
            Check(Walk(root).OfType<Button>().Any(b => Equals(b.Content, "Receive to Downloads") && b.IsEnabled), "receive actionable");
            AssertFooter(root); await Save(root, "panel-incoming.png");
            Check(panel.MiddleScroll.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled && panel.MiddleScroll.ScrollableHeight < 1, "incoming panel fits without scrollbar");
            VerifyNativeWindow("panel-native-incoming-desktop.png");
            panel.Update(new("Ready for native paste · or save to Downloads", "2 files · 64 MiB · report.pdf, photo.png"));
            panel.Show(); await Task.Delay(250); root.UpdateLayout(); AssertFooter(root); await Save(root, "panel-paste-ready.png");
            panel.Update(new("Receiving files · 42%", "2 files · 640 MiB · presentation.zip, video.mp4", true, .42));
            panel.Show(); await Task.Delay(250); root.UpdateLayout();
            Check(Walk(root).OfType<ProgressBar>().Any(p => p.Value == 42), "progress state");
            Check(Walk(root).OfType<Button>().Any(b => Equals(b.Content, "Cancel") && b.Visibility == Visibility.Visible), "cancel visible");
            AssertFooter(root); await Save(root, "panel-test.png");
            await Save(root, "panel-transfer-150.png", 1.5);
            panel.PreviewMaxHeight = 500; panel.PanelRoot.RequestedTheme = ElementTheme.Dark; panel.Show(); await Task.Delay(250); root.UpdateLayout();
            Check(root.ActualHeight <= 502, "small work area height");
            Check(panel.MiddleScroll.ScrollableHeight > 0, "small screen scrolls middle"); AssertFooter(root); await Save(root, "panel-small-dark.png");
            panel.Toggle(); Check(!panel.AppWindow.IsVisible, "tray toggle hides panel"); panel.Show(); Check(panel.AppWindow.IsVisible, "tray opens panel");
            // Focus must belong to the panel after opening from a tray callback. Moving focus to
            // another top-level window is the same activation path as clicking outside it.
            var outside = new Microsoft.UI.Xaml.Window { Content = new TextBlock { Text = "Outside panel" } };
            outside.Activate(); Native.SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(outside));
            await Task.Delay(200);
            panel.Show(); await Task.Delay(200);
            Check(GetForegroundWindow() == WinRT.Interop.WindowNative.GetWindowHandle(panel), "panel takes foreground");
            outside.Activate(); Native.SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(outside)); await Task.Delay(200);
            Check(!panel.AppWindow.IsVisible, "outside activation dismisses panel");
            panel.Toggle(); Check(!panel.AppWindow.IsVisible, "tray click does not reopen just-dismissed panel");
            await Task.Delay(350); panel.Toggle(); Check(panel.AppWindow.IsVisible, "later tray click reopens panel"); outside.Close();
            Log.Write("panel_test", "passed screens_above_below latency controls receive progress cancel small_screen footer 150percent_render outside_dismiss"); Finish(null);
        }
        catch (Exception error) { Finish(error); }
    }
    private static void AssertFooter(FrameworkElement root)
    {
        var quit = Walk(root).OfType<TextBlock>().Single(t => t.Text == "Quit Unictrl");
        var at = quit.TransformToVisual(root).TransformPoint(new Point(0, 0));
        Check(at.Y >= 0 && at.Y + quit.ActualHeight <= root.ActualHeight, "quit action inside window");
        Check(root.ActualHeight - at.Y - quit.ActualHeight <= 26, "no blank area below final action");
    }
    private static void VerifyNativeWindow(string screenshot)
    {
        var frame = panel!.NativeFrame; var outer = frame.WindowBounds; var client = frame.ClientBounds; var visible = frame.VisibleBounds;
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromPoint(new Windows.Graphics.PointInt32(visible.Left + 8, visible.Top + 8), Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
        Check((frame.Style & 0x00CF0000L) == 0 && (frame.Style & 0x80000000L) != 0, "native popup has no overlapped window frame");
        Check(outer.Right - outer.Left == client.Right && outer.Bottom - outer.Top == client.Bottom, "native client and outer size agree");
        Check(Math.Abs(visible.Bottom - area.WorkArea.Y - area.WorkArea.Height) <= 1, "visible panel meets taskbar work-area edge");
        Check(frame.CornerPreference is 2 or -1, "native rounded corner preference");
        var scale = panel.PanelRoot.XamlRoot.RasterizationScale;
        Check(Math.Abs(panel.PanelRoot.ActualWidth * scale - client.Right) <= 1 && Math.Abs(panel.PanelRoot.ActualHeight * scale - client.Bottom) <= 1, "XAML fits actual native client at current DPI");
        var report = new { window = new { outer.Left, outer.Top, outer.Right, outer.Bottom }, client = new { client.Right, client.Bottom }, visible = new { visible.Left, visible.Top, visible.Right, visible.Bottom }, workArea = area.WorkArea, cornerPreference = frame.CornerPreference, scale, scrollableHeight = panel.MiddleScroll.ScrollableHeight };
        File.WriteAllText(Path.Combine(Settings.Directory, "panel-native-geometry.json"), System.Text.Json.JsonSerializer.Serialize(report));
        try
        {
            var monitor = area.OuterBounds;
            var left = Math.Max(monitor.X, visible.Left - 16); var top = Math.Max(monitor.Y, visible.Top - 16);
            var right = Math.Min(monitor.X + monitor.Width, visible.Right + 16); var bottom = Math.Min(monitor.Y + monitor.Height, visible.Bottom + 56);
            using var bitmap = new System.Drawing.Bitmap(right - left, bottom - top);
            using (var drawing = System.Drawing.Graphics.FromImage(bitmap)) drawing.CopyFromScreen(left, top, 0, 0, bitmap.Size);
            bitmap.Save(Path.Combine(Settings.Directory, screenshot), System.Drawing.Imaging.ImageFormat.Png);
        }
        catch (Exception error) { Log.Write("panel_desktop_capture_unavailable", error.GetType().Name + ": " + error.Message); }
    }
    private static async Task Save(FrameworkElement root, string name, double scale = 1)
    {
        var image = new RenderTargetBitmap(); await image.RenderAsync(root, (int)Math.Round(root.ActualWidth * scale), (int)Math.Round(root.ActualHeight * scale));
        var pixels = await image.GetPixelsAsync(); using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)image.PixelWidth, (uint)image.PixelHeight, 96 * scale, 96 * scale, pixels.ToArray()); await encoder.FlushAsync();
        stream.Seek(0); using var input = stream.AsStreamForRead(); using var output = File.Create(Path.Combine(Settings.Directory, name)); await input.CopyToAsync(output);
    }
    private static IEnumerable<DependencyObject> Walk(DependencyObject parent)
    {
        yield return parent;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) foreach (var child in Walk(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); }
    private static void Finish(Exception? error)
    {
        if (error is not null) { Environment.ExitCode = 1; Log.Write("panel_test_failed", error.GetType().Name + ": " + error.Message); }
        panel?.ClosePanel(); files?.Dispose(); desktop?.Dispose(); Application.Current.Exit();
    }
}
