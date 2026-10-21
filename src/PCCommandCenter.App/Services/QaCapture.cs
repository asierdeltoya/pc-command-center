using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PCCommandCenter.App.ViewModels;
using PCCommandCenter.Core;

namespace PCCommandCenter.App.Services;

/// <summary>
/// Quality-assurance mode (<c>--qa-capture &lt;folder&gt;</c>): visits every page, renders the window to PNG
/// files and records any binding/render failure in app.log, then exits. Works even on a locked session.
/// </summary>
public static class QaCapture
{
    public static async Task RunAsync(Window window, FrameworkElement root, NavigationService nav, MainViewModel main, string folder, IReadOnlyList<string> extra)
    {
        Directory.CreateDirectory(folder);
        AppLog.Info($"QA capture started → {folder}");
        await Task.Delay(TimeSpan.FromSeconds(7)); // let telemetry history accumulate a few samples

        int index = 0;
        foreach (var page in NavigationService.Pages.Select(p => p.Key))
        {
            nav.Navigate(page);
            await Task.Delay(page switch
            {
                PageKey.Health => 26000,      // full scan runs on first visit
                PageKey.Processes => 5000,
                PageKey.Windows => 6000,
                PageKey.Startup => 3500,
                _ => 2500,
            });
            Save(root, Path.Combine(folder, $"{++index:00}-{page}.png"));
        }

        if (extra.Contains("functional")) index = await RunFunctionalAsync(root, nav, folder, index);

        if (extra.Contains("palette"))
        {
            nav.Navigate(PageKey.Dashboard);
            await Task.Delay(1500);
            main.Palette.Open();
            main.Palette.Query = "pi";
            await Task.Delay(800);
            Save(root, Path.Combine(folder, $"{++index:00}-Palette.png"));
            main.Palette.Close();
        }
        AppLog.Info("QA capture finished");
    }

    /// <summary>Exercises the real tools end-to-end (network, logs, terminal, storage) and captures the results.</summary>
    private static async Task<int> RunFunctionalAsync(FrameworkElement root, NavigationService nav, string folder, int index)
    {
        var sp = App.Services;
        async Task Shot(string name, int delay)
        {
            await Task.Delay(delay);
            Save(root, Path.Combine(folder, $"{++index:00}-F-{name}.png"));
        }

        var net = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<NetworkViewModel>(sp);
        nav.Navigate(PageKey.Network);
        net.SelectedTab = 0;
        net.PingHost = "1.1.1.1";
        net.PingCount = NetworkViewModel.PingCounts[1];
        await net.StartPingCommand.ExecuteAsync(null);
        await Shot("Ping", 500);
        net.SelectedTab = 1;
        net.DnsQuery = "github.com";
        await net.LookupCommand.ExecuteAsync(null);
        await Shot("Dns", 500);
        net.SelectedTab = 2;
        net.TraceHost = "8.8.8.8";
        await net.TraceCommand.ExecuteAsync(null);
        await Shot("Trace", 2500);
        net.SelectedTab = 3;
        await Shot("Interfaces", 1500);
        net.SelectedTab = 4;
        await Shot("Connections", 1500);
        net.SelectedTab = 5;
        await Shot("Arp", 1500);

        var logs = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<LogAnalyzerViewModel>(sp);
        nav.Navigate(PageKey.Logs);
        var cbs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Logs\CBS\CBS.log");
        await logs.AnalyzeFileAsync(File.Exists(cbs) ? cbs : AppInfo.LogFile);
        await Shot("Logs", 1500);

        var term = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<TerminalViewModel>(sp);
        nav.Navigate(PageKey.Terminal);
        term.Current = term.Sessions[0];
        term.Current.Input = "Get-CimInstance Win32_OperatingSystem | Select-Object Caption, Version, LastBootUpTime | Format-List";
        await term.RunCommand.ExecuteAsync(null);
        term.Current.Input = "Get-Item C:\\does-not-exist";
        await term.RunCommand.ExecuteAsync(null);
        await Shot("Terminal-PS", 800);
        term.Current = term.Sessions[1];
        term.Current.Input = "ipconfig | findstr IPv4";
        await term.RunCommand.ExecuteAsync(null);
        await Shot("Terminal-CMD", 800);

        var storage = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<StorageViewModel>(sp);
        nav.Navigate(PageKey.Storage);
        await Task.Delay(800);
        var target = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var scan = typeof(StorageViewModel).GetMethod("ScanPathAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (scan?.Invoke(storage, [target]) is Task t) await t;
        await Shot("Storage", 1500);
        return index;
    }

    public static void Save(FrameworkElement root, string path)
    {
        try
        {
            root.UpdateLayout();
            int w = (int)Math.Ceiling(root.ActualWidth), h = (int)Math.Ceiling(root.ActualHeight);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle((Brush)Application.Current.FindResource("Brush.Bg"), null, new Rect(0, 0, w, h));
                dc.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, w, h));
            }
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(path);
            encoder.Save(fs);
            AppLog.Info($"QA capture saved {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            AppLog.Error("QA capture failed for " + path, ex);
        }
    }
}
