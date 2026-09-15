using AileArc.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AileArc.UI;

public sealed partial class App : Application
{
    private readonly string[] args;
    private MainWindow? window;
    public App(string[] arguments)
    {
        args = arguments;
        InitializeComponent();
    }
    protected override async void OnLaunched(LaunchActivatedEventArgs e)
    {
        var settings = await AppSettings.LoadAsync();
        window = new MainWindow(new LanguageService(settings.Language));
        window.Activate();
        if (args.Length == 1) await window.OpenArchiveAsync(args[0]);
    }
}
