using System.Windows;
using System.Windows.Controls;
using SmartResourceDispatcher.Core;

namespace CoreShift.Desktop;

public partial class FocusLiveWindow : Window
{
    internal FocusLiveWindow(bool light)
    {
        InitializeComponent();
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 16;
        Top = workArea.Top + 34;
        SourceInitialized += (_, _) => UpdateMaterial(light);
        Loaded += (_, _) => UiMotion.Enter(FocusLiveContent);
    }

    internal void UpdateMaterial(bool light)
    {
        bool glass = AppAppearance.SetWindowMaterial(this, light);
        FocusLiveSurface.SetResourceReference(Border.BackgroundProperty, glass ? "GlassTint" : "Canvas");
    }

    internal void UpdateReadings(string phase, FocusPhaseResult? baseline, FocusPhaseResult? optimized)
    {
        PhaseText.Text = phase;
        CpuDifference.Text = baseline is { } before && optimized is { } after
            ? UiText.Format($"{after.CpuPercent - before.CpuPercent:+0.0;-0.0;0.0} п. п.") : "—";
        MemoryDifference.Text = baseline is { } first && optimized is { } second
            ? FormatMemory(second.AverageWorkingSet - first.AverageWorkingSet) : "—";
        CurrentText.Text = (optimized ?? baseline) is { } current
            ? UiText.Format($"ЦП {current.CpuPercent:0.0}% · ОЗУ {DiskCleaner.FormatBytes(current.AverageWorkingSet)}")
            : "—";
    }

    private static string FormatMemory(long bytes) => (bytes == 0 ? "" : bytes < 0 ? "−" : "+") +
        DiskCleaner.FormatBytes(Math.Abs(bytes));
}
