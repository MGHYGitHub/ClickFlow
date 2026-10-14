using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Forms = System.Windows.Forms;
using ClickFlow.Core;

namespace ClickFlow.Desktop;

public sealed class GuideOverlayWindow : Window
{
    public GuideOverlayWindow(Window owner, IReadOnlyList<ActionStep> steps)
    {
        Owner = owner; ShowInTaskbar = false; Topmost = true; WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize; AllowsTransparency = true; Background = new SolidColorBrush(Color.FromArgb(205, 12, 20, 32));
        var bounds = Forms.SystemInformation.VirtualScreen;
        Left = bounds.Left; Top = bounds.Top; Width = bounds.Width; Height = bounds.Height;
        var canvas = new Canvas(); Content = canvas;
        var title = new Border { Background = new SolidColorBrush(Color.FromArgb(235, 20, 30, 45)), CornerRadius = new CornerRadius(10), Padding = new Thickness(18, 12, 18, 12) };
        Canvas.SetLeft(title, 24); Canvas.SetTop(title, 24); canvas.Children.Add(title);
        title.Child = new StackPanel { Children = { new TextBlock { Text = "步骤点位引导", Foreground = Brushes.White, FontSize = 20, FontWeight = FontWeights.SemiBold }, new TextBlock { Text = "按顺序查看点位，按 Esc 退出蒙版", Foreground = Brushes.LightGray, Margin = new Thickness(0, 5, 0, 0) } } };
        var index = 0;
        foreach (var step in steps.Where(s => s.Enabled && !s.UseCurrentPosition && s.Kind is ActionKind.Click or ActionKind.DoubleClick or ActionKind.Move or ActionKind.Scroll or ActionKind.MouseDown or ActionKind.MouseUp))
        {
            index++;
            var dot = new Border { Width = 54, Height = 54, CornerRadius = new CornerRadius(27), Background = new SolidColorBrush(Color.FromRgb(31, 184, 145)), BorderBrush = Brushes.White, BorderThickness = new Thickness(3), ToolTip = step.Name };
            dot.Child = new TextBlock { Text = index.ToString(), Foreground = Brushes.White, FontSize = 21, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
            Canvas.SetLeft(dot, step.X - bounds.Left - 27); Canvas.SetTop(dot, step.Y - bounds.Top - 27); canvas.Children.Add(dot);
            var label = new Border { Background = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)), CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 4, 8, 4) };
            label.Child = new TextBlock { Text = $"{index}. {step.Name}", Foreground = new SolidColorBrush(Color.FromRgb(20, 30, 40)), FontWeight = FontWeights.SemiBold };
            Canvas.SetLeft(label, step.X - bounds.Left + 34); Canvas.SetTop(label, step.Y - bounds.Top - 18); canvas.Children.Add(label);
        }
        if (index == 0)
        {
            var empty = new TextBlock { Text = "没有可显示的固定坐标点位\n请先关闭蒙版并编辑步骤", Foreground = Brushes.White, FontSize = 22, TextAlignment = TextAlignment.Center };
            Canvas.SetLeft(empty, Math.Max(0, bounds.Width / 2 - 180)); Canvas.SetTop(empty, Math.Max(0, bounds.Height / 2 - 30)); canvas.Children.Add(empty);
        }
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Loaded += (_, _) => Focus();
    }
}
