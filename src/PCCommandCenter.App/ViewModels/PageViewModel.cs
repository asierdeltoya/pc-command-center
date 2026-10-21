using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using PCCommandCenter.App.Services;
using PCCommandCenter.Core;

namespace PCCommandCenter.App.ViewModels;

/// <summary>Base class for pages: tracks visibility so pages only refresh while they are on screen.</summary>
public abstract partial class PageViewModel : ObservableObject, IPage
{
    protected PageViewModel()
    {
        Dispatcher = Application.Current.Dispatcher;
    }

    protected Dispatcher Dispatcher { get; }

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _errorMessage;

    /// <summary>Set by the shell while the main window is hidden in the tray.</summary>
    public static bool WindowVisible { get; set; } = true;

    public virtual void OnNavigatedTo(object? parameter) => IsActive = true;

    public virtual void OnNavigatedFrom() => IsActive = false;

    protected void Ui(Action action)
    {
        if (Dispatcher.CheckAccess()) action();
        else Dispatcher.BeginInvoke(action, DispatcherPriority.Background);
    }

    /// <summary>Runs a background operation with busy state and friendly error reporting.</summary>
    protected async Task RunBusyAsync(Func<Task> work, string failureMessage)
    {
        IsBusy = true;
        ErrorMessage = null;
        try { await work(); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Error(failureMessage, ex);
            ErrorMessage = Loc.T(failureMessage);
        }
        finally { IsBusy = false; }
    }
}
