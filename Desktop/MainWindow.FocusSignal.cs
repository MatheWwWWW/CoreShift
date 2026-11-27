using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using SmartResourceDispatcher.Core;

namespace CoreShift.Desktop;

public partial class MainWindow
{
    private sealed record SignalRow(string Name, string Transition, string State, bool IsApplied);
    private ProfileApplicationRow[] signalTargets = [];
    private readonly Dictionary<int, ContextProfileManager.PriorityUpdate> signalChanges = [];
    private ProcessPriorityClass signalPriority;

    private void RenderFocusSignal(bool updateTargets = false)
    {
        if (FocusSignalTitle is null)
            return;

        bool hasApplied = signalChanges.Values.Any(update => update.State == ContextProfileManager.PriorityState.Applied);
        bool running = profileStop is not null;
        bool stopping = profileStop?.IsCancellationRequested == true;
        string title = (running, stopping, focusTrial?.Stage) switch
        {
            (false, _, _) => "Профиль остановлен",
            (_, true, _) => "Восстанавливаем приоритеты",
            (_, _, FocusTrialStage.Baseline) => "Замер без профиля",
            (_, _, FocusTrialStage.Ready) => "Первый замер готов",
            (_, _, FocusTrialStage.Failed) => "Замер прерван",
            _ when hasApplied => "Профиль применён",
            _ when signalChanges.Values.Any(update => update.State == ContextProfileManager.PriorityState.AlreadySet) => "Приоритет уже задан",
            _ => "Ожидаем выбранные процессы"
        };
        SetChanged(FocusSignalTitle, UiText.Get(title));
        string brush = running ? hasApplied && !stopping ? "Accent" : "Standby" : "Muted";
        if (!Equals(FocusSignalLamp.Tag, brush))
        {
            FocusSignalLamp.Tag = brush;
            FocusSignalLamp.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brush);
        }
        Control overviewButton = OverviewFocusButton;
        string action = running ? "UI:К текущему профилю" : "UI:Открыть профиль фокуса";
        if (!Equals(overviewButton.Tag, action))
        {
            overviewButton.Tag = action;
            overviewButton.SetResourceReference(ContentControl.ContentProperty, action);
        }

        if (!updateTargets)
            return;
        var rows = signalTargets.Select(app =>
        {
            string name = $"{app.DisplayName} · PID {app.Process.Id}";
            if (!signalChanges.TryGetValue(app.Process.Id, out var result))
                return new SignalRow(name, PriorityLabel(signalPriority), UiText.Get("Ожидает применения"), false);
            string transition = result.Original is { } before && before != result.Requested
                ? $"{PriorityLabel(before)} → {PriorityLabel(result.Requested)}" : PriorityLabel(result.Requested);
            string state = result.State switch
            {
                ContextProfileManager.PriorityState.Applied => "Применён",
                ContextProfileManager.PriorityState.AlreadySet => "Уже задан",
                ContextProfileManager.PriorityState.Restored => "Исходный приоритет восстановлен",
                ContextProfileManager.PriorityState.External => "Изменён другим приложением",
                ContextProfileManager.PriorityState.Exited => "Процесс завершён",
                _ => "Приоритет недоступен"
            };
            if (result.State == ContextProfileManager.PriorityState.Restored)
                transition = PriorityLabel(result.Original);
            return new SignalRow(name, transition, UiText.Get(state), result.State == ContextProfileManager.PriorityState.Applied);
        }).ToArray();
        UpdateRows(FocusSignalTargets, rows);
        FocusSignalTargets.Visibility = VisibleWhen(rows.Length > 0);
    }
}
