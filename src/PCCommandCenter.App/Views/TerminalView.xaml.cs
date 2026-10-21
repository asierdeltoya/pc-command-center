using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PCCommandCenter.App.ViewModels;

namespace PCCommandCenter.App.Views;

public partial class TerminalView : UserControl
{
    public TerminalView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) Dispatcher.BeginInvoke(() => Input.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        };
    }

    private TerminalViewModel? Vm => DataContext as TerminalViewModel;

    private void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        switch (e.Key)
        {
            case Key.Enter:
                Input.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                if (vm.RunCommand.CanExecute(null)) vm.RunCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Up:
                vm.HistoryUp(); Input.CaretIndex = Input.Text.Length; e.Handled = true;
                break;
            case Key.Down:
                vm.HistoryDown(); Input.CaretIndex = Input.Text.Length; e.Handled = true;
                break;
            case Key.Escape when vm.IsRunning:
                vm.StopCommand.Execute(null); e.Handled = true;
                break;
            case Key.L when Keyboard.Modifiers == ModifierKeys.Control:
                vm.ClearCommand.Execute(null); e.Handled = true;
                break;
        }
    }
}
