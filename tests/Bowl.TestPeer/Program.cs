if (args[0] == "lock")
{
    try
    {
        using var stream = new FileStream(args[1], FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Console.WriteLine("acquired");
        Console.Out.Flush();
        await Console.In.ReadLineAsync();
        return 0;
    }
    catch (IOException)
    {
        Console.WriteLine("busy");
        return 2;
    }
}

await Task.Delay(int.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture));
return 0;
