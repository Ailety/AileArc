using AileArc.Shared;

// A test-only worker that speaks the real framing protocol over redirected pipes.
internal static class WorkerPipeProbe
{
    public static async Task<int> RunAsync()
    {
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        var request = await ArchiveProtocol.ReadAsync<ScanRequest>(input);
        Task Send(ScanMessage message) => ArchiveProtocol.WriteAsync(output, message);
        if (request.Operation == "create")
        {
            if (request.CompressionCount > 32) await Task.Delay(Timeout.Infinite);
            var batch = await ArchiveProtocol.ReadAsync<CompressionBatch>(input);
            await Send(new("verifying"));
            if (batch.Items[0].EntryPath is "changed-stage" or "unverified-stage")
            {
                ArchiveIdentity? checkedVersion = null;
                if (batch.Items[0].EntryPath == "changed-stage")
                {
                    using var handle = new Microsoft.Win32.SafeHandles.SafeFileHandle(new IntPtr(request.OutputHandle!.Value), true);
                    checkedVersion = ArchiveFileIdentity.Read(handle);
                    using var changed = new FileStream(handle, FileAccess.ReadWrite);
                    changed.WriteByte(42);
                }
                await Send(new("completed", Identity: checkedVersion));
                return 0;
            }
            await Send(new("completed"));
            await Task.Delay(Timeout.Infinite);
        }
        string mode = Path.GetFileName(request.Path);
        if (mode == "slow-open")
        {
            for (int i = 0; i < 9; i++) { await Task.Delay(500); await Send(new("activity")); }
        }
        await Send(new("opened", Format: "ZIP", Identity: new("fixture", 0, 0)));
        if (request.Operation == "test")
        {
            await Send(new("ready"));
            await ArchiveProtocol.ReadAsync<ExtractionCommand>(input);
            if (mode == "duplicate-check") await Send(new("checked", Checked: [new(0, 0, 1), new(0, 0, 1)]));
            if (mode == "test-sends-data") await Send(new("data", EntryId: 0, Data: [42]));
            await Send(new("completed"));
            return 0;
        }
        switch (mode)
        {
            case "invalid-json": await output.WriteAsync(new byte[] { 1, 0, 0, 0, 123 }); return 0;
            case "null-entry": await Send(new("entries", Entries: [null!])); return 0;
            case "empty-batch": await Send(new("entries", Entries: [])); return 0;
            case "negative-id": await Send(new("entries", Entries: [new(-1, "x", false, 1, false)])); return 0;
            case "invalid-file-time": await Send(new("entries", Entries: [new(0, "x", false, 1, false, ModifiedFileTime: long.MaxValue)])); return 0;
            case "unordered-ids":
            case "duplicate-prefix":
            case "duplicate-sparse":
                await Send(new("entries", Entries: [new(0, "a", false, 1, false), new(20, "b", false, 1, false)]));
                long last = mode switch { "duplicate-prefix" => 0, "duplicate-sparse" => 20, _ => 1 };
                await Send(new("entries", Entries: [new(last, "c", false, 1, false)]));
                await Send(new("completed")); return 0;
            case "missing-id":
                await ArchiveProtocol.WriteAsync(output, new { Kind = "entries", Entries = new[] { new { Path = "x", IsDirectory = false, Size = 1, Encrypted = false } } });
                return 0;
        }
        if (mode == "partial-frame")
        {
            await output.WriteAsync(new byte[] { 80, 0, 0, 0, 123 });
            await output.FlushAsync();
            await File.WriteAllTextAsync(request.Path + ".starting", Environment.ProcessId.ToString());
            File.Move(request.Path + ".starting", request.Path + ".pid");
            await Task.Delay(Timeout.Infinite);
        }
        if (request.Operation == "extract")
        {
            if (mode.StartsWith("damaged-copy-", StringComparison.Ordinal))
            {
                string fault = mode["damaged-copy-".Length..];
                await Send(new("entries", Entries: [new(0, "partial.txt", false, fault == "overrun" ? 1u : 5u, fault == "encrypted")]));
                await Send(new("ready")); await ArchiveProtocol.ReadAsync<ExtractionCommand>(input);
                await Send(new("file", EntryId: 0));
                if (fault != "empty") await Send(new("data", EntryId: 0, Data: [65, 66]));
                await Send(new("fileDone", EntryId: 0, Result: fault switch { "short" => 5, "method" => 1, "false-success" => 0, _ => 3 }));
                if (fault == "extra") await Send(new("data", EntryId: 0, Data: [67]));
                if (fault == "cancel") { File.WriteAllText(request.Path + ".ready", "ready"); await Task.Delay(Timeout.Infinite); }
                await Send(new("completed"));
                return fault == "exit" ? 3 : 0;
            }
            await Send(new("entries", Entries: [new(0, "ok.txt", false, 1, false), new(1, "partial.txt", false, 2, false)]));
            await Send(new("ready"));
            await ArchiveProtocol.ReadAsync<ExtractionCommand>(input);
            await Send(new("file", EntryId: 0));
            await Send(new("data", EntryId: 0, Data: [42]));
            await Send(new("fileDone", EntryId: 0, Result: 0));
            await Send(new("file", EntryId: 1));
            await Send(new("data", EntryId: 1, Data: [99]));
            if (mode == "invalid-mid-extraction")
            {
                await output.WriteAsync(new byte[] { 1, 0, 0, 0, 123 });
                return 0;
            }
            if (mode == "slow-decision")
            {
                await Send(new("data", EntryId: 1, Data: [100]));
                await Send(new("fileDone", EntryId: 1, Result: 0));
                await Send(new("completed"));
                return 0;
            }
            await Task.Delay(Timeout.Infinite);
        }
        int batches = mode == "slow-scan" ? 9 : 1;
        for (int i = 0; i < batches; i++)
        {
            if (batches > 1) await Task.Delay(500);
            await Send(new("entries", Entries: [new(i, $"entry-{i}.txt", false, 1, false)]));
        }
        await Send(new("completed"));
        if (mode == "stuck-exit") await Task.Delay(Timeout.Infinite);
        return 0;
    }
}
