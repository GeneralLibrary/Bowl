using System.Net;
using Bowl.Host;

namespace Bowl.Host.Tests;

internal sealed class Fixture : IDisposable
{
    public string Base { get; } = Path.Combine(Path.GetTempPath(), "BowlHostTests", Guid.NewGuid().ToString("N"));
    public string Root => Path.Combine(Base, "state");
    public string DirectoryPath => Path.Combine(Root, "attempts", Request.AttemptId);
    public Request Request { get; set; }
    public FakeProcesses Processes { get; } = new();
    public RecordingHandler Handler { get; } = new();
    private readonly HttpClient client;

    public Fixture()
    {
        Request = new Request
        {
            AttemptId = Guid.NewGuid().ToString("D"), CreatedAtUtc = DateTimeOffset.UtcNow,
            Updater = new(12345, DateTimeOffset.UtcNow),
            InstallPath = Path.Combine(Base, "install"),
            BackupDirectory = Path.Combine(Base, "install", ".backups", "snapshot"),
            LaunchMode = "filesOnly", HealthTimeoutSeconds = 1, UpdateTimeoutSeconds = 5,
            Report = new() { Url = "http://localhost/report", RecordId = 42 }
        };
        Directory.CreateDirectory(DirectoryPath);
        Directory.CreateDirectory(Request.InstallPath);
        client = new HttpClient(Handler);
    }

    public Supervisor Supervisor => new(Processes, new Delivery(client), TimeSpan.Zero);
    public void Save() => Disk.Write(Path.Combine(DirectoryPath, "request.json"), Request, ProtocolJson.Default.Request);
    public void Producer(string stage, ProcessIdentity? application = null, Failure? error = null) =>
        Disk.Write(Path.Combine(DirectoryPath, "producer.json"), new Producer
        {
            AttemptId = Request.AttemptId, Stage = stage, UpdatedAtUtc = DateTimeOffset.UtcNow,
            Application = application, Error = error
        }, ProtocolJson.Default.Producer);
    public Task<Result> Run() { Save(); return Supervisor.RunAsync(Root, Request.AttemptId, CancellationToken.None); }
    public Outbox Outbox => Disk.Read(Path.Combine(DirectoryPath, "outbox.json"), ProtocolJson.Default.Outbox)!;
    public void Snapshot()
    {
        Directory.CreateDirectory(Request.BackupDirectory!);
        File.WriteAllText(Path.Combine(Request.BackupDirectory!, "application.txt"), "old");
        File.WriteAllText(Path.Combine(Request.InstallPath, "application.txt"), "new");
        File.WriteAllText(Path.Combine(Request.InstallPath, "new-only.txt"), "remove");
    }
    public void Dispose()
    {
        client.Dispose();
        Directory.Delete(Base, recursive: true);
    }
}

internal sealed class FakeProcesses : IProcesses
{
    public bool Alive { get; set; } = true;
    public Func<ProcessIdentity, bool>? Probe { get; set; }
    public List<ProcessIdentity> Stopped { get; } = [];
    public bool IsAlive(ProcessIdentity identity) => Probe?.Invoke(identity) ?? Alive;
    public Task StopAsync(ProcessIdentity identity, CancellationToken token)
    {
        Stopped.Add(identity);
        return Task.CompletedTask;
    }
}

internal sealed class RecordingHandler : HttpMessageHandler
{
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
    public List<string> Bodies { get; } = [];
    public List<string> Keys { get; } = [];
    public Action? BeforeSend { get; set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        BeforeSend?.Invoke();
        Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
        Keys.Add(request.Headers.GetValues("Idempotency-Key").Single());
        return new HttpResponseMessage(Status);
    }
}
