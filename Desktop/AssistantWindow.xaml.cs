using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using SmartResourceDispatcher.Core;

namespace CoreShift.Desktop;

public partial class AssistantWindow : Window
{
    private readonly List<AssistantService.Turn> history = [];
    private readonly Func<string> snapshot;
    private readonly CancellationTokenSource lifetime = new();
    private readonly bool modelAvailable = ShifterEngine.IsInstalled;
    private CancellationTokenSource? activeAnswer;
    private AssistantImage? image;
    private bool busy;
    private bool readingImage;
    private bool attachDevice;
    private bool closed;
    private int generation;

    internal AssistantWindow(Func<string> snapshot)
    {
        this.snapshot = snapshot;
        InitializeComponent();
        UpdateControls();
        Closed += (_, _) =>
        {
            closed = true;
            lifetime.Cancel();
            lifetime.Dispose();
        };
        Loaded += (_, _) => Question.Focus();
    }

    private void UpdateControls(string? notice = null)
    {
        bool canAttach = modelAvailable && !busy && !readingImage;
        SubmitButton.IsEnabled = !busy && !readingImage;
        PhotoButton.IsEnabled = canAttach;
        AttachmentButton.IsEnabled = canAttach;
        Attachments.Visibility = modelAvailable ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        AttachmentButton.Content = UiText.Get(attachDevice ? "Сводка ПК прикреплена · убрать" : "Прикрепить сведения о ПК");
        ImageAttachment.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
        ImagePreview.Source = image?.Preview;
        ImageName.Text = image?.Name ?? "";
        Status.Text = notice ?? (busy ? UiText.Get("Локальная модель готовит ответ…")
            : readingImage ? UiText.Get("Подготавливаю фото…")
            : modelAvailable ? UiText.Get("Локальный чат · фото и сообщения обрабатываются на этом ПК.")
            : UiText.Get("Доступна справка. Для чата нужен полный установщик CoreShift."));
    }

    private void ClearClick(object sender, RoutedEventArgs e)
    {
        generation++;
        activeAnswer?.Cancel();
        Messages.Children.Clear();
        history.Clear();
        image = null;
        attachDevice = false;
        UpdateControls();
        Question.Focus();
    }

    private void CancelClick(object sender, RoutedEventArgs e) => activeAnswer?.Cancel();

    private void AttachmentClick(object sender, RoutedEventArgs e)
    {
        attachDevice = !attachDevice;
        UpdateControls();
        Question.Focus();
    }

    private void RemoveImageClick(object sender, RoutedEventArgs e)
    {
        image = null;
        UpdateControls();
        Question.Focus();
    }

    private async void PhotoClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = UiText.Get("Изображения PNG, JPEG и BMP") + "|*.png;*.jpg;*.jpeg;*.bmp",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
            await LoadImageAsync(dialog.FileName);
    }

    private void ImageDragOver(object sender, DragEventArgs e)
    {
        e.Effects = modelAvailable && !busy && !readingImage && e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void ImageDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files)
            await LoadImageAsync(files[0]);
    }

    private void AssistantKeyDown(object sender, KeyEventArgs e)
    {
        if (!modelAvailable || busy || readingImage || e.Key != Key.V || Keyboard.Modifiers != ModifierKeys.Control)
            return;
        try
        {
            if (Clipboard.GetImage() is not { } bitmap)
                return;
            e.Handled = true;
            image = AssistantImage.FromClipboard(UiText.Get("Скриншот из буфера"), bitmap);
            UpdateControls();
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or NotSupportedException or ExternalException)
        {
            UpdateControls(UiText.Get("Не удалось прикрепить фото. Выберите PNG, JPEG или BMP до 12 МБ."));
        }
    }

    private async Task LoadImageAsync(string path)
    {
        if (!modelAvailable || busy || readingImage)
            return;
        readingImage = true;
        int current = generation;
        string? notice = null;
        UpdateControls();
        try
        {
            var attachment = await Task.Run(() => AssistantImage.Load(path));
            if (generation == current && !closed)
                image = attachment;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                or ArgumentException or NotSupportedException or ExternalException or FileFormatException)
        {
            notice = UiText.Get("Не удалось прикрепить фото. Выберите PNG, JPEG или BMP до 12 МБ.");
        }
        finally
        {
            readingImage = false;
            if (!closed)
            {
                UpdateControls(generation == current ? notice : null);
                Question.Focus();
            }
        }
    }

    private async void SubmitClick(object sender, RoutedEventArgs e)
    {
        string input = Question.Text.Trim();
        if (busy || readingImage || (input.Length == 0 && image is null))
            return;

        string question = input.Length > 0 ? input : UiText.Get("Разбери изображение. Если на нём ошибка, объясни, что проверить.");
        var sources = AssistantService.FindSources(question);
        int turnGeneration = generation;
        var attachment = image;
        AddMessage(UiText.Get("Вы"), input, [], attachment: attachment);
        Question.Clear();
        if (!modelAvailable)
        {
            var notes = sources.Select(source => UiText.IsEnglish ? source.English : source.Russian);
            string answer = AssistantService.ShortReply(question) ?? (sources.Length > 0 ? string.Join("\n\n", notes)
                : UiText.Get("Для этого вопроса нужна локальная модель из установщика CoreShift с Шифтером."));
            AddMessage(UiText.Get("Шифтер"), answer, sources);
            return;
        }

        bool includeDevice = attachDevice;
        attachDevice = false;
        image = null;
        busy = true;
        UpdateControls();
        Question.Focus();

        using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        activeAnswer = request;
        string? notice = null;
        try
        {
            string? device = includeDevice ? snapshot() : null;
            string answer = await AssistantService.ReplyAsync(history.ToArray(), question, sources, device, attachment?.Png, request.Token);
            if (turnGeneration != generation || closed)
                return;
            history.Add(new("user", attachment is null ? question : question + "\n[Image attached in this previous turn.]"));
            history.Add(new("assistant", answer));
            if (history.Count > 6)
                history.RemoveRange(0, history.Count - 6);
            AddMessage(UiText.Get("Шифтер"), answer, sources, citedOnly: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or KeyNotFoundException
                or InvalidOperationException or IOException or Win32Exception or UnauthorizedAccessException)
        {
            if (!closed && turnGeneration == generation)
            {
                notice = request.IsCancellationRequested ? UiText.Get("Ответ остановлен.")
                    : UiText.Get("Шифтер не смог ответить. Повторите запрос или попробуйте уменьшить фото.");
                if (string.IsNullOrWhiteSpace(Question.Text))
                {
                    Question.Text = input;
                    image = attachment;
                }
            }
        }
        finally
        {
            activeAnswer = null;
            busy = false;
            if (!closed)
                UpdateControls(notice);
        }
    }

    private void AddMessage(string title, string text, AssistantService.Source[] sources, bool citedOnly = false,
        AssistantImage? attachment = null)
    {
        var stack = new StackPanel { Margin = new Thickness(3, 4, 3, 13) };
        var name = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        name.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        stack.Children.Add(name);
        if (attachment is not null)
        {
            var thumbnail = new Image
            {
                Source = attachment.Preview, ToolTip = attachment.Name, MaxWidth = 160, MaxHeight = 110,
                HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 6)
            };
            System.Windows.Automation.AutomationProperties.SetName(thumbnail, attachment.Name);
            stack.Children.Add(thumbnail);
        }
        if (text.Length > 0)
            stack.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        if (sources.Length > 0)
        {
            var links = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) };
            for (int i = 0; i < sources.Length; i++)
            {
                if (citedOnly && !text.Contains($"[{i + 1}]", StringComparison.Ordinal))
                    continue;
                var source = sources[i];
                var link = new Hyperlink(new Run($"[{i + 1}] {source.Title}"));
                link.SetResourceReference(TextElement.ForegroundProperty, "Accent");
                link.Click += (_, _) => { using var process = Process.Start(new ProcessStartInfo(source.Link.AbsoluteUri) { UseShellExecute = true }); };
                links.Inlines.Add(link);
                links.Inlines.Add(new Run("   "));
            }
            if (links.Inlines.Count > 0)
                stack.Children.Add(links);
        }
        Messages.Children.Add(stack);
        if (Messages.Children.Count > 24)
            Messages.Children.RemoveAt(0);
        Transcript.ScrollToEnd();
    }
}
