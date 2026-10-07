using System.Net.Http.Headers;
using System.Text.Json;

namespace Bowl.Host;

internal sealed class Delivery(HttpClient client)
{
    public async Task DeliverAsync(string directory, Request request, Result result, CancellationToken token)
    {
        // No honest mapping exists for monitorUnavailable in the legacy API.
        // Result remains durable locally, but it must not become update Failure.
        var localOnly = result.Outcome == "monitorUnavailable" ||
                        string.IsNullOrEmpty(request.Report.Url);
        var path = Path.Combine(directory, "outbox.json");
        var outbox = Disk.Read(path, ProtocolJson.Default.Outbox);
        if (outbox == null)
        {
            outbox = new Outbox
            {
                AttemptId = request.AttemptId,
                EventId = result.EventId,
                Payload = new(request.Report.RecordId,
                    result.Outcome == "success" ? 2 : 3, request.Report.Type),
                State = localOnly ? "localOnly" : "pending"
            };
            Disk.Write(path, outbox, ProtocolJson.Default.Outbox);
        }
        Disk.Validate(outbox, request.AttemptId);
        if (outbox.EventId != result.EventId ||
            outbox.Payload != new LegacyPayload(request.Report.RecordId,
                result.Outcome == "success" ? 2 : 3, request.Report.Type) ||
            outbox.State is not ("pending" or "acknowledged" or "localOnly") ||
            outbox.Attempts < 0)
            throw new InvalidDataException("Outbox identity or payload mismatch.");
        if (outbox.State != "pending" || outbox.NextAttemptUtc > DateTimeOffset.UtcNow)
            return;

        var attempts = outbox.Attempts == int.MaxValue ? int.MaxValue : outbox.Attempts + 1;
        // Persist the retry schedule BEFORE sending. Repeated host crashes cannot
        // turn into a tight network loop, even between send and acknowledgement.
        outbox = outbox with
        {
            Attempts = attempts,
            NextAttemptUtc = DateTimeOffset.UtcNow.AddSeconds(
                Math.Min(3600, 5 * Math.Pow(2, Math.Min(attempts - 1, 10))))
        };
        Disk.Write(path, outbox, ProtocolJson.Default.Outbox);
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, request.Report.Url);
            message.Headers.Add("Idempotency-Key", outbox.EventId);
            if (request.Report.CredentialEnvironmentVariable is { Length: > 0 } name)
            {
                var credential = Environment.GetEnvironmentVariable(name);
                if (string.IsNullOrEmpty(credential))
                    throw new InvalidOperationException("Report credential is unavailable.");
                if (message.RequestUri!.Scheme != "https")
                    throw new InvalidOperationException("Credentials require HTTPS.");
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            }
            var bytes = JsonSerializer.SerializeToUtf8Bytes(outbox.Payload, ProtocolJson.Default.LegacyPayload);
            message.Content = new ByteArrayContent(bytes);
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            // Only explicit 2xx acknowledgement changes delivery state. Keep the
            // record so restarts cannot recreate and resend acknowledged events.
            outbox = outbox with
            {
                State = "acknowledged",
                AcknowledgedAtUtc = DateTimeOffset.UtcNow,
                LastError = null
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException
                                       or InvalidOperationException or FormatException)
        {
            // Exception messages may contain URLs/tokens. Persist only a code.
            var error = ex is HttpRequestException http && http.StatusCode != null
                ? $"HTTP {(int)http.StatusCode}" : ex.GetType().Name;
            Console.Error.WriteLine($"Report remains pending: {error}.");
            outbox = outbox with { LastError = error };
        }
        Disk.Write(path, outbox, ProtocolJson.Default.Outbox);
    }
}
