using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
namespace Unictrl;

/// One operational surface for the tray: offers, acceptance, progress and cancellation.
internal sealed class QuickPanel : Window
{
    private readonly WindowsFileTransfer files;
    private readonly Settings settings;
    private readonly TextBlock status = Text("Files ready", 12), incoming = Text("", 12), notice = Text("", 12), connection = Text("", 12);
    private readonly ProgressBar progress = new() { Maximum = 100 };
    private readonly Button receive = new() { Content = "Receive & save…" }, cancel = new() { Content = "Cancel" }, choose = new() { Content = "Choose files…" };
    private readonly Border inbox, drop;
    private readonly ToggleSwitch enabled;
    private bool picking;
    public QuickPanel(Settings settings, WindowsFileTransfer files, Action openSettings, Action<bool> setEnabled, Action quit)
    {
        this.settings = settings; this.files = files; Title = "Unictrl";
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false); presenter.IsResizable = false;
            presenter.IsMaximizable = false; presenter.IsMinimizable = false; presenter.IsAlwaysOnTop = true;
        }
        var root = new StackPanel { Spacing = 12, Padding = new Thickness(16) };
        var header = new Grid(); header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var title = new StackPanel { Spacing = 3 }; title.Children.Add(Text("Unictrl", 18, true)); title.Children.Add(connection); header.Children.Add(title);
        enabled = new ToggleSwitch { MinWidth = 0, IsOn = settings.Enabled, OnContent = "", OffContent = "" };
        Grid.SetColumn(enabled, 1); header.Children.Add(enabled); enabled.Toggled += (_, _) => { if (settings.Enabled != enabled.IsOn) setEnabled(enabled.IsOn); };
        root.Children.Add(header);
        root.Children.Add(Text("File transfer", 15, true));
        var dropContent = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        dropContent.Children.Add(new FontIcon { Glyph = "\uE898", FontSize = 27 });
        dropContent.Children.Add(Text("Drop files to send", 14, true)); dropContent.Children.Add(choose);
        dropContent.Children.Add(Text("1 GiB/file · recipient accepts first", 11));
        drop = Card(dropContent); drop.AllowDrop = true; drop.DragOver += DragOver; drop.Drop += Drop;
        root.Children.Add(drop); choose.Click += Choose;
        var incomingContent = new StackPanel { Spacing = 8 }; incomingContent.Children.Add(Text("Incoming files", 14, true)); incomingContent.Children.Add(incoming); incomingContent.Children.Add(receive);
        inbox = Card(incomingContent); root.Children.Add(inbox); receive.Click += Receive;
        root.Children.Add(progress); root.Children.Add(status); root.Children.Add(cancel); cancel.Click += (_, _) => files.Cancel();
        root.Children.Add(notice);
        root.Children.Add(Text("Copy/paste ≤100 MiB. Temporary clipboard files clear after 1 hour. Keep the destination app active while files download.", 11));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var preferences = new Button { Content = "Settings & history" }; preferences.Click += (_, _) => { AppWindow.Hide(); openSettings(); };
        var exit = new Button { Content = "Quit" }; exit.Click += (_, _) => quit(); actions.Children.Add(preferences); actions.Children.Add(exit); root.Children.Add(actions);
        Content = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Gray), Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"], Child = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        Activated += (_, e) => { if (e.WindowActivationState == WindowActivationState.Deactivated && !picking) AppWindow.Hide(); };
        AppWindow.Closing += (_, e) => { e.Cancel = true; AppWindow.Hide(); };
        Update(new("Enable files on both computers"));
    }
    private static TextBlock Text(string value, double size, bool bold = false) => new() { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal };
    private static Border Card(UIElement child) => new() { Child = child, Padding = new Thickness(14), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.DarkSeaGreen), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(18, 40, 130, 95)) };
    public void UpdateConnection(string text) { connection.Text = text; enabled.IsOn = settings.Enabled; }
    public void Update(FileViewState state)
    {
        status.Text = state.Status; incoming.Text = state.Incoming; notice.Text = state.Notice;
        notice.Visibility = state.Notice.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        inbox.Visibility = state.Incoming.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        progress.Visibility = cancel.Visibility = state.Busy ? Visibility.Visible : Visibility.Collapsed;
        progress.Value = state.Progress * 100; receive.IsEnabled = !state.Busy; choose.IsEnabled = !state.Busy;
    }
    public void Toggle() { if (AppWindow.IsVisible) AppWindow.Hide(); else Show(); }
    public void Show()
    {
        Native.GetCursorPos(out var cursor);
        var area = DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Nearest).WorkArea;
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        var width = (int)(360 * scale); var height = Math.Min((int)(650 * scale), area.Height - 24);
        AppWindow.MoveAndResize(new RectInt32(Math.Max(area.X, area.X + area.Width - width - 12), Math.Max(area.Y, area.Y + area.Height - height - 12), width, height));
        AppWindow.Show(); Activate();
    }
    private async void Choose(object sender, RoutedEventArgs e)
    {
        picking = true;
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker(); picker.FileTypeFilter.Add("*"); WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var selected = await picker.PickMultipleFilesAsync(); if (selected.Count > 0) files.Offer(selected.Select(f => f.Path).ToArray());
        }
        catch (Exception error) { notice.Text = error.Message; notice.Visibility = Visibility.Visible; }
        finally { picking = false; Show(); }
    }
    private async void Receive(object sender, RoutedEventArgs e)
    {
        picking = true;
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker(); picker.FileTypeFilter.Add("*"); WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var selected = await picker.PickSingleFolderAsync(); if (selected is not null) files.Receive(selected.Path);
        }
        catch (Exception error) { notice.Text = error.Message; notice.Visibility = Visibility.Visible; }
        finally { picking = false; Show(); }
    }
    private void DragOver(object sender, DragEventArgs e)
    {
        if (settings.ClipboardFilesEnabled && e.DataView.Contains(StandardDataFormats.StorageItems)) { e.AcceptedOperation = DataPackageOperation.Copy; e.DragUIOverride.Caption = "Offer files to the paired computer"; }
        e.Handled = true;
    }
    private async void Drop(object sender, DragEventArgs e)
    {
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            if (items.Any(i => i is not Windows.Storage.StorageFile)) { notice.Text = "Zip folders before sending"; notice.Visibility = Visibility.Visible; return; }
            files.Offer(items.Select(i => i.Path).ToArray());
        }
        catch (Exception error) { notice.Text = error.Message; notice.Visibility = Visibility.Visible; }
    }
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
}
