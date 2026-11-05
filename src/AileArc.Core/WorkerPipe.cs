using System.Diagnostics;
using AileArc.Shared;
using Microsoft.Win32.SafeHandles;

namespace AileArc.Core;

// Only waiting for the worker is timed. Disk I/O and user conflict decisions in
// the client can take longer than an IPC timeout without indicating a stuck worker.
internal sealed class WorkerPipe : IAsyncDisposable
{
    private readonly Process process;
    private readonly TimeSpan timeout;
    private readonly CancellationToken caller;
    private readonly CancellationTokenSource deadline;
    private readonly CancellationTokenRegistration terminate;
    private readonly SafeFileHandle job;
    private readonly Task drain;

    public WorkerPipe(Process process, SafeFileHandle job, TimeSpan timeout, CancellationToken caller)
    {
        this.process = process;
        this.timeout = timeout;
        this.caller = caller;
        this.job = job;
        // Drain without retaining native diagnostics, which may include private paths.
        drain = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        deadline = CancellationTokenSource.CreateLinkedTokenSource(caller);
        // Closing the kill-on-close job also unblocks anonymous pipe I/O on Windows.
        terminate = deadline.Token.Register(static state => ((SafeFileHandle)state!).Dispose(), job);
    }

    public async Task WriteAsync<T>(T value)
    {
        deadline.CancelAfter(timeout);
        try { await ArchiveProtocol.WriteAsync(process.StandardInput.BaseStream, value, deadline.Token).ConfigureAwait(false); }
        finally { FinishWait(); }
    }

    public async Task<ScanMessage> ReadAsync()
    {
        deadline.CancelAfter(timeout);
        try { return await ArchiveProtocol.ReadAsync<ScanMessage>(process.StandardOutput.BaseStream, deadline.Token).ConfigureAwait(false); }
        finally { FinishWait(); }
    }

    public async Task WaitForExitAsync()
    {
        deadline.CancelAfter(timeout);
        try { await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
        finally { FinishWait(); }
    }

    private void FinishWait()
    {
        deadline.CancelAfter(Timeout.InfiniteTimeSpan);
        caller.ThrowIfCancellationRequested();
        if (deadline.IsCancellationRequested) throw new ArchiveOperationException("TimedOut");
    }

    public async ValueTask DisposeAsync()
    {
        terminate.Dispose();
        deadline.Dispose();
        job.Dispose();
        await process.WaitForExitAsync().ConfigureAwait(false);
        await drain.ConfigureAwait(false);
    }
}
