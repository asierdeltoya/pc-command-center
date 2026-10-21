using System.Windows;
using System.Windows.Controls;
using PCCommandCenter.App.ViewModels;

namespace PCCommandCenter.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    // PasswordBox.Password is intentionally not bindable; copy it only when the user saves.
    private void KeyBox_PasswordChanged(object sender, RoutedEventArgs e) { }

    private void SaveKey_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        vm.ApiKeyInput = KeyBox.Password;
        vm.SaveApiKeyCommand.Execute(null);
        KeyBox.Clear();
    }
}
