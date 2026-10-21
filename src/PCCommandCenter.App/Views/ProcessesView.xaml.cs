using System.Windows.Controls;
using System.Windows.Input;
using PCCommandCenter.App.ViewModels;

namespace PCCommandCenter.App.Views;

public partial class ProcessesView : UserControl
{
    public ProcessesView() => InitializeComponent();

    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ProcessesViewModel vm && vm.Selected != null) vm.ShowDetailsCommand.Execute(vm.Selected);
    }
}
