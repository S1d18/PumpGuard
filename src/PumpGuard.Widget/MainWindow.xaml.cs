using System.Diagnostics;
using System.Globalization;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PumpGuard.Core;

namespace PumpGuard.Widget;

public sealed record Row(string Name, string Value, Brush Dot, PointCollection? Spark, string? SparkTip, double SparkWidth, double SparkHeight);

public sealed record GroupView(string Title, IReadOnlyList<Row> Rows, double Width, Thickness Margin);

public partial class MainWindow : Window
{
    private readonly WidgetSettings _settings = WidgetSettings.Load();
    private readonly ApiClient _api;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _busy;
    private bool _inAlarm;
    private DateTime _lastBeep;
    private FansWindow? _fansWindow;
    private readonly SeriesStore _series = new();
    private bool _historyLoaded;
    private readonly AppBar _appBar;
    private Rect? _barRect;          // where the bar is docked; null when floating
    private BarEdge? _dockedEdge;
    private bool _docking;            // suppresses position saving while we move the window ourselves

    private const double NarrowBelow = 180, GripSize = 6, MoveThreshold = 10;
    private double BarHeight => Math.Clamp(_settings.BarHeight, 26, 48);
    private double SideWidth => Math.Clamp(_settings.SideWidth, 90, 600);
    private bool Narrow => _settings.View == ViewMode.Bar && SideEdge && SideWidth < NarrowBelow;

    private enum DockDrag { None, Move, Resize }
    private DockDrag _dockDrag;
    private Point _dragStart;

    private bool SideEdge => _settings.BarEdge is BarEdge.Left or BarEdge.Right;

    public MainWindow()
    {
        InitializeComponent();
        _api = new ApiClient(_settings);
        _appBar = new AppBar(this, Redock);
        Topmost = _settings.Topmost;
        PlaceWindow();
        // Docking needs the window handle, which exists only after SourceInitialized.
        SourceInitialized += (_, _) => ApplyView();
        Closing += (_, _) => _appBar.Undock();
        _timer.Tick += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) => { await RefreshAsync(); _timer.Start(); };
    }

    private Brush B(string key) => (Brush)FindResource(key);

    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            if (!_historyLoaded)
            {
                // Pre-fill the sparklines with what the service already recorded.
                _historyLoaded = true;
                try
                {
                    if (await _api.GetHistoryAsync((int)_series.Window.TotalSeconds) is { } history) _series.Seed(history);
                }
                catch { /* graphs simply start empty */ }
            }
            var status = await _api.GetStatusAsync();
            if (status is null) RenderOffline("пустой ответ службы");
            else Render(status);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            RenderOffline(ex is TaskCanceledException ? "служба не отвечает" : "служба недоступна");
        }
        catch (Exception ex)
        {
            // A widget bug, not a service outage: keep it visible in the log instead of posing as "offline".
            WidgetLog.Write(ex);
            RenderOffline("ошибка виджета, см. widget.log");
        }
        finally
        {
            _busy = false;
        }
    }

    private void Render(GuardStatus s)
    {
        var stale = DateTimeOffset.Now - s.Timestamp > TimeSpan.FromSeconds(5);
        var (stateBrush, stateText) = stale ? (B("Muted"), "данные устарели") : s.State switch
        {
            GuardState.Normal => (B("Ok"), "норма"),
            GuardState.Warning => (B("Warn"), s.SnoozedUntil is { } until ? $"тревога отложена до {until:HH:mm}" : "внимание"),
            GuardState.Alarm => (B("Crit"), "ТРЕВОГА"),
            _ => (B("Crit"), "ВЫКЛЮЧЕНИЕ"),
        };
        StateDot.Fill = stateBrush;
        StateText.Text = s.DryRun ? $"{stateText} · тест" : stateText;
        StateText.Foreground = s.State == GuardState.Normal ? B("Muted") : stateBrush;
        RenderAlarm(s);
        ApplyView();

        PumpRpm.Text = s.Pump.Rpm is { } rpm ? rpm.ToString("0", CultureInfo.InvariantCulture) : "—";
        PumpRpm.Foreground = s.Pump.Ok ? B("Text") : B("Crit");
        PumpSub.Text = s.Pump.SensorId is null ? "датчик не настроен" : $"мин. {s.Pump.MinRpm:0}";

        _series.Add(s);
        var (pumpPts, pumpTip) = Spark("pump", "RPM", PumpSparkHost.Width, PumpSparkHost.Height);
        PumpSpark.Points = pumpPts ?? new PointCollection();
        PumpSpark.Tag = pumpTip;

        var view = EffectiveView;
        var (sparkW, sparkH) = view == ViewMode.Wide ? (130.0, 28.0) : (76.0, 18.0);
        Row Item(string name, string value, Brush dot, string key, string unit)
        {
            var (points, tip) = Spark(key, unit, sparkW, sparkH);
            return new Row(name, value, dot, points, tip, sparkW, sparkH);
        }

        var blocks = new List<(string Title, List<Row> Rows)>();
        // Blocks in config order (CPU, GPU, ...); each block lists its temperatures, then its other values.
        var groups = BlockNames(s);
        foreach (var g in groups)
        {
            var rows = new List<Row>();
            foreach (var t in s.Temperatures.Where(t => t.Group == g))
                rows.Add(Item(t.Label ?? t.Name, t.Value is { } v ? $"{v:0} °C" : "—", LevelDot(t.Level), $"temp:{t.Name}", "°C"));
            foreach (var e in s.Extras.Where(e => e.Group == g))
                rows.Add(Item(e.Name, FormatExtra(e), Brushes.Transparent, $"extra:{e.Group}/{e.Name}", e.Unit));
            blocks.Add((g == "" ? (groups.Count > 1 ? "Прочее" : "") : g, rows));
        }
        if (s.Fans.Count > 0)
        {
            var rows = new List<Row>();
            foreach (var f in s.Fans)
            {
                var pct = (f.TargetPercent ?? f.Percent) is { } p ? $" · {p:0}%" : "";
                var mode = f.Mode is "Manual" or "Max" ? $" ({(f.Mode == "Max" ? "макс" : "ручн.")})" : "";
                rows.Add(Item(f.Name + mode, (f.Rpm is { } r ? $"{r:0}" : "—") + pct,
                    f.Mode == "Max" ? B("Hot") : Brushes.Transparent, $"fan:{f.Name}", "RPM"));
            }
            blocks.Add((s.FanControlEnabled ? $"Вентиляторы · {s.FanPreset}" : "Вентиляторы", rows));
        }
        Groups.ItemsSource = blocks.Select((b, i) => new GroupView(
            b.Title.ToUpperInvariant(), b.Rows,
            view == ViewMode.Wide ? 300 : double.NaN,
            new Thickness(0, 0, view == ViewMode.Wide && i < blocks.Count - 1 ? 28 : 0, 0))).ToList();

        RenderCompact(s, stateBrush, stateText);
        RenderBar(s, stateBrush, stateText);
        RenderNarrow(s, stateBrush, stateText);

        var notes = s.Issues.Where(i => i.Severity == IssueSeverity.Warning).Select(i => "• " + i.Message).Take(4).ToList();
        Footer.Text = string.Join("\n", notes);
        Footer.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The strip: pump, then per block its first temperature and first power reading.</summary>
    private void RenderCompact(GuardStatus s, Brush stateBrush, string stateText)
    {
        CompactDot.Fill = stateBrush;
        CompactDot.ToolTip = stateText;
        CompactItems.Children.Clear();
        AddSegment("Помпа", s.Pump.Rpm is { } rpm ? $"{rpm:0}" : "—", s.Pump.Ok ? null : B("Crit"));
        foreach (var g in BlockNames(s).Where(g => g != ""))
        {
            var temp = s.Temperatures.FirstOrDefault(t => t.Group == g);
            var power = s.Extras.FirstOrDefault(e => e.Group == g && e.Unit == "W");
            var parts = new List<string>();
            if (temp is not null) parts.Add(temp.Value is { } v ? $"{v:0} °C" : "—");
            if (power is not null) parts.Add(FormatExtra(power));
            if (parts.Count > 0) AddSegment(g, string.Join(" · ", parts), temp is null ? null : NullIfClear(LevelDot(temp.Level)));
        }
        var warnings = s.Issues.Count(i => i.Severity == IssueSeverity.Warning);
        if (warnings > 0)
            AddSegment("⚠", warnings == 1 ? "1 предупреждение" : $"{warnings} предупр.", B("Warn"), s.Issues.Select(i => i.Message));
    }

    private void AddSegment(string label, string value, Brush? dot, IEnumerable<string>? tip = null)
    {
        if (CompactItems.Children.Count > 0)
            CompactItems.Children.Add(new TextBlock { Text = "│", Foreground = B("Muted"), Opacity = 0.5, Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        text.Inlines.Add(new System.Windows.Documents.Run(label + " ") { Foreground = B("Muted"), FontSize = 11 });
        text.Inlines.Add(new System.Windows.Documents.Run(value) { Foreground = B("Text"), FontSize = 12, FontWeight = FontWeights.SemiBold });
        if (tip is not null) text.ToolTip = string.Join("\n", tip);
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(text);
        if (dot is not null)
            panel.Children.Add(new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = dot, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        CompactItems.Children.Add(panel);
    }

    private static Brush? NullIfClear(Brush b) => b == Brushes.Transparent ? null : b;

    /// <summary>The full-width bar: pump, every value of each block with the block's sparkline, then fans.</summary>
    private void RenderBar(GuardStatus s, Brush stateBrush, string stateText)
    {
        BarDot.Fill = stateBrush;
        BarState.Text = s.DryRun ? $"{stateText} · тест" : stateText;
        BarItems.Children.Clear();

        AddBarBlock("ПОМПА", [("", s.Pump.Rpm is { } rpm ? $"{rpm:0} об/мин" : "—", s.Pump.Ok ? null : B("Crit"))], "pump", "RPM");
        foreach (var g in BlockNames(s))
        {
            var items = new List<(string, string, Brush?)>();
            foreach (var t in s.Temperatures.Where(t => t.Group == g))
                items.Add((t.Label ?? t.Name, t.Value is { } v ? $"{v:0}°" : "—", NullIfClear(LevelDot(t.Level))));
            foreach (var e in s.Extras.Where(e => e.Group == g))
                items.Add((e.Name, FormatExtra(e), null));
            var first = s.Temperatures.FirstOrDefault(t => t.Group == g);
            AddBarBlock(g == "" ? "ПРОЧЕЕ" : g.ToUpperInvariant(), items,
                first is null ? null : $"temp:{first.Name}", "°C");
        }
        if (s.Fans.Count > 0)
            AddBarBlock(s.FanControlEnabled ? $"ВЕНТ. · {s.FanPreset}" : "ВЕНТ.",
                s.Fans.Select(f => (f.Name, f.Rpm is { } r ? $"{r:0}" : "—", f.Mode == "Max" ? B("Hot") : (Brush?)null)).ToList(),
                null, "RPM");

        var warnings = s.Issues.Where(i => i.Severity == IssueSeverity.Warning).Select(i => i.Message).ToList();
        if (warnings.Count > 0)
        {
            BarState.Text = $"⚠ {warnings.Count} · " + BarState.Text;
            BarState.ToolTip = string.Join("\n", warnings);
        }
        else BarState.ToolTip = null;
    }

    /// <summary>Narrow side column: a tile per block with its main temperature, power and sparkline.</summary>
    private void RenderNarrow(GuardStatus s, Brush stateBrush, string stateText)
    {
        NarrowDot.Fill = stateBrush;
        var warnings = s.Issues.Where(i => i.Severity == IssueSeverity.Warning).Select(i => i.Message).ToList();
        NarrowState.Text = warnings.Count > 0 ? $"{stateText}\n⚠ {warnings.Count} предупр." : stateText;
        NarrowState.ToolTip = warnings.Count > 0 ? string.Join("\n", warnings) : null;
        NarrowItems.Children.Clear();

        AddTile("ПОМПА", s.Pump.Rpm is { } rpm ? $"{rpm:0}" : "—", "об/мин", s.Pump.Ok ? null : B("Crit"), "pump", "RPM");
        foreach (var g in BlockNames(s).Where(g => g != ""))
        {
            var temp = s.Temperatures.FirstOrDefault(t => t.Group == g);
            var power = s.Extras.FirstOrDefault(e => e.Group == g && e.Unit == "W");
            AddTile(temp?.Label is { } l ? $"{g} · {l}" : g, temp?.Value is { } v ? $"{v:0}°" : "—",
                power is null ? "" : FormatExtra(power),
                temp is null ? null : NullIfClear(LevelDot(temp.Level)), temp is null ? null : $"temp:{temp.Name}", "°C");
        }
        if (s.Fans.Count > 0)
        {
            NarrowItems.Children.Add(new TextBlock { Text = "ВЕНТ.", Foreground = B("Muted"), FontSize = 10, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2) });
            foreach (var f in s.Fans)
            {
                var line = new DockPanel { Margin = new Thickness(0, 0, 0, 1), ToolTip = f.Mode };
                var value = new TextBlock { Text = f.Rpm is { } r ? $"{r:0}" : "—", Foreground = f.Mode == "Max" ? B("Hot") : B("Text"), FontSize = 11, FontWeight = FontWeights.SemiBold };
                DockPanel.SetDock(value, System.Windows.Controls.Dock.Right);
                line.Children.Add(value);
                line.Children.Add(new TextBlock { Text = f.Name, Foreground = B("Muted"), FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
                NarrowItems.Children.Add(line);
            }
        }
    }

    private void AddTile(string title, string value, string sub, Brush? dot, string? sparkKey, string unit)
    {
        var tile = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        tile.Children.Add(new TextBlock { Text = title.ToUpperInvariant(), Foreground = B("Muted"), FontSize = 10, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        var main = new StackPanel { Orientation = Orientation.Horizontal };
        main.Children.Add(new TextBlock { Text = value, Foreground = B("Text"), FontSize = 22, FontWeight = FontWeights.Light });
        if (dot is not null)
            main.Children.Add(new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = dot, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        tile.Children.Add(main);
        if (sub != "") tile.Children.Add(new TextBlock { Text = sub, Foreground = B("Text"), FontSize = 12, FontWeight = FontWeights.SemiBold });
        if (sparkKey is not null)
        {
            var (points, tip) = Spark(sparkKey, unit, 80, 18);
            var host = new Border { Width = 80, Height = 18, Background = Brushes.Transparent, ToolTip = tip, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 3, 0, 0) };
            host.Child = new System.Windows.Shapes.Polyline { Points = points ?? new PointCollection(), Stroke = B("Series"), StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round };
            tile.Children.Add(host);
        }
        NarrowItems.Children.Add(tile);
    }

    private void AddBarBlock(string title, IReadOnlyList<(string Label, string Value, Brush? Dot)> items, string? sparkKey, string unit)
    {
        // Every block, the first one included, is separated from what precedes it (the title).
        BarItems.Children.Add(new Border { Width = 1, Height = 16, Background = B("Muted"), Opacity = 0.35, Margin = new Thickness(12, 0, 12, 0) });
        var block = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        block.Children.Add(new TextBlock { Text = title, Foreground = B("Muted"), FontSize = 10, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        foreach (var (label, value, dot) in items)
        {
            var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, dot is null ? 10 : 4, 0) };
            if (label != "") text.Inlines.Add(new System.Windows.Documents.Run(label + " ") { Foreground = B("Muted"), FontSize = 11 });
            text.Inlines.Add(new System.Windows.Documents.Run(value) { Foreground = B("Text"), FontSize = 12, FontWeight = FontWeights.SemiBold });
            block.Children.Add(text);
            if (dot is not null)
                block.Children.Add(new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = dot, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        }
        if (sparkKey is not null)
        {
            var (points, tip) = Spark(sparkKey, unit, 56, 18);
            var host = new Border { Width = 56, Height = 18, Background = Brushes.Transparent, ToolTip = tip, VerticalAlignment = VerticalAlignment.Center };
            host.Child = new System.Windows.Shapes.Polyline { Points = points ?? new PointCollection(), Stroke = B("Series"), StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round };
            block.Children.Add(host);
        }
        BarItems.Children.Add(block);
    }

    private static List<string> BlockNames(GuardStatus s) =>
        s.Temperatures.Select(t => t.Group).Concat(s.Extras.Select(e => e.Group))
            .Distinct().OrderBy(g => g == "" ? 1 : 0).ToList();

    /// <summary>During an alarm the strip expands, so the countdown and the cancel button are visible.</summary>
    private ViewMode EffectiveView => _inAlarm && _settings.View == ViewMode.Compact ? ViewMode.Normal : _settings.View;

    private void ApplyView()
    {
        var view = EffectiveView;
        var compact = view == ViewMode.Compact;
        var bar = view == ViewMode.Bar;
        var strip = bar && !SideEdge;   // one line along the top/bottom edge
        var narrow = bar && Narrow;     // ~100 px column of tiles
        // A wide side panel is tall enough for the full card layout.
        CompactContent.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        BarContent.Visibility = strip ? Visibility.Visible : Visibility.Collapsed;
        NarrowContent.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
        FullContent.Visibility = compact || strip || narrow ? Visibility.Collapsed : Visibility.Visible;
        UpdateDocking(bar);
        Card.Width = view == ViewMode.Normal ? 330 : double.NaN; // docked: the card fills the window
        Card.Height = double.NaN;
        Card.Padding = strip ? new Thickness(14, 0, 14, 0) : narrow ? new Thickness(10, 10, 8, 10)
            : compact ? new Thickness(12, 6, 12, 6) : new Thickness(16, 12, 16, 12);
        Card.CornerRadius = new CornerRadius(bar ? 0 : compact ? 8 : 12);
        Card.Background = strip && _inAlarm ? new SolidColorBrush(Color.FromArgb(0xF0, 0x8B, 0x1A, 0x1A)) : _cardBackground ??= Card.Background;
        Groups.ItemsPanel = (ItemsPanelTemplate)FindResource(view == ViewMode.Wide ? "SideBySideGroups" : "StackedGroups");
        (PumpSparkHost.Width, PumpSparkHost.Height) = view == ViewMode.Wide ? (220, 34) : (110, 26);
    }

    private Brush? _cardBackground;

    /// <summary>Docks to / undocks from the screen edge only when the bar mode or its edge actually changes.</summary>
    private void UpdateDocking(bool bar)
    {
        if (new System.Windows.Interop.WindowInteropHelper(this).Handle == IntPtr.Zero) return;
        if (bar && _dockedEdge != _settings.BarEdge) Dock();
        else if (!bar && _dockedEdge is not null) Undock();
    }

    private DateTime _lastDock;

    /// <summary>
    /// Takes (or re-takes) the strip along the chosen edge. The app bar stays registered across calls:
    /// re-registering makes the shell broadcast "position changed", which would bring us straight back here.
    /// </summary>
    private void Dock()
    {
        var edge = _settings.BarEdge;
        var thickness = SideEdge ? SideWidth : BarHeight;
        // Switching edges: drop the old reservation first, so the new one is measured against a clean work area.
        if (_dockedEdge is { } old && old != edge) _appBar.Undock();
        Rect rect;
        if (_settings.BarReserve)
        {
            rect = _appBar.Dock(edge, thickness);
        }
        else
        {
            _appBar.Undock();
            var a = SystemParameters.WorkArea;
            rect = edge switch
            {
                BarEdge.Left => new Rect(a.Left, a.Top, thickness, a.Height),
                BarEdge.Right => new Rect(a.Right - thickness, a.Top, thickness, a.Height),
                BarEdge.Bottom => new Rect(a.Left, a.Bottom - thickness, a.Width, thickness),
                _ => new Rect(a.Left, a.Top, a.Width, thickness),
            };
        }
        _lastDock = DateTime.Now;
        _dockedEdge = _settings.BarEdge;
        if (_barRect == rect) return;
        _barRect = rect;
        PlaceDocked(rect);
        Topmost = true;
    }

    private void Undock()
    {
        _appBar.Undock();
        _barRect = null;
        _dockedEdge = null;
        _docking = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        ClearValue(WidthProperty);
        ClearValue(HeightProperty);
        PlaceWindow();
        _docking = false;
        Topmost = _settings.Topmost || _inAlarm;
    }

    /// <summary>The shell asked us to reposition (taskbar moved, resolution changed).</summary>
    private void Redock()
    {
        // Our own SETPOS echoes back as a notification; ignore echoes right after docking.
        if (_dockedEdge is null || DateTime.Now - _lastDock < TimeSpan.FromMilliseconds(500)) return;
        Dock();
    }

    private void SetView(ViewMode view, BarEdge? edge = null)
    {
        if (edge is { } e) _settings.BarEdge = e;
        _settings.View = view;
        if (view is ViewMode.Normal or ViewMode.Wide) _settings.LastFullView = view;
        _settings.Save();
        ApplyView();
        _ = RefreshAsync();
    }

    private void OnViewCompact(object sender, RoutedEventArgs e) => SetView(ViewMode.Compact);
    private void OnViewNormal(object sender, RoutedEventArgs e) => SetView(ViewMode.Normal);
    private void OnViewWide(object sender, RoutedEventArgs e) => SetView(ViewMode.Wide);
    private void OnViewBarTop(object sender, RoutedEventArgs e) => SetView(ViewMode.Bar, BarEdge.Top);
    private void OnViewBarBottom(object sender, RoutedEventArgs e) => SetView(ViewMode.Bar, BarEdge.Bottom);
    private void OnViewBarLeft(object sender, RoutedEventArgs e) => SetView(ViewMode.Bar, BarEdge.Left);
    private void OnViewBarRight(object sender, RoutedEventArgs e) => SetView(ViewMode.Bar, BarEdge.Right);

    private void OnToggleSideNarrow(object sender, RoutedEventArgs e)
    {
        _settings.SideWidth = SideNarrowItem.IsChecked ? 100 : 300;
        _settings.Save();
        if (_dockedEdge is BarEdge.Left or BarEdge.Right) Dock(); // new width
        ApplyView();
        _ = RefreshAsync();
    }

    private void OnToggleBarReserve(object sender, RoutedEventArgs e)
    {
        _settings.BarReserve = BarReserveItem.IsChecked;
        _settings.Save();
        if (_dockedEdge is not null) Dock();
    }

    /// <summary>Keeps the widget on screen when a wider view makes it grow past the edge.</summary>
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_dockedEdge is not null) return;
        var right = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth;
        var bottom = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
        if (Left + ActualWidth > right) Left = Math.Max(SystemParameters.VirtualScreenLeft, right - ActualWidth);
        if (Top + ActualHeight > bottom) Top = Math.Max(SystemParameters.VirtualScreenTop, bottom - ActualHeight);
    }

    private void RenderAlarm(GuardStatus s)
    {
        var alarm = s.State is GuardState.Alarm or GuardState.ShuttingDown;
        AlarmPanel.Visibility = alarm ? Visibility.Visible : Visibility.Collapsed;
        BarAlarm.Visibility = AlarmPanel.Visibility;
        NarrowAlarm.Visibility = AlarmPanel.Visibility;

        if (alarm)
        {
            AlarmText.Text = s.ShutdownReason ?? "Авария охлаждения";
            CountdownText.Text = s.State == GuardState.Alarm
                ? $"Выключение через {Math.Ceiling(s.SecondsToShutdown ?? 0):0} с"
                : s.DryRun ? "Выключение (тестовый режим)" : "Выключение…";
            CancelButton.Visibility = s.State == GuardState.Alarm ? Visibility.Visible : Visibility.Collapsed;
            BarAlarmText.Text = $"{AlarmText.Text} — {CountdownText.Text}";
            BarCancelButton.Visibility = CancelButton.Visibility;
            NarrowAlarmText.Text = CountdownText.Text;
            NarrowAlarmText.ToolTip = AlarmText.Text;
            NarrowCancelButton.Visibility = CancelButton.Visibility;

            if (!_inAlarm)
            {
                // Pull the widget in front of everything so the countdown cannot be missed.
                Topmost = true;
                Activate();
            }
            if (DateTime.Now - _lastBeep > TimeSpan.FromSeconds(2))
            {
                SystemSounds.Hand.Play();
                _lastBeep = DateTime.Now;
            }
        }
        else if (_inAlarm)
        {
            Topmost = _settings.Topmost || _dockedEdge is not null;
        }
        _inAlarm = alarm;
    }

    private void RenderOffline(string reason)
    {
        StateDot.Fill = B("Muted");
        StateText.Text = reason;
        StateText.Foreground = B("Crit");
        PumpRpm.Text = "—";
        PumpRpm.Foreground = B("Muted");
        PumpSub.Text = "";
        Groups.ItemsSource = null;
        CompactItems.Children.Clear();
        CompactItems.Children.Add(new TextBlock { Text = "PumpGuard: " + reason, Foreground = B("Crit"), FontSize = 12 });
        CompactDot.Fill = B("Muted");
        BarItems.Children.Clear();
        BarItems.Children.Add(new TextBlock { Text = "  служба недоступна — защита не работает", Foreground = B("Crit"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        BarDot.Fill = B("Muted");
        BarState.Text = reason;
        BarAlarm.Visibility = Visibility.Collapsed;
        NarrowItems.Children.Clear();
        NarrowItems.Children.Add(new TextBlock { Text = "служба недоступна — защита не работает", Foreground = B("Crit"), FontSize = 11, TextWrapping = TextWrapping.Wrap });
        NarrowDot.Fill = B("Muted");
        NarrowState.Text = reason;
        NarrowAlarm.Visibility = Visibility.Collapsed;
        AlarmPanel.Visibility = Visibility.Collapsed;
        Footer.Text = $"Защита не работает, пока служба PumpGuard не запущена ({_api.BaseAddress}).";
        Footer.Visibility = Visibility.Visible;
        if (_inAlarm) Topmost = _settings.Topmost || _dockedEdge is not null;
        _inAlarm = false;
    }

    private (PointCollection?, string?) Spark(string key, string unit, double width, double height) =>
        Sparkline.Build(_series.Get(key), DateTimeOffset.Now, _series.Window, width, height, Sparkline.MinSpan(unit),
            v => unit == "RPM" ? $"{v:0} об/мин" : Format(v, unit));

    /// <summary>Status dot only when something is off; a normal reading stays unmarked.</summary>
    private Brush LevelDot(string level) => level switch
    {
        "warn" => B("Warn"), "shutdown" => B("Hot"), "critical" => B("Crit"), "unknown" => B("Muted"), _ => Brushes.Transparent,
    };

    /// <summary>"21 / 300 W" when the value has a known ceiling, otherwise just the value.</summary>
    private static string FormatExtra(ExtraValue e) => e switch
    {
        { Value: null } => "—",
        { Limit: { } l, Unit: "GB" } => $"{e.Value:0.0} / {l:0.0} GB",
        { Limit: { } l } => $"{e.Value:0} / {l:0} {e.Unit}".TrimEnd(),
        _ => Format(e.Value, e.Unit),
    };

    private static string Format(double? v, string unit) => v switch
    {
        null => "—",
        _ when unit == "GB" => $"{v:0.0} GB",
        _ when unit == "W" => $"{v:0} W",
        _ => $"{v:0} {unit}".TrimEnd(),
    };

    private async void OnCancel(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = BarCancelButton.IsEnabled = NarrowCancelButton.IsEnabled = false;
        try
        {
            if (!await _api.CancelShutdownAsync())
                MessageBox.Show(this, "Служба не приняла отмену.", "PumpGuard", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось связаться со службой: " + ex.Message, "PumpGuard", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            CancelButton.IsEnabled = BarCancelButton.IsEnabled = NarrowCancelButton.IsEnabled = true;
            await RefreshAsync();
        }
    }

    private async void OnMenuOpened(object sender, RoutedEventArgs e)
    {
        TopmostItem.IsChecked = _settings.Topmost;
        ViewCompactItem.IsChecked = _settings.View == ViewMode.Compact;
        ViewNormalItem.IsChecked = _settings.View == ViewMode.Normal;
        ViewWideItem.IsChecked = _settings.View == ViewMode.Wide;
        ViewBarTopItem.IsChecked = _settings.View == ViewMode.Bar && _settings.BarEdge == BarEdge.Top;
        ViewBarBottomItem.IsChecked = _settings.View == ViewMode.Bar && _settings.BarEdge == BarEdge.Bottom;
        ViewBarLeftItem.IsChecked = _settings.View == ViewMode.Bar && _settings.BarEdge == BarEdge.Left;
        ViewBarRightItem.IsChecked = _settings.View == ViewMode.Bar && _settings.BarEdge == BarEdge.Right;
        BarReserveItem.IsChecked = _settings.BarReserve;
        SideNarrowItem.IsChecked = SideWidth < NarrowBelow;
        AutostartItem.IsChecked = Autostart.IsEnabled;
        PresetMenu.Items.Clear();
        try
        {
            var fans = await _api.GetFansAsync();
            if (fans is null) return;
            if (!fans.Enabled)
            {
                PresetMenu.Items.Add(new MenuItem { Header = "Управление выключено в config.json", IsEnabled = false });
                return;
            }
            foreach (var name in fans.Presets.Keys)
            {
                var item = new MenuItem { Header = name, IsCheckable = true, IsChecked = name == fans.ActivePreset };
                item.Click += async (_, _) => { await _api.SetPresetAsync(name); await RefreshAsync(); };
                PresetMenu.Items.Add(item);
            }
        }
        catch
        {
            PresetMenu.Items.Add(new MenuItem { Header = "Служба недоступна", IsEnabled = false });
        }
    }

    private void OnOpenFans(object sender, RoutedEventArgs e) => OpenFans();

    public void OpenFans()
    {
        if (_fansWindow is { IsLoaded: true })
        {
            _fansWindow.Activate();
            return;
        }
        _fansWindow = new FansWindow(_api);
        _fansWindow.Show();
    }

    private void OnToggleTopmost(object sender, RoutedEventArgs e)
    {
        _settings.Topmost = TopmostItem.IsChecked;
        _settings.Save();
        Topmost = _settings.Topmost || _inAlarm;
    }

    private void OnToggleAutostart(object sender, RoutedEventArgs e)
    {
        try { Autostart.Set(AutostartItem.IsChecked); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "PumpGuard", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void OnOpenApi(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(new Uri(_api.BaseAddress, "/api/status").ToString()) { UseShellExecute = true });

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            // Double-click: strip or bar -> the last full view; full view -> strip.
            SetView(_settings.View is ViewMode.Compact or ViewMode.Bar ? _settings.LastFullView : ViewMode.Compact);
            return;
        }
        if (_dockedEdge is not null)
        {
            // Docked: the inner edge resizes, anywhere else drags the panel to another screen edge.
            _dockDrag = OnGrip() ? DockDrag.Resize : DockDrag.Move;
            _dragStart = CursorDip();
            CaptureMouse();
            e.Handled = true;
            return;
        }
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    /// <summary>
    /// True when the cursor is over the few pixels along the panel's inner edge (the side facing the desktop).
    /// Uses screen coordinates against the docked rectangle, which stay right while the window is being moved.
    /// </summary>
    private bool OnGrip()
    {
        if (_barRect is not { } r) return false;
        var c = CursorDip();
        return _dockedEdge switch
        {
            BarEdge.Right => c.X <= r.Left + GripSize,
            BarEdge.Left => c.X >= r.Right - GripSize,
            BarEdge.Top => c.Y >= r.Bottom - GripSize,
            BarEdge.Bottom => c.Y <= r.Top + GripSize,
            _ => false,
        };
    }

    private Point CursorDip()
    {
        GetCursorPos(out var p);
        var dpi = VisualTreeHelper.GetDpi(this);
        return new Point(p.X / dpi.DpiScaleX, p.Y / dpi.DpiScaleY);
    }

    private void OnDockedMouseMove(object sender, MouseEventArgs e)
    {
        if (_dockedEdge is not { } edge || _barRect is not { } r) return;

        if (_dockDrag == DockDrag.None)
        {
            Cursor = OnGrip() ? (SideEdge ? Cursors.SizeWE : Cursors.SizeNS) : null;
            return;
        }

        var c = CursorDip();
        if (_dockDrag == DockDrag.Resize)
        {
            // Live preview by resizing the window only; the shell reservation is updated on release.
            switch (edge)
            {
                case BarEdge.Right: _settings.SideWidth = Math.Clamp(r.Right - c.X, 90, 600); break;
                case BarEdge.Left: _settings.SideWidth = Math.Clamp(c.X - r.Left, 90, 600); break;
                case BarEdge.Top: _settings.BarHeight = Math.Clamp(c.Y - r.Top, 26, 48); break;
                case BarEdge.Bottom: _settings.BarHeight = Math.Clamp(r.Bottom - c.Y, 26, 48); break;
            }
            PlaceDocked(edge switch
            {
                BarEdge.Right => new Rect(r.Right - SideWidth, r.Top, SideWidth, r.Height),
                BarEdge.Left => new Rect(r.Left, r.Top, SideWidth, r.Height),
                BarEdge.Top => new Rect(r.Left, r.Top, r.Width, BarHeight),
                _ => new Rect(r.Left, r.Bottom - BarHeight, r.Width, BarHeight),
            });
            SwitchSideLayout();
            return;
        }

        // Move: once past a small threshold, jump to whichever screen edge the cursor is closest to.
        if ((c - _dragStart).Length < MoveThreshold) return;
        Cursor = Cursors.SizeAll;
        double w = SystemParameters.PrimaryScreenWidth, h = SystemParameters.PrimaryScreenHeight;
        var nearest = new[] { (BarEdge.Left, c.X), (BarEdge.Right, w - c.X), (BarEdge.Top, c.Y), (BarEdge.Bottom, h - c.Y) }
            .MinBy(x => x.Item2).Item1;
        if (nearest != _settings.BarEdge)
        {
            _settings.BarEdge = nearest;
            Dock();
            ApplyView();
        }
    }

    /// <summary>Tiles below NarrowBelow, the full card above: switches as soon as the drag crosses it.</summary>
    private void SwitchSideLayout()
    {
        var narrow = Narrow;
        if ((NarrowContent.Visibility == Visibility.Visible) == narrow) return;
        NarrowContent.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
        FullContent.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        Card.Padding = narrow ? new Thickness(10, 10, 8, 10) : new Thickness(16, 12, 16, 12);
    }

    private void OnDockedMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dockDrag == DockDrag.None) return;
        var resized = _dockDrag == DockDrag.Resize;
        _dockDrag = DockDrag.None;
        ReleaseMouseCapture();
        Cursor = null;
        _settings.Save();
        if (resized)
        {
            Dock(); // take the new thickness in the shell's reservation
            ApplyView();
            _ = RefreshAsync();
        }
    }

    /// <summary>
    /// Puts the docked window exactly on <paramref name="r"/> (DIPs): size-to-content off, HWND moved directly.
    /// </summary>
    private void PlaceDocked(Rect r)
    {
        _docking = true;
        // One atomic move+resize. Setting Left, then Width, would briefly push a 300 px window past the screen
        // edge, and Windows nudges it back — which is how WPF's position and the real one drifted apart.
        SizeToContent = SizeToContent.Manual;
        ClearValue(WidthProperty);
        ClearValue(HeightProperty);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var dpi = VisualTreeHelper.GetDpi(this);
        SetWindowPos(hwnd, IntPtr.Zero,
            (int)Math.Round(r.Left * dpi.DpiScaleX), (int)Math.Round(r.Top * dpi.DpiScaleY),
            (int)Math.Round(r.Width * dpi.DpiScaleX), (int)Math.Round(r.Height * dpi.DpiScaleY),
            0x0004 | 0x0010); // SWP_NOZORDER | SWP_NOACTIVATE
        _docking = false;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out PointI point);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct PointI { public int X, Y; }

    private void OnMoved(object? sender, EventArgs e)
    {
        if (_docking || _dockedEdge is not null) return;
        _settings.Left = Left;
        _settings.Top = Top;
        _settings.Save();
    }

    private void PlaceWindow()
    {
        var area = SystemParameters.WorkArea;
        var virt = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (_settings.Left is { } l && _settings.Top is { } t && virt.Contains(new Point(l + 40, t + 20)))
        {
            Left = l;
            Top = t;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = area.Right - 290;
            Top = area.Top + 20;
        }
    }
}
