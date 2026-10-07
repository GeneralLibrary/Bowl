// A real, isolated process for PID/start-time and exit tests. It never touches
// installation files or performs production monitor duties.
await Task.Delay(int.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture));
