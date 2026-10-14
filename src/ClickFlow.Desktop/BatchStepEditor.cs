using System.Windows;
using System.Windows.Controls;
using ClickFlow.Core;

namespace ClickFlow.Desktop;

public sealed class BatchStepEditor : Window
{
    public int? DelayMs { get; private set; }
    public int? Count { get; private set; }
    public int? IntervalMs { get; private set; }
    private readonly CheckBox _delayEnabled, _countEnabled, _intervalEnabled;
    private readonly TextBox _delay, _count, _interval;
    public BatchStepEditor(Window owner, int selectedCount)
    {
        Owner = owner; Title = $"批量设置 · 已选 {selectedCount} 个步骤"; Width = 430; Height = 390; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false; Resources = owner.Resources;
        SetResourceReference(BackgroundProperty, "AppBackground"); SetResourceReference(ForegroundProperty, "TextPrimary"); FontFamily = new("Microsoft YaHei UI");
        var root = new DockPanel { Margin = new Thickness(24) }; Content = root;
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(new Button { Content = "取消", MinWidth = 82, IsCancel = true }); var save = new Button { Content = "应用到所选步骤", MinWidth = 130, Margin = new Thickness(10, 0, 0, 0), IsDefault = true }; save.Click += Save; footer.Children.Add(save);
        var panel = new StackPanel(); root.Children.Add(panel); panel.Children.Add(new TextBlock { Text = "批量设置步骤", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) }); panel.Children.Add(new TextBlock { Text = "勾选要修改的字段，未勾选的字段保持原值。", Foreground = (System.Windows.Media.Brush)FindResource("TextSecondary"), Margin = new Thickness(0, 0, 0, 18) });
        _delay = Field(panel, "执行前等待（秒）", "0", out _delayEnabled); _count = Field(panel, "此步骤执行次数", "1", out _countEnabled); _interval = Field(panel, "重复执行间隔（秒）", "0", out _intervalEnabled);
    }
    private static TextBox Field(Panel panel, string label, string value, out CheckBox enabled) { var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) }; enabled = new CheckBox { Content = label, VerticalAlignment = VerticalAlignment.Center, Width = 180 }; row.Children.Add(enabled); var box = new TextBox { Text = value, MinHeight = 30, Width = 140, IsEnabled = false }; enabled.Checked += (_, _) => box.IsEnabled = true; enabled.Unchecked += (_, _) => box.IsEnabled = false; DockPanel.SetDock(box, Dock.Right); row.Children.Add(box); panel.Children.Add(row); return box; }
    private void Save(object? sender, RoutedEventArgs e)
    {
        try { DelayMs = Parse(_delay, _delayEnabled, "等待"); Count = Parse(_count, _countEnabled, "次数"); IntervalMs = Parse(_interval, _intervalEnabled, "间隔"); if (DelayMs is null && Count is null && IntervalMs is null) throw new FormatException("至少勾选一个要修改的字段。"); DialogResult = true; }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "批量设置", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
    private static int? Parse(TextBox box, CheckBox enabled, string name) { if (!enabled.IsChecked.GetValueOrDefault()) return null; if (!int.TryParse(box.Text, out var value)) throw new FormatException(name + "必须是整数。"); return value; }
}

