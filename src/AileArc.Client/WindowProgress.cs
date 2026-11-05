using System.Windows.Threading;

namespace AileArc.Client;

// Producers may report once per IPC frame. Only the latest observation needs
// to reach the status bar, even when the dispatcher is busy or a dialog is open.
internal sealed class WindowProgress<T> : IProgress<T>, IDisposable where T : notnull
{
    private readonly DispatcherTimer timer;
    private readonly Action<T> display;
    private object? latest;

    internal WindowProgress(Dispatcher dispatcher, Action<T> display)
    {
        this.display = display;
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Normal, Tick, dispatcher);
    }

    public void Report(T value) => Interlocked.Exchange(ref latest, value);

    private void Tick(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref latest, null) is T value) display(value);
    }

    public void Dispose()
    {
        timer.Stop();
        timer.Tick -= Tick;
        Interlocked.Exchange(ref latest, null);
    }
}
