using System.Net.Http.Headers;
using System.Net;
using System.Text.Json;

namespace Bowl.Host;

internal sealed class Delivery(HttpClient client)
{
    private sealed class ReportException(string code) : Exception
    {
        public string Code { get; } = code;
    }

    private static readonly HashSet<string> SafeExceptionTypes =
    [
        "System.Exception", "System.IO.IOException", "System.IO.FileNotFoundException",
        "System.IO.DirectoryNotFoundException", "System.IO.InvalidDataException",
        "System.UnauthorizedAccessException", "System.InvalidOperationException",
        "System.ArgumentException", "System.TimeoutException", "System.Net.Http.HttpRequestException",
        "System.ComponentModel.Win32Exception", "System.OperationCanceledException"
    ];
    private static readonly HashSet<string> SafeStages =
        ["preparing", "filesApplying", "filesApplied", "launching", "awaitingHealth", "completed", "failed"];
    private static readonly HashSet<string> SafeRollbackErrors =
    [
        "UpdaterStillRunning", "IOException", "FileNotFoundException", "DirectoryNotFoundException",
        "InvalidDataException", "UnauthorizedAccessException", "InvalidOperationException",
        "Win32Exception", "OperationCanceledException", "JsonException", "RecoveryPlanException"
    ];

    private static ReportDiagnostics CreateDiagnostics(Result result)
    {
        var exceptionType = result.Error?.ExceptionType;
        if (exceptionType == null || !SafeExceptionTypes.Contains(exceptionType))
            exceptionType = null;
        var stage = SafeStages.Contains(result.Stage) ? result.Stage : "unknown";
        var rollbackError = result.RollbackError != null && SafeRollbackErrors.Contains(result.RollbackError)
            ? result.RollbackError : null;
        bool? rollback = result.Rollback switch { "succeeded" => true, "failed" => false, _ => null };
        return new(result.Outcome, stage, rollback, rollbackError,
            exceptionType, result.Error?.HResult);
    }

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
                    result.Outcome == "success" ? 2 : 3, request.Report.Type)
                {
                    Diagnostics = result.Outcome is "success" or "monitorUnavailable"
                        ? null : CreateDiagnostics(result)
                },
                State = localOnly ? "localOnly" : "pending"
            };
            Disk.Write(path, outbox, ProtocolJson.Default.Outbox);
        }
        Disk.Validate(outbox, request.AttemptId);
        if (outbox.EventId != result.EventId ||
            outbox.Payload.RecordId != request.Report.RecordId ||
            outbox.Payload.Status != (result.Outcome == "success" ? 2 : 3) ||
            outbox.Payload.Type != request.Report.Type ||
            outbox.Payload.AppKey != null ||
            outbox.Payload.Diagnostics != null && outbox.Payload.Diagnostics != CreateDiagnostics(result) ||
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
            var payload = outbox.Payload;
            if (request.Report.AppKeyEnvironmentVariable is { Length: > 0 } appKeyName)
            {
                var appKey = Environment.GetEnvironmentVariable(appKeyName);
                if (string.IsNullOrEmpty(appKey))
                    throw new ReportException("AppKeyUnavailable");
                if (message.RequestUri!.Scheme != "https" &&
                    !(IPAddress.TryParse(message.RequestUri.Host, out var address) && IPAddress.IsLoopback(address)))
                    throw new ReportException("AppKeyRequiresHttps");
                payload = payload with { AppKey = appKey };
            }
            if (request.Report.CredentialEnvironmentVariable is { Length: > 0 } name)
            {
                var credential = Environment.GetEnvironmentVariable(name);
                if (string.IsNullOrEmpty(credential))
                    throw new InvalidOperationException("Report credential is unavailable.");
                if (message.RequestUri!.Scheme != "https")
                    throw new InvalidOperationException("Credentials require HTTPS.");
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            }
            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, ProtocolJson.Default.LegacyPayload);
            message.Content = new ByteArrayContent(bytes);
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            await ValidateAcknowledgementAsync(response, request.Report.AppKeyEnvironmentVariable != null, timeout.Token);
            // Only explicit 2xx acknowledgement changes delivery state. Keep the
            // record so restarts cannot recreate and resend acknowledged events.
            outbox = outbox with
            {
                State = "acknowledged",
                AcknowledgedAtUtc = DateTimeOffset.UtcNow,
                LastError = null
            };
        }
        catch (Exception ex) when (ex is ReportException or HttpRequestException or OperationCanceledException or IOException
                                       or InvalidOperationException or FormatException or JsonException)
        {
            // Exception messages may contain URLs/tokens. Persist only a code.
            var error = ex is ReportException report ? report.Code
                : ex is HttpRequestException http && http.StatusCode != null
                    ? $"HTTP {(int)http.StatusCode}" : ex.GetType().Name;
            Console.Error.WriteLine($"Report remains pending: {error}.");
            outbox = outbox with { LastError = error };
        }
        Disk.Write(path, outbox, ProtocolJson.Default.Outbox);
    }

    private static async Task ValidateAcknowledgementAsync(
        HttpResponseMessage response, bool requireBusinessAcknowledgement, CancellationToken token)
    {
        const int maximumBytes = 16 * 1024;
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        var buffer = new byte[maximumBytes + 1];
        var length = 0;
        int read;
        while (length < buffer.Length &&
               (read = await stream.ReadAsync(buffer.AsMemory(length), token)) != 0)
            length += read;
        if (length > maximumBytes)
            throw new ReportException("AcknowledgementTooLarge");
        if (length == 0 && !requireBusinessAcknowledgement)
            return; // Legacy endpoints may acknowledge with an empty 2xx response.
        if (length == 0)
            throw new ReportException("InvalidAcknowledgement");
        using var document = JsonDocument.Parse(buffer.AsMemory(0, length));
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new ReportException("InvalidAcknowledgement");
        var root = document.RootElement;
        if (!root.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.Number ||
            !code.TryGetInt32(out var value) || value != 200 ||
            !root.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.True ||
            (root.TryGetProperty("isSuccess", out var success)
                ? success.ValueKind != JsonValueKind.True : requireBusinessAcknowledgement))
            throw new ReportException("ReportNotAccepted");
    }
}
