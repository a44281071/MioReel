using Avalonia.Controls;
using Avalonia.Input;
using MioReel.Desktop.ViewModels;

namespace MioReel.Desktop.Controls;

public partial class PlayerTransportBar : UserControl
{
    public PlayerTransportBar() => InitializeComponent();

    private void OnVolumeBtnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not PlayerViewModel player) return;
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed || props.IsMiddleButtonPressed)
        {
            player.ToggleMute();
            e.Handled = true;
        }
    }

    private void OnVolumeBtnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (DataContext is not PlayerViewModel player || Math.Abs(e.Delta.Y) < 0.01) return;
        var steps = Math.Clamp(e.Delta.Y, -3, 3);
        player.StepVolume(steps * 0.05);
        e.Handled = true;
    }
}
