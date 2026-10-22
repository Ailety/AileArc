using System.Windows;

namespace AileArc.WpfValidation;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var app = new Application();
        if (args is ["--self-test"]) return ValidationChecks.Run(app);
        app.Run(new MainWindow(args.FirstOrDefault()));
        return 0;
    }
}
