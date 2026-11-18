using GeneralUpdate.Bowl;

namespace BowlTest;

public class GeneralTracerFallbackTests
{
    [Fact]
    public void FileListener_ContainsBackgroundWriteFailures()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bowl-missing-{Guid.NewGuid():N}", "trace.log");
        var listener = new TextTraceListener(path);
        listener.WriteLine("first");
        listener.WriteLine("second");
        listener.Dispose();
    }
}
