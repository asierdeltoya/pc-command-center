using System.Windows;
using System.Windows.Controls;
using PCCommandCenter.App.ViewModels;

namespace PCCommandCenter.App.Views;

public partial class LogAnalyzerView : UserControl
{
    public LogAnalyzerView() => InitializeComponent();

    private LogAnalyzerViewModel? Vm => DataContext as LogAnalyzerViewModel;

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && Vm is { } vm) vm.IsDragOver = true;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        if (Vm is { } vm) vm.IsDragOver = false;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (Vm is not { } vm) return;
        vm.IsDragOver = false;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            await vm.AnalyzeFileAsync(files[0]);
    }
}
