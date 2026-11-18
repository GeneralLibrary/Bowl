using System;
using System.Diagnostics;
using System.IO;
using System.Collections.Concurrent;
using System.Threading;

namespace GeneralUpdate.Bowl;

internal class TextTraceListener : TraceListener, IDisposable
{
    private readonly string _filePath;
    private readonly BlockingCollection<string> _messageQueue;
    private readonly Thread _loggingThread;
    private volatile bool _isDisposed;

    public TextTraceListener(string filePath)
    {
        _filePath = filePath;
        _messageQueue = new BlockingCollection<string>();
        _loggingThread = new Thread(ProcessQueue);
        _loggingThread.IsBackground = true;
        _loggingThread.Start();
    }

    public override void Write(string? message)
    {
        QueueMessage(message);
    }

    public override void WriteLine(string? message)
    {
        QueueMessage(message + Environment.NewLine);
    }

    private void QueueMessage(string? message)
    {
        if (!_isDisposed && message != null)
        {
            _messageQueue.Add(message);
        }
    }

    private void ProcessQueue()
    {
        var unavailable = false;
        foreach (var message in _messageQueue.GetConsumingEnumerable())
        {
            if (unavailable) continue;
            try
            {
                using (var fileStream = new FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                using (var writer = new StreamWriter(fileStream))
                {
                    writer.Write(message);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                unavailable = true;
                Console.Error.WriteLine($"GeneralTracer: file logging unavailable ({ex.Message}); continuing with console logging only.");
            }
        }
    }

    public void Dispose()
    {
        if (!_isDisposed)
        {
            _isDisposed = true;
            _messageQueue.CompleteAdding();
            _loggingThread.Join();
            _messageQueue.Dispose();
        }
    }
}