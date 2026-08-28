using Avalonia.Controls;
using Avalonia.Interactivity;
using JetBrains.Annotations;

namespace SS14.Launcher.Views;

public partial class CrashReportDialog : Window
{
    private readonly string _report;

    // Required by the Avalonia XAML loader/previewer; never used at runtime otherwise.
    [UsedImplicitly]
    public CrashReportDialog() : this("")
    {
    }

    public CrashReportDialog(string report)
    {
        _report = report;
        InitializeComponent();

        ReportText.Text = report;
    }

    private void Close_OnClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void Copy_OnClick(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is { } clipboard)
            await clipboard.SetTextAsync(_report);
    }
}
