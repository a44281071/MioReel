using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MioReel.Desktop.Controls;

public partial class PlayerMiniTopBar : UserControl
{
    public PlayerMiniTopBar() => InitializeComponent();

    private void OnCloseWindowClick(object? sender, RoutedEventArgs e)
        => (TopLevel.GetTopLevel(this) as Window)?.Close();
}
