using Avalonia.Controls;
using Avalonia.Input;

namespace Stelliberty.Desktop.Controls;

public sealed class AppComboBox : ComboBox
{
    protected override Type StyleKeyOverride => typeof(ComboBox);

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        // 收起且获焦时将滚轮事件留给外层滚动容器。
        if (IsDropDownOpen || !IsFocused)
        {
            base.OnPointerWheelChanged(e);
        }
    }
}
