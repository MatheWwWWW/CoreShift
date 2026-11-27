using System.Windows;

namespace CoreShift.Desktop;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string message, string action)
    {
        InitializeComponent();
        Title = title;
        Heading.Text = title;
        Message.Text = message;
        ActionButton.Content = action;
        Loaded += (_, _) =>
        {
            if (Content is FrameworkElement panel)
                UiMotion.Enter(panel);
            CancelButton.Focus();
        };
    }

    private void ConfirmClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
