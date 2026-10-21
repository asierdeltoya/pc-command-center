using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using PCCommandCenter.App.Controls;
using PCCommandCenter.App.ViewModels;
using PCCommandCenter.Core;

namespace PCCommandCenter.App.Views;

public partial class WindowsView : UserControl
{
    public WindowsView() => InitializeComponent();

    private void ServiceMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ServiceItem item } button || DataContext is not WindowsViewModel vm) return;
        MenuItem Item(string text, string glyph, ICommand command)
        {
            var mi = new MenuItem { Header = Loc.T(text), Command = command, CommandParameter = item };
            Ui.SetGlyph(mi, glyph);
            return mi;
        }
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        menu.Items.Add(Item("Start", "", vm.StartServiceCommand));
        menu.Items.Add(Item("Stop", "", vm.StopServiceCommand));
        menu.Items.Add(Item("Restart", "", vm.RestartServiceCommand));
        menu.IsOpen = true;
    }
}
