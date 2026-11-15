using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LegacyGameLauncher.Services;

namespace LegacyGameLauncher;

/// <summary>Controllers tab: live button/axis tester (Raw Input HID, read-only).</summary>
public partial class MainWindow
{
    RawGamepadMonitor? _tester;
    DispatcherTimer? _testerTimer;

    sealed class PadView
    {
        public Dictionary<(ushort, ushort), (ProgressBar Bar, TextBlock Text)> Axes = new();
        public Dictionary<int, Border> Buttons = new();
        public TextBlock Status = new();
    }
    readonly Dictionary<IntPtr, PadView> _padViews = new();

    void Tester_Click(object sender, RoutedEventArgs e)
    {
        if (_tester != null) { StopTester(); return; }
        try
        {
            _tester = new RawGamepadMonitor();
            _tester.Start(this);
            _testerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _testerTimer.Tick += (_, _) => RenderTester();
            _testerTimer.Start();
            TesterButton.Content = "Stop tester";
            TesterButton.Icon = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.Stop24);
            TesterPanel.Children.Clear(); _padViews.Clear();
            TesterPanel.Children.Add(new TextBlock { Text = "Waiting for input - press a button on the controller…", Style = (Style)FindResource("Hint") });
        }
        catch (Exception ex)
        {
            _tester = null;
            TesterPanel.Children.Clear();
            TesterPanel.Children.Add(new TextBlock { Text = "Could not start the tester: " + ex.Message, Style = (Style)FindResource("Hint") });
        }
    }

    void StopTester()
    {
        _testerTimer?.Stop(); _testerTimer = null;
        _tester?.Stop(); _tester = null;
        TesterButton.Content = "Start tester";
        TesterButton.Icon = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.Play24);
    }

    protected override void OnClosed(EventArgs e)
    {
        StopTester();
        base.OnClosed(e);
    }

    void RenderTester()
    {
        if (_tester == null) return;
        var pads = _tester.Pads;
        if (pads.Count == 0) return;
        if (_padViews.Count == 0) TesterPanel.Children.Clear();

        foreach (var pad in pads)
        {
            if (!_padViews.TryGetValue(pad.Handle, out var view))
            {
                view = BuildPadView(pad);
                _padViews[pad.Handle] = view;
            }
            foreach (var a in pad.Axes)
            {
                if (!view.Axes.TryGetValue((a.Page, a.Usage), out var ui)) continue;
                if (a.IsHat) { ui.Text.Text = a.HatText; continue; }
                ui.Bar.Value = a.Value * 100;
                ui.Text.Text = $"{a.Value * 100:0}%  (raw {a.Raw})";
            }
            foreach (var (n, b) in view.Buttons)
            {
                bool on = pad.Pressed.Contains(n);
                b.Background = on ? (Brush)FindResource("AccentFillColorDefaultBrush") : (Brush)FindResource("ControlFillColorSecondaryBrush");
                ((TextBlock)b.Child).Foreground = on ? (Brush)FindResource("TextOnAccentFillColorPrimaryBrush") : (Brush)FindResource("TextFillColorPrimaryBrush");
            }
            view.Status.Text = pad.Pressed.Count == 0
                ? "Pressed: none"
                : "Pressed: " + string.Join(", ", pad.Pressed.OrderBy(x => x).Select(x => "Button " + x + KnownButton(pad.Name, x)));
        }
    }

    static string KnownButton(string padName, int n)
    {
        bool ps = padName.Contains("Wireless Controller", StringComparison.OrdinalIgnoreCase) || padName.Contains("DualShock", StringComparison.OrdinalIgnoreCase) || padName.Contains("DualSense", StringComparison.OrdinalIgnoreCase);
        bool xbox = padName.Contains("Xbox", StringComparison.OrdinalIgnoreCase) || padName.Contains("XINPUT", StringComparison.OrdinalIgnoreCase);
        string? label = ps ? n switch { 1 => "Square", 2 => "Cross", 3 => "Circle", 4 => "Triangle", 5 => "L1", 6 => "R1", 7 => "L2", 8 => "R2", 9 => "Share", 10 => "Options", 11 => "L3", 12 => "R3", 13 => "PS", 14 => "Touchpad", _ => null }
                      : xbox ? n switch { 1 => "A", 2 => "B", 3 => "X", 4 => "Y", 5 => "LB", 6 => "RB", 7 => "Back", 8 => "Start", 9 => "LS", 10 => "RS", _ => null }
                      : null;
        return label == null ? "" : $" ({label})";
    }

    PadView BuildPadView(PadState pad)
    {
        var view = new PadView();
        var card = new Border
        {
            Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 10), CornerRadius = new CornerRadius(6),
            Background = (Brush)FindResource("ControlFillColorDefaultBrush"),
            BorderBrush = (Brush)FindResource("CardStrokeColorDefaultBrush"), BorderThickness = new Thickness(1),
        };
        var root = new StackPanel();
        root.Children.Add(new TextBlock { Text = pad.Name, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 0, 0, 8) });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        int row = 0;
        foreach (var a in pad.Axes)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = a.Name, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 8, 3) };
            Grid.SetRow(label, row); grid.Children.Add(label);
            var bar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 6, VerticalAlignment = VerticalAlignment.Center, Visibility = a.IsHat ? Visibility.Collapsed : Visibility.Visible };
            Grid.SetRow(bar, row); Grid.SetColumn(bar, 1); grid.Children.Add(bar);
            var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Foreground = (Brush)FindResource("TextFillColorSecondaryBrush") };
            Grid.SetRow(text, row); Grid.SetColumn(text, a.IsHat ? 1 : 2); grid.Children.Add(text);
            view.Axes[(a.Page, a.Usage)] = (bar, text);
            row++;
        }
        root.Children.Add(grid);

        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 6) };
        for (int n = 1; n <= pad.ButtonCount; n++)
        {
            var b = new Border
            {
                Width = 34, Height = 28, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 6, 6),
                Background = (Brush)FindResource("ControlFillColorSecondaryBrush"),
                Child = new TextBlock { Text = n.ToString(), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                ToolTip = "Button " + n + KnownButton(pad.Name, n),
            };
            buttons.Children.Add(b);
            view.Buttons[n] = b;
        }
        root.Children.Add(buttons);
        view.Status = new TextBlock { Foreground = (Brush)FindResource("TextFillColorSecondaryBrush"), TextWrapping = TextWrapping.Wrap };
        root.Children.Add(view.Status);
        card.Child = root;
        TesterPanel.Children.Add(card);
        return view;
    }
}
