using GeneralUpdate.Bowl;
using GeneralUpdate.Bowl.Strategies;

/// <summary>
/// 分支覆盖点：
/// WindowsBowlStrategy.Prepare()：
///   - X86 架构 → "procdump.exe"
///   - X64 架构 → "procdump64.exe"
///   - 其他架构 → "procdump64a.exe"
///   - DumpType.Full → "-ma" 标志
///   - DumpType.Mini → "-mm" 标志
///   - DumpType.Heap → "-mh" 标志
///   - 正常返回非 null ProcessStartInfo
///   - FailDirectory 已存在 → 先删除再创建
///   - ProcessStartInfo 配置检查：RedirectStandardOutput/Error, UseShellExecute, CreateNoWindow
///   - 参数格式化：文件路径包含双引号
/// PostProcessAsync()：
///   - 始终返回 Task.CompletedTask
/// </summary>
public class WindowsBowlStrategyTests
{
    private BowlContext CreateContext(
        string processName = "test.exe",
        DumpType dumpType = DumpType.Full,
        string? dumpFileName = "crash.dmp",
        string? targetPath = null)
    {
        return new BowlContext
        {
            ProcessNameOrId = processName,
            DumpFileName = dumpFileName,
            FailFileName = "crash.json",
            TargetPath = targetPath ?? Path.GetTempPath(),
            FailDirectory = Path.Combine(Path.GetTempPath(), "fail", "test"),
            BackupDirectory = Path.Combine(Path.GetTempPath(), "backup", "test"),
            WorkModel = "Upgrade",
            ExtendedField = "1.0.0",
            TimeoutMs = 30_000,
            DumpType = dumpType,
        };
    }

    private string NormalizePath(string path)
    {
        // Remove double quotes for assertion
        return path.Trim('"').Replace("\\\\", "\\");
    }

    [Fact]
    public void Prepare_正常上下文_返回非null的ProcessStartInfo()
    {
        var strategy = new WindowsBowlStrategy();
        var ctx = CreateContext();

        var startInfo = strategy.Prepare(ctx);

        Assert.NotNull(startInfo);
    }

    [Fact]
    public void Prepare_正常上下文_FileName包含procdump路径()
    {
        var strategy = new WindowsBowlStrategy();
        var ctx = CreateContext();

        var startInfo = strategy.Prepare(ctx);

        Assert.NotNull(startInfo);
        Assert.Contains("procdump", startInfo!.FileName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Prepare_正常上下文_Arguments包含进程名()
    {
        var strategy = new WindowsBowlStrategy();
        var ctx = CreateContext(processName: "myapp.exe");

        var startInfo = strategy.Prepare(ctx);

        Assert.NotNull(startInfo);
        Assert.Contains("myapp.exe", startInfo!.Arguments);
    }

    [Fact]
    public void Prepare_DumpTypeFull_Arguments包含ma标志()
    {
        var strategy = new WindowsBowlStrategy();
        var ctx = CreateContext(dumpType: DumpType.Full);

        var startInfo = strategy.Prepare(ctx);

        Assert.NotNull(startInfo);
        Assert.Contains("-ma", startInfo!.Arguments);
    }

    [Fact]
    public void Prepare_DumpTypeMini_Arguments包含mm标志()
    {
        var strategy = new WindowsBowlStrategy();
        var ctx = CreateContext(dumpType: DumpType.Mini);

        var startInfo = strategy.Prepare(ctx);

        Assert.NotNull(startInfo);
        Assert.Contains("-mm", startInfo!.Arguments);
    }

    [Fact]
    public void Prepare_DumpTypeHeap_Arguments包含mh标志()
    {
        var strategy = new WindowsBowlStrategy();
        var ctx = CreateContext(dumpType: DumpType.Heap);

        var startInfo = strategy.Prepare(ctx);

        Assert.NotNull(startInfo);
        Assert.Contains("-mh", startInfo!.Arguments);
    }

    [Fact]
    public void Prepare_RedirectStandardOutput为true()
    {
        var strategy = new WindowsBowlStrategy();
        var ctx = CreateContext();

        var startInfo = strategy.Prepare(ctx);

        Assert.NotNull(startInfo);
        Assert.True(startInfo!.RedirectStandardOutput);
    }

    [Fact]
    public void Prepare_RedirectStandardError为true()
    {
        var strategy = new WindowsBowlStrategy();
        var ctx = CreateContext();

        var startInfo = strategy.Prepare(ctx);

        Assert.NotNull(startInfo);
        Assert.True(startInfo!.RedirectStandardError);
    }

    [Fact]
    public void Prepare_UseShellExecute为false()
    {
        var strategy = new WindowsBowlStrategy();
        var ctx = CreateContext();

        var startInfo = strategy.Prepare(ctx);

        Assert.NotNull(startInfo);
        Assert.False(startInfo!.UseShellExecute);
    }

    [Fact]
    public void Prepare_CreateNoWindow为true()
    {
        var strategy = new WindowsBowlStrategy();
        var ctx = CreateContext();

        var startInfo = strategy.Prepare(ctx);

        Assert.NotNull(startInfo);
        Assert.True(startInfo!.CreateNoWindow);
    }

    [Fact]
    public void Prepare_Arguments包含e标志()
    {
        var strategy = new WindowsBowlStrategy();
        var ctx = CreateContext();

        var startInfo = strategy.Prepare(ctx);

        Assert.NotNull(startInfo);
        Assert.StartsWith("-e ", startInfo!.Arguments);
    }

    [Fact]
    public async Task PostProcessAsync_始终返回CompletedTask()
    {
        var strategy = new WindowsBowlStrategy();
        var ctx = CreateContext();
        var exitResult = new ProcessExitResult { ExitCode = 0, OutputLines = new List<string>() };

        var task = strategy.PostProcessAsync(ctx, exitResult, CancellationToken.None);

        Assert.Equal(Task.CompletedTask, task);
        await task; // Should complete immediately
    }

    /// <summary>
    /// 串行化守卫：procdump 解析测试要读写 <c>AppContext.BaseDirectory</c> 下的固定路径
    /// （Applications\Windows\procdump64.exe），与其它测试并行写同一文件会互相干扰。
    /// </summary>
    private static readonly object ProcdumpResolutionLock = new();

    [Fact]
    public void Prepare_仅TargetPath有procdump_解析到TargetPath回退路径()
    {
        // 独立安装形态下 tier 探测在 watchdog 自身目录（BaseDirectory）进行；若
        // procdump 实际只存在于被监控应用的安装目录，Prepare 必须回退到该路径，
        // 否则每次启动采集工具都会失败（此前只解析 TargetPath 一处，独立部署时
        // 反过来失败——本次修复让两处都能解析）。
        lock (ProcdumpResolutionLock)
        {
            var targetRoot = Path.Combine(Path.GetTempPath(), $"bowl-procdump-{Guid.NewGuid():N}");
            var baseDirFile = Path.Combine(AppContext.BaseDirectory, "Applications", "Windows", "procdump64.exe");
            var targetDirFile = Path.Combine(targetRoot, "Applications", "Windows", "procdump64.exe");
            var backupFile = baseDirFile + ".test-backup";
            var baseExisted = File.Exists(baseDirFile);
            try
            {
                // 模拟“独立安装、BaseDirectory 无 procdump”的官方部署形态：先移走
                // 测试环境自带的副本，测试结束后恢复。
                if (baseExisted)
                    File.Move(baseDirFile, backupFile);

                Directory.CreateDirectory(Path.GetDirectoryName(targetDirFile)!);
                File.WriteAllBytes(targetDirFile, new byte[] { 0x4D, 0x5A });

                var ctx = CreateContext(targetPath: targetRoot);
                var startInfo = new WindowsBowlStrategy().Prepare(ctx);

                Assert.NotNull(startInfo);
                Assert.Equal(NormalizePath(targetDirFile), NormalizePath(startInfo!.FileName));
            }
            finally
            {
                if (baseExisted && File.Exists(backupFile))
                {
                    try { File.Move(backupFile, baseDirFile); } catch { /* best effort */ }
                }
                else
                {
                    // Only remove what this test created — the Applications\Windows
                    // directory under BaseDirectory also hosts export.bat for the
                    // SystemInfoProvider tests, so nuking the whole directory from a
                    // parallel test run breaks them.
                    try { if (File.Exists(baseDirFile)) File.Delete(baseDirFile); } catch { /* best effort */ }
                }
                try { Directory.Delete(targetRoot, recursive: true); } catch { /* best effort */ }
            }
        }
    }

    [Fact]
    public void Prepare_BaseDirectory有procdump_优先于TargetPath()
    {
        // 独立安装语义：tier 探测在 watchdog 目录命中后，Prepare 必须解析同一位置，
        // 保证探测与启动用的二进制一致。
        lock (ProcdumpResolutionLock)
        {
            var baseDirFile = Path.Combine(AppContext.BaseDirectory, "Applications", "Windows", "procdump64.exe");
            var targetRoot = Path.Combine(Path.GetTempPath(), $"bowl-procdump-{Guid.NewGuid():N}");
            var targetDirFile = Path.Combine(targetRoot, "Applications", "Windows", "procdump64.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(baseDirFile)!);
            var originalBytes = File.Exists(baseDirFile) ? File.ReadAllBytes(baseDirFile) : null;
            try
            {
                WriteStubExecutable(baseDirFile);
                Directory.CreateDirectory(Path.GetDirectoryName(targetDirFile)!);
                WriteStubExecutable(targetDirFile);

                var ctx = CreateContext(targetPath: targetRoot);
                var startInfo = new WindowsBowlStrategy().Prepare(ctx);

                Assert.NotNull(startInfo);
                Assert.Equal(NormalizePath(baseDirFile), NormalizePath(startInfo!.FileName));
            }
            finally
            {
                if (originalBytes != null)
                    File.WriteAllBytes(baseDirFile, originalBytes);
                else if (File.Exists(baseDirFile))
                    File.Delete(baseDirFile);
                try { Directory.Delete(targetRoot, recursive: true); } catch { /* best effort */ }
            }
        }
    }

    /// <summary>
    ///     写入假 MZ 头文件：杀软实时扫描会短暂占用刚落盘的可执行文件，写入需重试。
    /// </summary>
    private static void WriteStubExecutable(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.WriteAllBytes(path, new byte[] { 0x4D, 0x5A });
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(250);
            }
        }
    }
}
