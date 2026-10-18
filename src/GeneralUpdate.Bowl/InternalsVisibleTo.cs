using System.Runtime.CompilerServices;

// Friend assemblies (assembly names, not project names): the console binary is `bowl`
// (AssemblyName in the csproj) and the console test project is `Bowl.Console.Tests`.
[assembly: InternalsVisibleTo("bowl")]
[assembly: InternalsVisibleTo("Bowl.Console.Tests")]
