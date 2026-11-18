using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GeneralUpdate.Bowl;

namespace GeneralUpdate.Bowl.Internal;

/// <summary>
/// Windows system info provider — runs <c>export.bat</c> to collect driver, system,
/// and event log data. The executed command line is fully literal
/// (<c>cmd /c export.bat</c>, resolved from the script's own directory); the output
/// directory travels via a child-only environment variable, never
/// as a command-line argument, so no runtime data enters the process command line.
/// </summary>
internal sealed class WindowsSystemInfoProvider : ISystemInfoProvider
{
    public Task ExportAsync(string outputDirectory, CancellationToken ct)
    {
        GeneralTracer.Info($"WindowsSystemInfoProvider.ExportAsync: exporting to {outputDirectory}.");

        var appDir = Path.Combine(AppContext.BaseDirectory, "Applications", "Windows");
        var batPath = Path.Combine(appDir, "export.bat");

        if (!File.Exists(batPath))
        {
            GeneralTracer.Error($"WindowsSystemInfoProvider: export.bat not found at {batPath}.");
            throw new FileNotFoundException("export.bat not found!", batPath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c export.bat",
            WorkingDirectory = appDir,
            UseShellExecute = false,
        };
        startInfo.EnvironmentVariables["BOWL_EVIDENCE_DIRECTORY"] = outputDirectory;
        using (var process = Process.Start(startInfo))
        {
            if (process == null)
            {
                GeneralTracer.Warn("WindowsSystemInfoProvider: export.bat could not be started.");
                return Task.CompletedTask;
            }

            if (!process.WaitForExit(30_000))
            {
                process.Kill();
                GeneralTracer.Warn("WindowsSystemInfoProvider: export.bat timed out after 30s and was killed.");
            }
            else
            {
                GeneralTracer.Info($"WindowsSystemInfoProvider: export.bat finished (code {process.ExitCode}).");
            }
        }
        return Task.CompletedTask;
    }
}
