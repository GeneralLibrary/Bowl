// This executable is test-only and does not perform production monitor duties.
if (args.Length > 0 && args[0] == "test-report-retry")
{
    Environment.ExitCode = await TestSupervisor.RetryReport(args);
}
else if (args.Length > 0 && args[0] == "test-supervise")
{
    Environment.ExitCode = await TestSupervisor.Run(args);
}
else if (args.Length > 0 && args[0] == "test-controlled-worker")
{
    Environment.ExitCode = await TestSupervisor.Worker(args);
}
else if (args.Length > 0 && args[0] == "test-recover")
{
    Environment.ExitCode = await TestRecovery.Run(args);
}
else if (args.Length == 4 && args[0] == "pipe-peer" && OperatingSystem.IsWindows())
{
    using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", args[1],
        System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
    await pipe.ConnectAsync(10000);
    var expected = new Bowl.Host.ProcessIdentity(int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture),
        DateTimeOffset.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture));
    using var server = Bowl.Host.ControlledChannel.AuthenticateServer(pipe, expected);
    using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
    await writer.WriteLineAsync("VERIFIED");
    await Task.Delay(TimeSpan.FromSeconds(30));
}
else if (args.Length == 2 && args[0] == "job-parent" && OperatingSystem.IsWindows())
{
    using var worker = Bowl.Host.ControlledWorker.Start(
        Environment.ProcessPath!, [args[1], "60000"], Environment.CurrentDirectory);
    Console.WriteLine(worker.Identity.Pid);
    await Task.Delay(TimeSpan.FromMinutes(1));
}
else if (args.Length == 5 && args[0] == "lease")
{
    using var lease = Bowl.Host.ControlledLease.Acquire(args[1], args[2], args[3], args[4]);
    Console.WriteLine("LEASE_ACQUIRED");
    await Task.Delay(TimeSpan.FromMinutes(1));
}
else
    await Task.Delay(int.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture));
