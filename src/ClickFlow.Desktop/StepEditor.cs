using System.Windows;
using System.Windows.Controls;
using ClickFlow.Core;

namespace ClickFlow.Desktop;

public sealed class StepEditor : Window
{
    public ActionStep Result { get; private set; }
    private readonly TextBox _name, _x, _y, _delay, _count, _interval, _wheel, _keys, _vk;
    private readonly ComboBox _kind, _button;
    private readonly CheckBox _current, _enabled;
    private readonly TextBlock _error;
    private static readonly string[] Labels = ["单击", "双击", "移动鼠标", "滚轮", "键盘组合", "等待", "按下鼠标", "释放鼠标", "按下按键", "释放按键"];
    public StepEditor(Window owner, ActionStep step)
    {
        Owner=owner; Result=step.Clone(); Title="编辑步骤 · ClickFlow"; Width=560; Height=720; MinHeight=620; WindowStartupLocation=WindowStartupLocation.CenterOwner; ShowInTaskbar=false;
        Resources=owner.Resources;
        SetResourceReference(BackgroundProperty,"AppBackground"); SetResourceReference(ForegroundProperty,"TextPrimary"); FontFamily=new("Microsoft YaHei UI"); FontSize=13;
        var root = new DockPanel { Margin=new(24) }; Content=root;
        var footer=new StackPanel { Orientation=Orientation.Horizontal, HorizontalAlignment=HorizontalAlignment.Right, Margin=new(0,16,0,0) }; DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        var cancel=new Button { Content="取消",MinWidth=88,IsCancel=true }; footer.Children.Add(cancel);
        var save=new Button { Content="保存步骤",MinWidth=110,Margin=new(10,0,0,0),IsDefault=true }; save.Click+=Save;footer.Children.Add(save);
        var panel=new StackPanel(); root.Children.Add(new ScrollViewer { Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto });
        panel.Children.Add(new TextBlock { Text="每一步，都按你的方式",FontSize=23,FontWeight=FontWeights.SemiBold,Margin=new(0,0,0,18) });
        _name=Field(panel,"步骤名称",step.Name);
        _enabled=new CheckBox { Content="启用此步骤",IsChecked=step.Enabled,Margin=new(0,0,0,12) };panel.Children.Add(_enabled);
        _kind=new ComboBox { ItemsSource=Labels, SelectedIndex=(int)step.Kind,Margin=new(0,0,0,12) };Label(panel,"动作类型");panel.Children.Add(_kind);
        _button=new ComboBox { ItemsSource=new[]{"左键","右键","中键"},SelectedIndex=(int)step.Button,Margin=new(0,0,0,12) };Label(panel,"鼠标按键");panel.Children.Add(_button);
        _current=new CheckBox { Content="使用执行时的鼠标位置",IsChecked=step.UseCurrentPosition,Margin=new(0,0,0,12) };panel.Children.Add(_current);
        var xy=new Grid();xy.ColumnDefinitions.Add(new());xy.ColumnDefinitions.Add(new());var xp=new StackPanel{Margin=new(0,0,8,0)};var yp=new StackPanel{Margin=new(8,0,0,0)};Grid.SetColumn(yp,1);xy.Children.Add(xp);xy.Children.Add(yp);panel.Children.Add(xy);
        _x=Field(xp,"X · 屏幕像素",step.X);_y=Field(yp,"Y · 屏幕像素",step.Y);
        _delay=Field(panel,"执行前等待（ms）",step.DelayMs);_count=Field(panel,"此步骤执行次数",step.Count);_interval=Field(panel,"重复执行间隔（ms）",step.IntervalMs);
        _wheel=Field(panel,"滚轮量（+120 向上，-120 向下）",step.WheelDelta);
        _keys=Field(panel,"键盘组合（例如 Ctrl+Shift+S）",step.Keys);
        _vk=Field(panel,"录制按键代码（Windows VK，1–254）",step.VirtualKey);
        _error=new TextBlock { TextWrapping=TextWrapping.Wrap,Margin=new(0,8,0,0) };_error.SetResourceReference(TextBlock.ForegroundProperty,"DangerBrush");panel.Children.Add(_error);
        _kind.SelectionChanged+=(_,_)=>UpdateFields();UpdateFields();
    }
    private static void Label(Panel p,string text) => p.Children.Add(new TextBlock { Text=text,Margin=new(0,0,0,6) });
    private static TextBox Field(Panel p,string title,object value) { Label(p,title);var t=new TextBox { Text=value.ToString(),Margin=new(0,0,0,12),MinHeight=34 };p.Children.Add(t);return t; }
    private void UpdateFields()
    {
        var k=(ActionKind)_kind.SelectedIndex;bool pointer=k is ActionKind.Click or ActionKind.DoubleClick or ActionKind.Move or ActionKind.Scroll or ActionKind.MouseDown or ActionKind.MouseUp;
        _x.IsEnabled=_y.IsEnabled=_current.IsEnabled=pointer;
        _button.IsEnabled=k is ActionKind.Click or ActionKind.DoubleClick or ActionKind.MouseDown or ActionKind.MouseUp;
        _wheel.IsEnabled=k==ActionKind.Scroll;_keys.IsEnabled=k==ActionKind.Hotkey;_vk.IsEnabled=k is ActionKind.KeyDown or ActionKind.KeyUp;
    }
    private void Save(object sender,RoutedEventArgs e)
    {
        try
        {
            int Parse(TextBox t,string label) => int.TryParse(t.Text,out int n)?n:throw new FormatException(label+"必须是整数。");
            Result=new ActionStep { Name=_name.Text.Trim(),Kind=(ActionKind)_kind.SelectedIndex,Button=(PointerButton)_button.SelectedIndex,UseCurrentPosition=_current.IsChecked==true,X=Parse(_x,"X"),Y=Parse(_y,"Y"),DelayMs=Parse(_delay,"等待"),Count=Parse(_count,"次数"),IntervalMs=Parse(_interval,"间隔"),WheelDelta=Parse(_wheel,"滚轮量"),Keys=_keys.Text.Trim(),VirtualKey=Parse(_vk,"按键代码"),Enabled=Result.Enabled };
            Result.Enabled=true;
            var error=PlanValidator.Validate(new ExecutionPlan([Result],new RunOptions{StartDelayMs=0}));if(error!=null)throw new FormatException(error);
            Result.Enabled=_enabled.IsChecked==true;
            DialogResult=true;
        }
        catch(Exception ex) { _error.Text=ex.Message; }
    }
}
