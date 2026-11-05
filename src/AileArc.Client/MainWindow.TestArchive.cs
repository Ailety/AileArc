using System.Windows;
using AileArc.Core;

namespace AileArc.Client;

public partial class MainWindow
{
    private async void TestArchive_Click(object sender, RoutedEventArgs e) => await TestCurrentArchiveAsync();

    internal async Task TestCurrentArchiveAsync()
    {
        if (IsBusy || archive is null || OpenedPath is null) return;
        var snapshot = archive.Scan;
        string path = OpenedPath;
        ArchiveTestResult? completed = null;
        await RunAsync(Text["TestArchive"], async token =>
        {
            using var progress = new WindowProgress<ArchiveTestProgress>(Dispatcher, p =>
            {
                if (!closed && !token.IsCancellationRequested)
                    StatusText.Text = Text.Format("TestArchiveProgress", p.Checked, p.Total, p.Bytes / 1048576d);
            });
            var result = await WithPasswordAsync(secret => Task.Run(() => worker.TestAsync(path, snapshot, progress, token, secret), token), token);
            return () =>
            {
                completed = result;
                if (result.Failures.Any(f => f.Code == "WrongPasswordOrDamaged")) password = null;
                StatusText.Text = result.Checked == 0 ? Text["TestArchiveEmpty"] : result.Failures.Count == 0 ? Text.Format("TestArchiveOk", result.Checked) :
                    Text.Format("TestArchiveErrors", result.Checked, result.Failures.Count);
            };
        }, false);
        if (!closed && completed is { Failures.Count: > 0 })
        {
            var dialog = new ActionDialog(this, "TestArchive", "Close", "Close");
            dialog.Explain(StatusText.Text);
            AddFailureDetails(dialog, completed.Failures);
            dialog.ShowDialog();
        }
    }
}
