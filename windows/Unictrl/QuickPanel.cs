using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Foundation;
namespace Unictrl;

internal sealed record QuickPanelActions(Action OpenSettings, Action<bool> SetEnabled, Action EnableSharing, Action Pair,
    Action<ScrollPreferences> SetScroll, Action<ArrangementInfo> Arrange, Action Quit);
internal sealed record QuickPanelPeer(string Status, bool Connected, ArrangementInfo? Arrangement = null,
    QualityInfo? Quality = null, bool ThisPCActive = false, ScrollPreferences? Scroll = null);

/// Compact Control Center surface matching the Mac's menu panel. The middle scrolls on small displays;
/// the master switch and menu actions stay reachable at every supported DPI.
internal sealed class QuickPanel : Window
{
    private readonly WindowsFileTransfer files;
    private readonly Settings settings;
    private readonly QuickPanelActions actions;
    private readonly Grid root = new() { Padding = new Thickness(12), RowSpacing = 10 };
    private readonly StackPanel body = new() { Spacing = 10 }, devices = new() { Spacing = 5 };
    private readonly ScrollViewer scroll;
    private readonly Canvas screens = new() { Height = 94 };
    private readonly TextBlock connection = Text("Waiting for Mac", 11), status = Text("Files ready", 11), incoming = Text("", 11), notice = Text("", 11);
    private readonly ProgressBar progress = new() { Maximum = 100, Height = 3 };
    private readonly Button receive = SmallButton("Receive to Downloads"), cancel = SmallButton("Cancel"), choose = SmallButton("Choose…");
    private readonly Button enableFiles = SmallButton("Enable file sharing"), reverse, smooth;
    private readonly Border inbox, drop, notification;
    private readonly ToggleSwitch enabled;
    private QuickPanelPeer peer = new("Waiting for Mac", false);
    private bool picking, closing, shown;
    internal double? PreviewMaxHeight { get; set; }
    internal ScrollViewer MiddleScroll => scroll;
    internal Canvas ScreenCanvas => screens;
    internal Grid PanelRoot => root;
    private static readonly Brush Pine = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 22, 135, 99));
    public QuickPanel(Settings settings, WindowsFileTransfer files, QuickPanelActions actions)
    {
        this.settings = settings; this.files = files; this.actions = actions; Title = "Unictrl";
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false); presenter.IsResizable = false;
            presenter.IsMaximizable = false; presenter.IsMinimizable = false; presenter.IsAlwaysOnTop = true;
        }
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new Grid { ColumnSpacing = 9 }; Columns(header, GridLength.Auto, new(1, GridUnitType.Star), GridLength.Auto);
        header.Children.Add(new Border { Width = 30, Height = 30, CornerRadius = new(15), Background = Pine, Child = new FontIcon { Glyph = "\uE7F4", FontSize = 15, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) } });
        var title = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center }; title.Children.Add(Text("Unictrl", 14, true));
        connection.TextWrapping = TextWrapping.NoWrap; connection.TextTrimming = TextTrimming.CharacterEllipsis; connection.Opacity = .7; title.Children.Add(connection); Grid.SetColumn(title, 1); header.Children.Add(title);
        enabled = new ToggleSwitch { MinWidth = 0, IsOn = settings.Enabled, OnContent = "", OffContent = "", VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(enabled, "Enable keyboard and mouse sharing"); Grid.SetColumn(enabled, 2); header.Children.Add(enabled);
        enabled.Toggled += (_, _) => { if (settings.Enabled != enabled.IsOn) actions.SetEnabled(enabled.IsOn); };
        root.Children.Add(header);
        scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); root.Children.Add(scroll);

        var map = new Grid { RowSpacing = 2 }; map.RowDefinitions.Add(new() { Height = GridLength.Auto }); map.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var mapTitle = new Grid(); Columns(mapTitle, new(1, GridUnitType.Star), GridLength.Auto); mapTitle.Children.Add(SectionTitle("Screen layout"));
        var arrange = new HyperlinkButton { Content = "Arrange ›", FontSize = 11, Padding = new Thickness(0), MinHeight = 18 }; Grid.SetColumn(arrange, 1); mapTitle.Children.Add(arrange); arrange.Click += (_, _) => OpenSettings(); map.Children.Add(mapTitle);
        var mapButton = new Button { Content = screens, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(0), CornerRadius = new(10), Background = Surface(), BorderThickness = new(0) };
        mapButton.Click += (_, _) => OpenSettings(); Grid.SetRow(mapButton, 1); map.Children.Add(mapButton); body.Children.Add(map);
        screens.SizeChanged += (_, _) => RenderScreens();
        body.Children.Add(devices);

        var fileCard = new StackPanel { Spacing = 7 }; var fileHeading = new Grid(); Columns(fileHeading, new(1, GridUnitType.Star), GridLength.Auto);
        fileHeading.Children.Add(Text("File transfer", 12, true)); Grid.SetColumn(cancel, 1); fileHeading.Children.Add(cancel); cancel.Click += (_, _) => files.Cancel(); fileCard.Children.Add(fileHeading);
        enableFiles.Click += (_, _) => actions.EnableSharing(); fileCard.Children.Add(enableFiles);
        var sendRow = new Grid { ColumnSpacing = 8 }; Columns(sendRow, GridLength.Auto, new(1, GridUnitType.Star), GridLength.Auto);
        sendRow.Children.Add(new FontIcon { Glyph = "\uE898", FontSize = 22, Foreground = Pine, VerticalAlignment = VerticalAlignment.Center });
        var sendText = new StackPanel { Spacing = 2 }; sendText.Children.Add(Text("Drop files to send", 12, true)); var hint = Text("Up to 1 GiB per file", 10); hint.Opacity = .65; sendText.Children.Add(hint); Grid.SetColumn(sendText, 1); sendRow.Children.Add(sendText);
        Grid.SetColumn(choose, 2); sendRow.Children.Add(choose); choose.Click += Choose;
        drop = Card(sendRow, 10); drop.AllowDrop = true; drop.DragOver += DragOver; drop.Drop += Drop; fileCard.Children.Add(drop);
        var incomingContent = new StackPanel { Spacing = 5 }; incomingContent.Children.Add(Text("Incoming files", 11, true)); incoming.MaxLines = 2; incoming.TextTrimming = TextTrimming.CharacterEllipsis; incomingContent.Children.Add(incoming);
        incomingContent.Children.Add(receive); receive.Click += Receive; inbox = Card(incomingContent, 8, true); fileCard.Children.Add(inbox);
        fileCard.Children.Add(progress); status.Opacity = .7; status.MaxLines = 2; status.TextTrimming = TextTrimming.CharacterEllipsis; fileCard.Children.Add(status);
        var limits = Text("Copy/paste ≤100 MiB · cache clears after 1 hour", 10); limits.Opacity = .6; fileCard.Children.Add(limits);
        notification = Card(notice, 8); fileCard.Children.Add(notification); body.Children.Add(fileCard);

        var wheel = new Grid { ColumnSpacing = 8 }; Columns(wheel, new(1, GridUnitType.Star), new(1, GridUnitType.Star));
        reverse = WheelTile("Reverse Wheel", "\uE8CB", true); smooth = WheelTile("Smooth Scroll", "\uE8CB", false);
        wheel.Children.Add(reverse); Grid.SetColumn(smooth, 1); wheel.Children.Add(smooth); body.Children.Add(wheel);
        var menu = new StackPanel { Spacing = 1 }; menu.Children.Add(new Border { Height = 1, Background = Surface(), Margin = new Thickness(0, 0, 0, 5) });
        menu.Children.Add(MenuRow("Pair a Mac…", "\uE710", () => { AppWindow.Hide(); actions.Pair(); }));
        menu.Children.Add(MenuRow("Settings & history…", "\uE713", OpenSettings)); menu.Children.Add(MenuRow("Quit Unictrl", "\uE7E8", actions.Quit));
        Grid.SetRow(menu, 2); root.Children.Add(menu);
        Content = new Border { CornerRadius = new(12), BorderThickness = new(1), BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(45, 128, 128, 128)), Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"], Child = root };
        Activated += (_, e) => { if (e.WindowActivationState == WindowActivationState.Deactivated && !picking) AppWindow.Hide(); };
        AppWindow.Closing += (_, e) => { if (!closing) { e.Cancel = true; AppWindow.Hide(); } };
        root.ActualThemeChanged += (_, _) => {
            if (Content is Border surface) surface.Background = new SolidColorBrush(root.ActualTheme == ElementTheme.Dark ? Windows.UI.Color.FromArgb(255, 32, 32, 32) : Windows.UI.Color.FromArgb(255, 248, 248, 248));
            UpdateWheel(reverse, peer.Scroll?.Reverse ?? false); UpdateWheel(smooth, peer.Scroll?.Smooth ?? false); RenderDevices();
        };
        root.Loaded += (_, _) => { RenderScreens(); if (shown) Fit(); };
        Update(new("Enable files on both computers")); UpdatePeer(peer);
    }
    private static void Columns(Grid grid, params GridLength[] widths) { foreach (var width in widths) grid.ColumnDefinitions.Add(new() { Width = width }); }
    private static TextBlock Text(string value, double size, bool bold = false) => new() { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal };
    private static Brush Surface() => new SolidColorBrush(Windows.UI.Color.FromArgb(13, 128, 128, 128));
    private static TextBlock SectionTitle(string text) { var title = Text(text, 11, true); title.Opacity = .65; return title; }
    private static Button SmallButton(string text) => new() { Content = text, FontSize = 11, MinHeight = 26, Padding = new Thickness(8, 3, 8, 3), HorizontalAlignment = HorizontalAlignment.Left };
    private static Border Card(UIElement child, double padding, bool tint = false) => new() { Child = child, Padding = new(padding), CornerRadius = new(9), BorderThickness = new(1), BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(45, 22, 135, 99)), Background = tint ? new SolidColorBrush(Windows.UI.Color.FromArgb(18, 22, 135, 99)) : Surface() };
    private Button MenuRow(string text, string glyph, Action action)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9 }; row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 13, Width = 18 }); row.Children.Add(Text(text, 12));
        var button = new Button { Content = row, Padding = new Thickness(8, 5, 8, 5), MinHeight = 30, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new(0), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
        button.Click += (_, _) => action(); return button;
    }
    private Button WheelTile(string name, string glyph, bool reversed)
    {
        var button = new Button { Padding = new Thickness(8), MinHeight = 52, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Background = Surface(), CornerRadius = new(10), BorderThickness = new(0) };
        button.Tag = name; button.Click += (_, _) => { if (peer.Connected && peer.Scroll is { } value) actions.SetScroll(reversed ? value with { Reverse = !value.Reverse } : value with { Smooth = !value.Smooth }); };
        return button;
    }
    private void UpdateWheel(Button button, bool on)
    {
        var row = new Grid { ColumnSpacing = 7 }; Columns(row, GridLength.Auto, new(1, GridUnitType.Star));
        row.Children.Add(new Border { Width = 26, Height = 26, CornerRadius = new(13), Background = on ? Pine : Surface(), Child = new FontIcon { Glyph = button == reverse ? "↕" : "≋", FontFamily = new FontFamily("Segoe UI"), FontSize = 14, Foreground = on ? new SolidColorBrush(Microsoft.UI.Colors.White) : new SolidColorBrush(root.ActualTheme == ElementTheme.Dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black) } });
        var text = new StackPanel { Spacing = 1 }; text.Children.Add(Text((string)button.Tag, 11, true)); var state = Text(peer.Scroll is null ? "Connect / update Mac" : on ? "On" : "Off", 10); state.Opacity = .65; text.Children.Add(state); Grid.SetColumn(text, 1); row.Children.Add(text); button.Content = row;
        button.IsEnabled = peer.Connected && peer.Scroll is not null;
    }
    private void OpenSettings() { AppWindow.Hide(); actions.OpenSettings(); }
    public void UpdatePeer(QuickPanelPeer value)
    {
        var redraw = !ReferenceEquals(peer.Arrangement, value.Arrangement) || peer.Connected != value.Connected || peer.ThisPCActive != value.ThisPCActive;
        peer = value; connection.Text = !settings.Enabled ? "Paused" : value.Status; enabled.IsOn = settings.Enabled;
        UpdateWheel(reverse, value.Scroll?.Reverse ?? false); UpdateWheel(smooth, value.Scroll?.Smooth ?? false); RenderDevices();
        if (redraw) RenderScreens();
    }
    private void RenderDevices()
    {
        devices.Children.Clear(); devices.Children.Add(SectionTitle("Devices"));
        var list = peer.Arrangement?.Screens ?? [new ScreenInfo("mac", string.IsNullOrWhiteSpace(settings.MacName) ? "Mac" : settings.MacName, 300, 190, 0, peer.Connected), new ScreenInfo(settings.DeviceId, Environment.MachineName, 500, 300, 0, true)];
        foreach (var screen in list.OrderBy(s => !s.IsMac))
        {
            var mine = screen.Id == (peer.Arrangement?.You ?? settings.DeviceId); var online = mine || peer.Connected && screen.Online;
            var row = new Grid { ColumnSpacing = 8, MinHeight = 27 }; Columns(row, GridLength.Auto, new(1, GridUnitType.Star), GridLength.Auto);
            row.Children.Add(new Border { Width = 24, Height = 24, CornerRadius = new(12), Background = Surface(), Child = new FontIcon { Glyph = screen.IsMac ? "\uE7F8" : "\uE7F4", FontSize = 12, Foreground = online ? Pine : new SolidColorBrush(root.ActualTheme == ElementTheme.Dark ? Microsoft.UI.Colors.LightGray : Microsoft.UI.Colors.Gray) } });
            var name = Text(screen.Name, 12); name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis; name.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(name, 1); row.Children.Add(name);
            var status = !online ? "Offline" : mine ? (peer.ThisPCActive ? "In use · this PC" : "This PC") : screen.IsMac && peer.Quality?.RttMs is { } latency ? $"{latency:0} ms · Connected" : "Connected";
            var badge = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
            badge.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = online ? Pine : new SolidColorBrush(Microsoft.UI.Colors.Gray) }); var label = Text(status, 10); label.Opacity = .7; badge.Children.Add(label); Grid.SetColumn(badge, 2); row.Children.Add(badge); devices.Children.Add(row);
        }
    }
    private void RenderScreens()
    {
        screens.Children.Clear(); var width = screens.ActualWidth; if (width <= 0) width = 334;
        if (peer.Arrangement is not { } layout || layout.Screens.Count == 0)
        {
            var hint = Text(peer.Connected ? "Waiting for screen positions…" : "Connect to see screen positions", 11); hint.Opacity = .6; Canvas.SetLeft(hint, 14); Canvas.SetTop(hint, 37); screens.Children.Add(hint); return;
        }
        var rectangles = layout.Rectangles(); var left = rectangles.Values.Min(r => r.X); var top = rectangles.Values.Min(r => r.Y);
        var spanX = rectangles.Values.Max(r => r.Right) - left; var spanY = rectangles.Values.Max(r => r.Bottom) - top;
        var scale = Math.Min((width - 28) / Math.Max(1, spanX), (screens.Height - 12) / Math.Max(1, spanY));
        var x = (width - spanX * scale) / 2 - left * scale; var y = (screens.Height - spanY * scale) / 2 - top * scale;
        foreach (var screen in layout.Screens)
        {
            var r = rectangles[screen.Id]; var active = peer.ThisPCActive ? screen.Id == layout.You : screen.IsMac;
            var text = Text(screen.IsMac ? "Mac" : "Windows", 10, true); text.HorizontalAlignment = HorizontalAlignment.Center; text.VerticalAlignment = VerticalAlignment.Center; text.TextWrapping = TextWrapping.NoWrap; text.TextTrimming = TextTrimming.CharacterEllipsis; text.Foreground = active ? new SolidColorBrush(Microsoft.UI.Colors.White) : Pine;
            var tile = new Border { Tag = screen.Id, Width = Math.Max(1, r.Width * scale - 2), Height = Math.Max(1, r.Height * scale - 2), CornerRadius = new(5), Background = active ? Pine : new SolidColorBrush(Windows.UI.Color.FromArgb(45, 22, 135, 99)), Child = text, BorderThickness = new(1), BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(60, 22, 135, 99)), Opacity = screen.Online || screen.Id == layout.You ? 1 : .5 };
            ToolTipService.SetToolTip(tile, screen.Name + (active ? " · In use" : "")); Canvas.SetLeft(tile, x + r.X * scale + 1); Canvas.SetTop(tile, y + r.Y * scale + 1); screens.Children.Add(tile);
        }
        var menu = new MenuFlyout(); foreach (var (name, edge) in new[] { ("Windows above Mac", ScreenEdge.Top), ("Windows below Mac", ScreenEdge.Bottom), ("Windows left of Mac", ScreenEdge.Left), ("Windows right of Mac", ScreenEdge.Right) })
        { var item = new MenuFlyoutItem { Text = name, IsEnabled = peer.Connected && layout.Supports2D }; item.Click += (_, _) => actions.Arrange(layout.Place(layout.You, edge)); menu.Items.Add(item); }
        screens.ContextFlyout = menu;
    }
    public void Update(FileViewState state)
    {
        enableFiles.Visibility = settings.ClipboardEnabled && settings.ClipboardFilesEnabled ? Visibility.Collapsed : Visibility.Visible;
        drop.Visibility = settings.ClipboardEnabled && settings.ClipboardFilesEnabled ? Visibility.Visible : Visibility.Collapsed;
        status.Text = state.Status; incoming.Text = state.Incoming; notice.Text = state.Notice;
        notification.Visibility = state.Notice.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        inbox.Visibility = state.Incoming.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        progress.Visibility = cancel.Visibility = state.Busy ? Visibility.Visible : Visibility.Collapsed;
        progress.Value = state.Progress * 100; receive.IsEnabled = !state.Busy || state.CanReceive; choose.IsEnabled = !state.Busy;
        if (shown) Fit();
    }
    public void ClosePanel() { closing = true; Close(); }
    public void Toggle() { if (AppWindow.IsVisible) AppWindow.Hide(); else Show(); }
    public void Show() { shown = true; Fit(); AppWindow.Show(); Activate(); Native.SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)); }
    private void Fit()
    {
        Native.GetCursorPos(out var cursor); var area = DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Nearest).WorkArea;
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        var width = Math.Min(360, Math.Max(280, area.Width / scale - 24));
        // Measure natural content first; reserve header and footer outside the scrollable middle.
        body.Measure(new Size(width - 26, double.PositiveInfinity));
        var natural = body.DesiredSize.Height + 34 + 101 + 24 + 20 + 2;
        var max = Math.Min(PreviewMaxHeight ?? 680, area.Height / scale - 24);
        var height = Math.Min(natural, Math.Max(260, max));
        var pxWidth = (int)Math.Ceiling(width * scale); var pxHeight = (int)Math.Ceiling(height * scale);
        // Size the client, not the outer frame: the invisible resize border otherwise steals
        // several DIPs and clips the bottom wheel controls after an incoming offer expands the card.
        AppWindow.ResizeClient(new SizeInt32(pxWidth, pxHeight));
        var outer = AppWindow.Size;
        AppWindow.Move(new PointInt32(Math.Max(area.X, area.X + area.Width - outer.Width - 12), Math.Max(area.Y, area.Y + area.Height - outer.Height - 12)));
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
    private void Receive(object sender, RoutedEventArgs e)
    {
        try { files.Receive(); }
        catch (Exception error) { notice.Text = error.Message; notice.Visibility = Visibility.Visible; }
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
