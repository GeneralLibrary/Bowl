using System.Runtime.CompilerServices;

// Friend assembly names, including the console binary's `bowl` AssemblyName.
[assembly: InternalsVisibleTo("bowl")]
[assembly: InternalsVisibleTo("Bowl.Console.Tests")]
[assembly: InternalsVisibleTo("Bowl.Host")]
