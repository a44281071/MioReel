using Avalonia.Controls;
using MioReel.Desktop.ViewModels;

namespace MioReel.Desktop.Views;

public partial class SearchView : UserControl
{
    public SearchView()
    {
        InitializeComponent();
        SizeChanged += (_, e) =>
        {
            if (DataContext is MainWindowViewModel vm)
                vm.UpdateSearchColumns(Math.Max(310, e.NewSize.Width - 20));
        };
    }
}
