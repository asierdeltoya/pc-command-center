using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using PCCommandCenter.Core;
using PCCommandCenter.Settings;

namespace PCCommandCenter.App.Services;

public sealed partial class NotificationItem : ObservableObject
{
    public Guid Id { get; } = Guid.NewGuid();
    public NotificationLevel Level { get; init; }
    public string Title { get; init; } = "";
    public string Message { get; init; } = "";
    public DateTime Time { get; init; } = DateTime.Now;
    public string Glyph => Level switch
    {
        NotificationLevel.Success => "",
        NotificationLevel.Warning => "",
        NotificationLevel.Error => "",
        _ => "",
    };

    [ObservableProperty] private bool _isLeaving;
}

/// <summary>
/// In-app notification system: transient toasts plus a dismissable notification center.
/// Thread-safe; supports de-duplication with cooldowns so recurring alerts don't spam the user.
/// </summary>
public sealed partial class NotificationCenter : ObservableObject, INotificationService
{
    private readonly ISettingsService _settings;
    private readonly Dictionary<string, DateTime> _lastByKey = [];
    private readonly Dispatcher _dispatcher;

    public NotificationCenter(ISettingsService settings)
    {
        _settings = settings;
        _dispatcher = Application.Current.Dispatcher;
    }

    public ObservableCollection<NotificationItem> Items { get; } = [];
    public ObservableCollection<NotificationItem> Toasts { get; } = [];

    [ObservableProperty] private int _unreadCount;

    /// <summary>Raised for alerts while the main window is hidden so the tray can show a balloon.</summary>
    public event Action<NotificationItem>? Raised;

    public void Notify(NotificationLevel level, string title, string message, string? dedupKey = null, TimeSpan? cooldown = null)
    {
        if (dedupKey != null)
        {
            lock (_lastByKey)
            {
                if (_lastByKey.TryGetValue(dedupKey, out var last) && DateTime.Now - last < (cooldown ?? TimeSpan.FromMinutes(10))) return;
                _lastByKey[dedupKey] = DateTime.Now;
            }
        }

        var item = new NotificationItem { Level = level, Title = title, Message = message };
        AppLog.Info($"Notification [{level}] {title}: {message}");
        _dispatcher.BeginInvoke(() =>
        {
            Items.Insert(0, item);
            while (Items.Count > 100) Items.RemoveAt(Items.Count - 1);
            UnreadCount++;
            if (_settings.Current.NotificationsEnabled || level == NotificationLevel.Error)
            {
                Toasts.Add(item);
                while (Toasts.Count > 4) Toasts.RemoveAt(0);
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(level >= NotificationLevel.Warning ? 7 : 4.5) };
                timer.Tick += (_, _) => { timer.Stop(); DismissToast(item); };
                timer.Start();
            }
            Raised?.Invoke(item);
        });
    }

    public void DismissToast(NotificationItem item)
    {
        if (!Toasts.Contains(item)) return;
        item.IsLeaving = true;
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        t.Tick += (_, _) => { t.Stop(); Toasts.Remove(item); };
        t.Start();
    }

    public void Dismiss(NotificationItem item)
    {
        Items.Remove(item);
        Toasts.Remove(item);
    }

    public void ClearAll()
    {
        Items.Clear();
        Toasts.Clear();
        UnreadCount = 0;
    }

    public void MarkRead() => UnreadCount = 0;
}
