using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using PumpGuard.Core;

namespace PumpGuard.Widget;

public partial class FansWindow : Window
{
    private readonly ApiClient _api;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<string, (TextBlock Info, CheckBox Manual, Slider Slider)> _rows = new();
    private bool _loadingPresets;

    public FansWindow(ApiClient api)
    {
        InitializeComponent();
        _api = api;
        Loaded += async (_, _) => { await LoadAsync(); _timer.Start(); };
        _timer.Tick += async (_, _) => await RefreshAsync();
        Closed += (_, _) => _timer.Stop();
    }

    private async Task LoadAsync()
    {
        FansInfo? info;
        try { info = await _api.GetFansAsync(); }
        catch (Exception ex)
        {
            Hint.Text = "Служба недоступна: " + ex.Message;
            return;
        }
        if (info is null) return;

        _loadingPresets = true;
        PresetBox.ItemsSource = info.Presets.Keys.ToList();
        PresetBox.SelectedItem = info.ActivePreset;
        PresetBox.IsEnabled = info.Enabled;
        _loadingPresets = false;
        Hint.Text = info.Enabled ? "" : "Управление выключено: FanControl:Enabled = false";

        Channels.Children.Clear();
        _rows.Clear();
        if (info.Fans.Count == 0)
            Channels.Children.Add(new TextBlock { Text = "Каналы не настроены (FanControl:Channels в config.json).", Foreground = Brushes.Gray });

        foreach (var fan in info.Fans)
            AddRow(fan, info.Manual.TryGetValue(fan.Name, out var pct) ? pct : null);
    }

    private void AddRow(FanStatus fan, double? manual)
    {
        var name = new TextBlock { Text = fan.Name, FontWeight = FontWeights.SemiBold };
        var info = new TextBlock { Foreground = Brushes.Gray, FontSize = 11, Margin = new Thickness(0, 2, 0, 4) };
        var check = new CheckBox { Content = "Вручную", Foreground = Brushes.White, IsChecked = manual is not null, IsEnabled = fan.Controllable, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        var slider = new Slider { Minimum = 0, Maximum = 100, Width = 220, Value = manual ?? fan.TargetPercent ?? 50, IsEnabled = fan.Controllable && manual is not null, TickFrequency = 5, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
        var value = new TextBlock { Width = 40, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        value.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Value") { Source = slider, StringFormat = "{0:0}%" });

        check.Click += async (_, _) =>
        {
            slider.IsEnabled = check.IsChecked == true;
            await _api.SetManualAsync(fan.Name, check.IsChecked == true ? slider.Value : null);
        };
        slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(async (_, _) => await _api.SetManualAsync(fan.Name, slider.Value)));
        slider.PreviewMouseLeftButtonUp += async (_, _) => { if (check.IsChecked == true) await _api.SetManualAsync(fan.Name, slider.Value); };

        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        controls.Children.Add(check);
        controls.Children.Add(slider);
        controls.Children.Add(value);

        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(name);
        panel.Children.Add(info);
        panel.Children.Add(controls);
        Channels.Children.Add(panel);

        _rows[fan.Name] = (info, check, slider);
        UpdateInfo(fan);
    }

    private async Task RefreshAsync()
    {
        try
        {
            var info = await _api.GetFansAsync();
            if (info is null) return;
            if (!PresetBox.IsDropDownOpen && !Equals(PresetBox.SelectedItem, info.ActivePreset))
            {
                _loadingPresets = true;
                PresetBox.ItemsSource = info.Presets.Keys.ToList();
                PresetBox.SelectedItem = info.ActivePreset;
                _loadingPresets = false;
            }
            foreach (var fan in info.Fans) UpdateInfo(fan);
        }
        catch { /* the main widget already reports an unreachable service */ }
    }

    private void UpdateInfo(FanStatus f)
    {
        if (!_rows.TryGetValue(f.Name, out var row)) return;
        var mode = f.Mode switch
        {
            "Curve" => "по кривой", "Fixed" => "фиксированная", "Manual" => "вручную", "Bios" => "BIOS",
            "Max" => "МАКСИМУМ (защита)", _ => "только мониторинг",
        };
        var rpm = f.Rpm is { } r ? $"{r:0} об/мин" : "— об/мин";
        var pct = (f.TargetPercent ?? f.Percent) is { } p ? $" · {p:0}%" : "";
        var temp = f.SourceTemp is { } t ? $" · источник {t:0} °C" : "";
        row.Info.Text = $"{rpm}{pct} · {mode}{temp}";
        // Outside manual mode the slider mirrors what the service is currently applying.
        if (row.Manual.IsChecked != true && (f.TargetPercent ?? f.Percent) is { } current) row.Slider.Value = current;
    }

    private async void OnPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingPresets || PresetBox.SelectedItem is not string name) return;
        if (!await _api.SetPresetAsync(name))
            MessageBox.Show(this, "Служба не приняла пресет.", "PumpGuard", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
