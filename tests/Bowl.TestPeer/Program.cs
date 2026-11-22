// A real, isolated process for PID/start-time and exit tests. It never touches
// installation files or performs production monitor duties.
await Task.Delay(int.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture));
if (args.Length > 1 && args[1] == "--crash")
    throw new InvalidOperationException("Intentional test crash.");
