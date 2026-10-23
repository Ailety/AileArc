using System.IO;
using System.IO.Compression;
using System.Windows.Threading;
using AileArc.Core;
using AileArc.DesktopBridge;

namespace AileArc.Client;

internal static class ClientChecks
{
    public static int Run(ClientApp app)
    {
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "client-self-test-error.txt"), "", System.Text.Encoding.UTF8);
        int result = 1;
        var window = new MainWindow { Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
        window.ContentRendered += async (_, _) =>
        {
            string temp = Path.Combine(Path.GetTempPath(), "ailearc-client-test-" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                var zh = LanguageService.Load("zh-CN"); var en = LanguageService.Load("en-US");
                foreach (string key in zh.Keys.Where(k => k.StartsWith("Client", StringComparison.Ordinal))) Require(en.ContainsKey(key), "missing translation");
                Require(window.Title == ClientApp.Text["ClientTitle"], "localized window title");
                Require(window.TestGrid.Items.Count == 0, "formal client starts without generated data");
                var restored = ClientApp.ParseArguments(["--restore-session", "[{\"Path\":\"C:/测试.zip\",\"CodePage\":936}]"]);
                Require(restored.Length == 1 && restored[0].CodePage == 936 && restored[0].Path == "C:/测试.zip", "restart preserves filename encoding");
                bool refused = false;
                try { ClientApp.ParseArguments(["--restore-session", "[{\"Path\":\"C:/test.zip\",\"CodePage\":999}]"]); }
                catch (ArgumentException) { refused = true; }
                Require(refused, "unsupported encoding rejected");
                using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
                { using var writer = new StreamWriter(zip.CreateEntry("文档/说明.txt").Open(), System.Text.Encoding.UTF8); writer.Write("public test fixture"); }
                await window.TestOpenAsync(temp);
                Require(window.TestHasArchive && window.TestGrid.Items.Count == 1, "real worker and implicit directory");
                var rows = Enumerable.Range(0, 100000).Select(i => new EntryView(new BrowserRow(i.ToString(), $"测试_{i}.md", "", $"测试_{i}.md", "md", 1, "—", false))).ToArray();
                window.ApplyRows(rows); window.UpdateLayout();
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                window.TestGrid.SelectedItems.Add(rows[1]); window.TestGrid.SelectedItems.Add(rows[3]);
                Require(window.TestAllState is null && window.TestGrid.SelectedItems.Count == 2, "mixed selection");
                window.ToggleAll(); Require(window.TestGrid.SelectedItems.Count == 100000 && window.TestAllState is true, "select all");
                window.TestGrid.UnselectAll(); window.TestGrid.ScrollIntoView(rows[^1]);
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                Require(window.TestGrid.ItemContainerGenerator.ContainerFromIndex(99999) is not null, "last virtual row");
                await window.TestOpenAsync(temp);
                Require(window.TestHasArchive && window.TestGrid.Items.Count == 1, "real worker and implicit directory");
                Require(window.TestGrid.SelectedItems.Count == 0, "session replacement clears selection");
                await window.TestOpenAsync(temp + ".missing");
                Require(window.OpenedPath == temp && window.TestGrid.Items.Count == 1, "failed open retains previous session");
                result = 0;
            }
            catch (Exception error) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "client-self-test-error.txt"), error.ToString(), System.Text.Encoding.UTF8); }
            finally { if (File.Exists(temp)) File.Delete(temp); window.Close(); app.Shutdown(result); }
        };
        app.Run(window); return result;
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
