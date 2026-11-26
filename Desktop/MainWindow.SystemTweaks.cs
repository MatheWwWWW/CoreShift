using System.Globalization;
using System.Windows;
using SmartResourceDispatcher.Core;

namespace CoreShift.Desktop;

public partial class MainWindow
{
    private PagefileIncrease.State? pagefileTweak;
    private bool workingWithPagefile;
    private bool workingWithWindowEffects;

    private async Task RefreshPagefileTweakAsync()
    {
        if (workingWithPagefile)
            return;

        workingWithPagefile = true;
        SetPagefileControls(false, false);
        try { await ReadPagefileTweakAsync(); }
        finally { workingWithPagefile = false; }
    }

    private async Task ReadPagefileTweakAsync()
    {
        try
        {
            pagefileTweak = await RunInBackground(PagefileIncrease.Read);
            var state = pagefileTweak;
            string detail = state.Issue ?? "Параметры файла подкачки недоступны.";
            if (state.File is { } file)
            {
                string sizes = file.Automatic ? "размер управляется Windows"
                    : file.Initial == 0 ? "планируется новый файл" : $"задано {file.Initial:N0}–{file.Maximum:N0} МиБ";
                detail = $"{file.Path} · {sizes} · выделено {file.Allocated:N0} МиБ · свободно {DiskCleaner.FormatBytes((long)state.FreeBytes)}.";
                if (state.Issue is not null && !state.HasBackup)
                    detail += $" {state.Issue}";
            }

            PagefileTweakState.Text = detail;
            uint initial = state.HasBackup ? state.File?.Initial ?? 0 : state.SuggestedInitial;
            uint maximum = state.HasBackup ? state.File?.Maximum ?? 0 : state.SuggestedMaximum;
            PagefileInitialValue.Text = initial == 0 ? "" : initial.ToString(CultureInfo.CurrentCulture);
            PagefileMaximumValue.Text = maximum == 0 ? "" : maximum.ToString(CultureInfo.CurrentCulture);
            SetPagefileControls(state.Issue is null && !state.HasBackup, state.HasBackup);
        }
        catch (Exception ex) when (IsSystemError(ex))
        {
            pagefileTweak = null;
            PagefileTweakState.Text = $"Не удалось прочитать параметры файла подкачки. {ex.Message}";
            SetPagefileControls(false, false);
        }
    }

    private void SetPagefileControls(bool canApply, bool canRestore)
    {
        PagefileInitialValue.IsEnabled = canApply;
        PagefileMaximumValue.IsEnabled = canApply;
        ApplyPagefileButton.IsEnabled = canApply;
        RestorePagefileButton.IsEnabled = canRestore;
    }

    private async void ApplyPagefileClick(object sender, RoutedEventArgs e)
    {
        if (workingWithPagefile || pagefileTweak is not { File: { } file } state)
            return;

        if (!uint.TryParse(PagefileInitialValue.Text.Trim(), NumberStyles.None, CultureInfo.CurrentCulture, out uint initial) ||
            !uint.TryParse(PagefileMaximumValue.Text.Trim(), NumberStyles.None, CultureInfo.CurrentCulture, out uint maximum) ||
            !state.Accepts(initial, maximum))
        {
            PagefileTweakStatus.Visibility = Visibility.Visible;
            SetStatus(PagefileTweakStatus, $"Начальный размер должен быть в пределах {state.Minimum:N0}–{state.Capacity:N0} МиБ. Максимум берётся не ниже начального размера и ранее заданного максимума ({file.Maximum:N0} МиБ), верхняя граница — {state.AllowedMaximum:N0} МиБ.", error: true);
            return;
        }

        if (Confirm("Увеличение файла подкачки", $"Для {file.Path} будет задан размер от {initial:N0} до {maximum:N0} МиБ. " +
                "Убедитесь, что на диске останется место для приложений и обновлений. Изменения вступят в силу после перезагрузки Windows.", "Применить"))
            await ChangePagefileAsync(initial, maximum, restore: false, file.Path);
    }

    private async void RestorePagefileClick(object sender, RoutedEventArgs e) =>
        await ChangePagefileAsync(0, 0, restore: true, expectedPath: null);

    private async Task ChangePagefileAsync(uint initial, uint maximum, bool restore, string? expectedPath)
    {
        if (workingWithPagefile)
            return;

        workingWithPagefile = true;
        SetPagefileControls(false, false);
        PagefileTweakStatus.Visibility = Visibility.Visible;
        SetStatus(PagefileTweakStatus, "Обновляем параметры файла подкачки…");
        try
        {
            bool changed = await RunInBackground(() => PagefileIncrease.Change(initial, maximum, restore, expectedPath));
            SetStatus(PagefileTweakStatus, (restore, changed) switch
            {
                (false, true) => "Новый размер сохранён. Перезагрузите Windows.",
                (true, true) => "Исходные параметры восстановлены. Перезагрузите Windows.",
                _ => "Сохранённая настройка отсутствует."
            });
        }
        catch (Exception ex) when (IsSystemError(ex))
        {
            SetStatus(PagefileTweakStatus, ex.Message, error: true);
        }
        finally
        {
            if (!closing)
                await ReadPagefileTweakAsync();
            workingWithPagefile = false;
        }
    }

    private async Task RefreshWindowEffectsAsync()
    {
        if (workingWithWindowEffects)
            return;

        workingWithWindowEffects = true;
        SetWindowEffectsControls(false, false);
        try { await ReadWindowEffectsAsync(); }
        finally { workingWithWindowEffects = false; }
    }

    private async Task ReadWindowEffectsAsync()
    {
        try
        {
            var (optimized, hasBackup) = await RunInBackground(WindowEffects.Read);
            WindowEffectsState.Text = optimized ? "Перечисленные эффекты отключены."
                : hasBackup ? "Часть эффектов включена. Исходные значения сохранены." : "Некоторые визуальные эффекты включены.";
            SetWindowEffectsControls(!optimized && !hasBackup, hasBackup);
        }
        catch (Exception ex) when (IsSystemError(ex))
        {
            WindowEffectsState.Text = $"Не удалось прочитать визуальные настройки. {ex.Message}";
            SetWindowEffectsControls(false, false);
        }
    }

    private void SetWindowEffectsControls(bool canApply, bool canRestore)
    {
        ApplyWindowEffectsButton.IsEnabled = canApply;
        RestoreWindowEffectsButton.IsEnabled = canRestore;
    }

    private async void ApplyWindowEffectsClick(object sender, RoutedEventArgs e) => await ChangeWindowEffectsAsync(restore: false);

    private async void RestoreWindowEffectsClick(object sender, RoutedEventArgs e) => await ChangeWindowEffectsAsync(restore: true);

    private async Task ChangeWindowEffectsAsync(bool restore)
    {
        if (workingWithWindowEffects)
            return;

        workingWithWindowEffects = true;
        SetWindowEffectsControls(false, false);
        WindowEffectsStatus.Visibility = Visibility.Visible;
        SetStatus(WindowEffectsStatus, "Обновляем визуальные настройки…");
        try
        {
            bool changed = await RunInBackground(() => WindowEffects.Change(restore));
            SetStatus(WindowEffectsStatus, (restore, changed) switch
            {
                (false, true) => "Визуальные эффекты отключены.",
                (true, true) => "Исходные визуальные параметры восстановлены.",
                (true, false) => "Сохранённые настройки отсутствуют.",
                _ => "Эффекты уже отключены."
            });
        }
        catch (Exception ex) when (IsSystemError(ex))
        {
            SetStatus(WindowEffectsStatus, ex.Message, error: true);
        }
        finally
        {
            if (!closing)
                await ReadWindowEffectsAsync();
            workingWithWindowEffects = false;
        }
    }
}
