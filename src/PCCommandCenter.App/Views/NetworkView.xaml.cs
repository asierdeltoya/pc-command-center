using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using PCCommandCenter.App.ViewModels;

namespace PCCommandCenter.App.Views;

public partial class NetworkView : UserControl
{
    private NetworkViewModel? _vm;

    public NetworkView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm != null) _vm.PropertyChanged -= OnVmChanged;
            _vm = DataContext as NetworkViewModel;
            if (_vm != null) _vm.PropertyChanged += OnVmChanged;
            ShowTab();
        };
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NetworkViewModel.SelectedTab)) ShowTab();
    }

    private void ShowTab()
    {
        var tabs = new FrameworkElement[] { PingTab, DnsTab, TraceTab, InterfacesTab, ConnectionsTab, ArpTab };
        int selected = _vm?.SelectedTab ?? 0;
        for (int i = 0; i < tabs.Length; i++) tabs[i].Visibility = i == selected ? Visibility.Visible : Visibility.Collapsed;
    }
}
