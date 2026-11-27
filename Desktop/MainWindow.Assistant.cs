using System.Windows;
using SmartResourceDispatcher.Core;

namespace CoreShift.Desktop;

public partial class MainWindow
{
    private AssistantWindow? assistantWindow;

    private void OpenAssistant()
    {
        if (assistantWindow is { } existing)
        {
            existing.Activate();
            return;
        }
        var window = new AssistantWindow(AssistantSnapshot) { Owner = this };
        assistantWindow = window;
        window.Closed += (_, _) => assistantWindow = null;
        window.Show();
    }

    private string AssistantSnapshot()
    {
        var hardware = componentInventory;
        var memory = lastOverview?.Memory;
        return string.Join("\n", new[]
        {
            $"Windows: {hardware?.Windows ?? Environment.OSVersion.ToString()}",
            $"CPU: {hardware?.Cpu}, cores: {hardware?.Cores}, threads: {hardware?.Threads}",
            $"GPU: {dashboardGraphics?.Name}",
            $"RAM total: {(memory is { } sample ? FormatBytes(sample.TotalBytes) : "—")}",
            $"RAM available: {(memory is { } current ? FormatBytes(current.AvailableBytes) : "—")}",
            $"Commit headroom: {FormatBytes(lastOverview?.Commit?.AvailableBytes)}",
            $"Windows volume: {lastOverview?.Drive?.Name}, free: {SystemDiskFreeText.Text}",
            $"Physical drives: {string.Join("; ", hardware?.Drives.Select(drive => $"{drive.Name}, {drive.Capacity}, {drive.Volumes}") ?? [])}"
        });
    }
}
