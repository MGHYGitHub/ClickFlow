using System.Globalization;
using System.Windows.Data;
using ClickFlow.Core;
namespace ClickFlow.Desktop;
public sealed class ActionLabelConverter : IValueConverter
{
    public object Convert(object value,Type targetType,object parameter,CultureInfo culture) => value is ActionKind kind ? kind switch {
        ActionKind.Click=>"单击",ActionKind.DoubleClick=>"双击",ActionKind.Move=>"移动鼠标",ActionKind.Scroll=>"滚轮",ActionKind.Hotkey=>"组合键",ActionKind.Wait=>"等待",ActionKind.MouseDown=>"按下鼠标",ActionKind.MouseUp=>"释放鼠标",ActionKind.KeyDown=>"按下按键",ActionKind.KeyUp=>"释放按键",_=>"未知动作"
    }:"";
    public object ConvertBack(object value,Type targetType,object parameter,CultureInfo culture)=>Binding.DoNothing;
}
