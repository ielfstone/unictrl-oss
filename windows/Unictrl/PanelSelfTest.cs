using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
namespace Unictrl;

/// Runs on the isolated CI desktop with synthetic state, without pairing or real input capture.
internal static class PanelSelfTest
{
    private static QuickPanel? panel;
    private static DesktopClipboard? desktop;
    private static WindowsFileTransfer? files;
    private static DispatcherTimer? timer;
    public static void Start()
    {
        try
        {
            desktop = new DesktopClipboard(_ => { }, _ => { });
            var settings = new Settings { ClipboardEnabled = true, ClipboardFilesEnabled = true };
            files = new WindowsFileTransfer(settings, desktop, _ => { });
            panel = new QuickPanel(settings, files, () => { }, _ => { }, () => { }, () => { });
            panel.UpdateConnection("Connected · TEST-MAC");
            panel.Update(new("Receiving files · 42%", "2 files · 640 MiB · presentation.zip, video.mp4", true, .42));
            panel.Show();
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += Verify; timer.Start();
        }
        catch (Exception error) { Finish(error); }
    }
    private static async void Verify(object? sender, object e)
    {
        timer!.Stop();
        try
        {
            var root = (FrameworkElement)panel!.Content; root.UpdateLayout();
            var controls = Walk(root).ToArray();
            Check(controls.OfType<Border>().Any(b => b.AllowDrop), "drop target");
            Check(controls.OfType<Button>().Any(b => Equals(b.Content, "Cancel") && b.Visibility == Visibility.Visible), "cancel visible");
            Check(controls.OfType<ProgressBar>().Any(p => p.Value == 42), "progress state");
            Check(!panel.AppWindow.IsShownInSwitchers, "tray panel stays out of taskbar");
            Check(root.ActualWidth >= 300 && root.ActualHeight > 300, "panel laid out");
            var image = new RenderTargetBitmap(); await image.RenderAsync(root);
            var pixels = await image.GetPixelsAsync();
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)image.PixelWidth, (uint)image.PixelHeight, 96, 96, pixels.ToArray()); await encoder.FlushAsync();
            stream.Seek(0); using var input = stream.AsStreamForRead(); using var output = File.Create(Path.Combine(Settings.Directory, "panel-test.png")); await input.CopyToAsync(output);
            panel.Update(new("Files offered", "1 file · report.pdf"));
            Check(controls.OfType<Button>().Any(b => Equals(b.Content, "Receive & save…") && b.IsEnabled), "receive actionable");
            panel.Toggle(); Check(!panel.AppWindow.IsVisible, "tray toggle hides panel");
            panel.Show(); Check(panel.AppWindow.IsVisible, "tray opens panel");
            Log.Write("panel_test", "passed tray_toggle receive progress cancel drop_target layout"); Finish(null);
        }
        catch (Exception error) { Finish(error); }
    }
    private static IEnumerable<DependencyObject> Walk(DependencyObject parent)
    {
        yield return parent;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) foreach (var child in Walk(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); }
    private static void Finish(Exception? error)
    {
        if (error is not null) { Environment.ExitCode = 1; Log.Write("panel_test_failed", error.GetType().Name + ": " + error.Message); }
        panel?.ClosePanel(); files?.Dispose(); desktop?.Dispose(); Application.Current.Exit();
    }
}
