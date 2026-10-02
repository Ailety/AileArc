using AileArc.Core.Recovery;

if (args.Length != 2) return 1;
using var file = TrackedTemporaryFile.Create(args[0], "Test", new RecoveryStore(args[1]));
await File.WriteAllTextAsync(file.Path, "owned incomplete data");
Console.WriteLine(file.Id);
await Task.Delay(Timeout.Infinite);
return 0;
