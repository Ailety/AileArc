using AileArc.Core.Extraction;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AileArc.UI;

public sealed partial class MainWindow
{
    private IReadOnlyList<ExtractionFailure> latestFailures = [];
    private void SetFailures(IReadOnlyList<ExtractionFailure> failures)
    {
        latestFailures = failures;
        errorDetails.Visibility = failures.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
    private void ShowFailure(string code, string path = "")
    {
        status.Text = text[code];
        SetFailures([new(path, code)]);
    }
    private async Task ShowErrorDetailsAsync()
    {
        if (dialogOpen || latestFailures.Count == 0) return;
        var details = new ListView { MaxHeight = 380, MinWidth = 440,
            ItemsSource = latestFailures.Select(f => string.IsNullOrEmpty(f.Path) ? text[f.Code] : $"{f.Path}\n{text[f.Code]}").ToArray() };
        var dialog = new ContentDialog { XamlRoot = root.XamlRoot, Title = text.Format("FailureCount", latestFailures.Count),
            Content = details, CloseButtonText = text["Close"] };
        dialogOpen = true;
        try { await dialog.ShowAsync(); }
        finally { dialogOpen = false; }
    }
}
