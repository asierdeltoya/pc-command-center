using System.Diagnostics;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.Input;
using PCCommandCenter.Core;

namespace PCCommandCenter.App.ViewModels;

public sealed record DataSource(string Title, string Text);

public sealed partial class AboutViewModel : PageViewModel
{
    public string Name => AppInfo.Name;
    public string Version => Loc.F("Version {0}", AppInfo.Version);
    public string Description => Loc.T(AppInfo.Description);
    public string Tagline => Loc.T(AppInfo.Tagline);
    public string Runtime => RuntimeInformation.FrameworkDescription;
    public string Os => RuntimeInformation.OSDescription;
    public string Architecture => RuntimeInformation.ProcessArchitecture.ToString();
    public string Privileges => AppInfo.IsElevated ? Loc.T("Administrator") : Loc.T("Standard user");
    public string DataFolder => AppInfo.DataDirectory;
    public string Executable => AppInfo.ExecutablePath;

    public IReadOnlyList<DataSource> DataSources { get; } =
    [
        new("CPU", "Windows performance counters (Processor Information, Thermal Zone, Energy Meter/RAPL)"),
        new("GPU", "DXGI adapters, GPU Engine / GPU Adapter Memory counters, NVIDIA NVML when available"),
        new("Memory", "GlobalMemoryStatusEx, GetPerformanceInfo, WMI Win32_PhysicalMemory"),
        new("Storage", "PhysicalDisk counters, Storage Management API (MSFT_PhysicalDisk), SMART via WMI"),
        new("Network", "IP Helper API (routing, TCP/UDP tables, ARP), ICMP, DnsQuery"),
        new("Processes", "NtQuerySystemInformation, process tokens and image paths"),
        new("Windows", "Event Log API, Service Control Manager, registry"),
        new("AI", "Anthropic Claude API (only when you send a question)"),
    ];

    [RelayCommand]
    private void OpenFolder()
    {
        try { Process.Start(new ProcessStartInfo(AppInfo.DataDirectory) { UseShellExecute = true }); } catch { }
    }
}
