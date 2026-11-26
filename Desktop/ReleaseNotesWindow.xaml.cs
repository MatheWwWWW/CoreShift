using System.Diagnostics;
using System.Windows;

namespace CoreShift.Desktop;

public partial class ReleaseNotesWindow : Window
{
    private readonly CancellationTokenSource loadStop = new();

    internal ReleaseNotesWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Версия {AppUpdater.CurrentVersion.ToString(3)}";
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        var token = loadStop.Token;
        try
        {
            string? notes = await AppUpdater.FetchReleaseNotesAsync(token);
            if (token.IsCancellationRequested)
                return;

            if (notes is not null)
                ReleaseNotesMarkdown.Render(ReleaseText.Document, notes);

            if (ReleaseText.Document.Blocks.Count == 0)
            {
                Status.Text = "Для этой версии пока нет описания релиза.";
                return;
            }

            ReleaseText.Visibility = Visibility.Visible;
            Status.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (AppUpdater.IsUpdateError(ex))
        {
            if (token.IsCancellationRequested)
                return;

            Debug.WriteLine(ex);
            Status.Text = "Не удалось загрузить описание релиза с GitHub. Попробуйте открыть окно ещё раз.";
        }
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        loadStop.Cancel();
        loadStop.Dispose();
        base.OnClosed(e);
    }
}
