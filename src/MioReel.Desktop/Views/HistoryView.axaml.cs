using Avalonia.Controls;
using MioReel.Desktop.ViewModels;

namespace MioReel.Desktop.Views;

public partial class HistoryView : UserControl
{
    public HistoryView()
    {
        InitializeComponent();
        SizeChanged += (_, e) =>
        {
            if (DataContext is MainWindowViewModel vm)
                vm.UpdateHistoryColumns(Math.Max(330, e.NewSize.Width - 20));
        };
    }
}
