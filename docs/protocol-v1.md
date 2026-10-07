# Bowl host disk protocol v1

This is the consumer contract for the GeneralUpdate producer's
`docs/bowl-integration-v1.md`. No shared assembly, IPC package or server change
is required. Unknown versions and mismatched attempt IDs are rejected.

## Launch, identity and file ownership

```text
Bowl.Host --attempt <canonical-lowercase-GUID> --state-root <absolute-directory>
```

Records are UTF-8 JSON with camelCase properties. Timestamps are UTC ISO-8601;
process start times retain their precision. A process identity is
`{"pid":1234,"startTimeUtc":"2026-10-07T05:00:00+00:00"}`. PID reuse cannot
satisfy identity checks. Access denied is not interpreted as process exit.

Each attempt uses `<state-root>\attempts\<GUID>\`:

| File | Only writer | Purpose |
| --- | --- | --- |
| `request.json` | Update, once before launch | Immutable configuration |
| `producer.json` | Update | Latest phase, process identity and original failure |
| `ready.json` | Bowl | Same host PID/start time that Update launched |
| `result.json` | Bowl | Durable decision and recovery journal |
| `outbox.json` | Bowl | One stable terminal event and delivery state |
| `host.lock` | Bowl | Exclusive attempt owner, held open |

`locks\<SHA256-of-canonical-install-path>.lock` coordinates attempts for the same
installation. Its `.owner.json` sidecar records the latest owning attempt
(`protocolVersion`, `attemptId`). File locks are not deleted to steal ownership.
An unresolved prior attempt or still-running prior updater prevents a new
attempt from becoming ready. A superseded attempt cannot later restore an old
snapshot. All updates of one installation must share a single state root.

Writers flush a temporary file in the same directory and atomically rename or
replace it. Readers use `FileShare.Read | FileShare.Delete` on Windows. Bowl's
fixed `.tmp` files bound crash debris; its exclusive lock prevents concurrent
writers. IPC, if added, must be notification only.

Bowl validates paths and identity access, acquires durable ownership, and writes
readiness before monitoring. Update validates version, attempt, exact host
PID/start time and liveness before applying files. Readiness failures are
`monitorUnavailable`, not application update failure.

Paths must be absolute, local, trusted and free of symlinks/junctions/reparse
points. Host, state and installation trees are disjoint. The state root must
also be outside updater, staging and backup trees (the producer checks updater
and staging locations). Never include it in packages or restoration scopes.

## Request and producer

`request.json`:

```json
{
  "protocolVersion": 1,
  "attemptId": "4b0a8245-204d-464a-b86b-65c8f4519a8e",
  "createdAtUtc": "2026-10-07T05:00:00+00:00",
  "updater": { "pid": 1000, "startTimeUtc": "2026-10-07T04:59:59+00:00" },
  "installPath": "C:\\Apps\\Example",
  "backupDirectory": "C:\\Apps\\Example\\.backups\\snapshot",
  "currentVersion": "1.0.0",
  "targetVersion": "2.0.0",
  "launchMode": "processAlive",
  "healthTimeoutSeconds": 5,
  "updateTimeoutSeconds": 600,
  "autoRollback": false,
  "report": {
    "url": "https://updates.example.test/report",
    "recordId": 42,
    "type": 1,
    "credentialEnvironmentVariable": "MYPRODUCT_REPORT_TOKEN"
  }
}
```

`launchMode` is `filesOnly` or `processAlive`. No application launch forces
`filesOnly`. `healthTimeoutSeconds` defaults to 5 (range 1..3600), and is a full
survival observation window. `updateTimeoutSeconds` defaults to 600 (range
1..86400), measured from request creation until completion/handoff. A restarted
host observes a new full survival window; it never credits time it did not
observe. Creation timestamps more than five minutes in the future are rejected.

`ready.json` has `protocolVersion`, `attemptId`, `host:{pid,startTimeUtc}`.

`producer.json` has `protocolVersion`, `attemptId`, `stage`, `updatedAtUtc`,
optional `application:{pid,startTimeUtc}` and optional `error` containing
`category`, `stage`, `exceptionType`, `message`, `stackTrace`, `hResult`,
`failedPath`. Extra fields are ignored. Known credential values are redacted
when Bowl copies failures into its result. Update owns redaction in its files.

Stages: `preparing`, `filesApplying`, `filesApplied`, `launching`,
`awaitingHealth`, `completed`, `failed`.

- `filesApplied` alone is not startup success.
- `filesOnly` requires `completed`; it confirms only file application.
- `processAlive` requires `awaitingHealth` and uninterrupted survival of the
  supplied application identity for the configured window.
- Updater exit before a terminal handoff is `updaterTerminated`. Exit after
  `completed`/`awaitingHealth` is allowed. Bowl re-reads producer state after
  observing exit to avoid racing its last atomic write.
- Process creation failure is `launchFailure`; exit/PID reuse during survival
  observation is `healthCheckFailure`. These are separate from `updateFailure`.
- Invalid/missing protocol data or inaccessible observation is
  `monitorUnavailable`, not an application failure.

`processAlive` means process survival, **not business health**. No `health.json`
signal is part of v1. No dump is ever used as success evidence.

## Result and recovery journal

`result.json` extends `protocolVersion`/`attemptId` with:

| Field | Values / meaning |
| --- | --- |
| `eventId` | `<attemptId>-terminal-v1`, stable across every retry/recovery |
| `host`, `updater`, `application` | Decision-time identities; application may be null |
| `outcome` | `success`, `updateFailure`, `launchFailure`, `healthCheckFailure`, `updaterTerminated`, `updateTimeout`, `monitorUnavailable` |
| `stage` | Last observed producer phase |
| `verification` | `none`, `filesApplied`, `processAlive` |
| `currentVersion`, `targetVersion` | Copied immutable request versions |
| `observedAtUtc` | Decision timestamp |
| `error` | Original structured producer failure, or monitoring exception type |
| `diagnostics` | `metadataOnly`; no implicit dump/system-information capture |
| `rollback` | `notRequested`, `pending`, `inProgress`, `deferred`, `succeeded`, `failed` |
| `rollbackError` | Bounded error classification, never credentials or response body |

The failure decision is flushed before recovery. `autoRollback` is opt-in and
only applies to actual update/startup failures, not `monitorUnavailable`.
Bowl is the sole rollback owner of a supervised update; Update must suppress
its own rollback.

The host waits up to 10 seconds for the exact updater to exit; it never kills
the updater merely because a timeout elapsed. If still alive, recovery becomes
`deferred`, no HTTP request is attempted, and the process returns exit code 2.
Resume with `--attempt` or a `--retry` sweep after the updater has exited.
The updater must not leave independent child processes writing installation
files. Such custom updater strategies need their own writer fencing.

Before restoring, Bowl stops only the recorded application identity and waits
for exit. Missing identity at an application handoff, identity-access failure
or a failed stop is an explicit rollback failure, not permission to restore
concurrently. The application must not delegate ongoing installation writes to
untracked children.

Backups are immutable **directory mirrors**, not ZIP files. A nested backup
must be a child of `install\.backups`. Restoration first checks the full trees,
rejects empty/recursive/linked snapshots, removes extra installed files, then
copies the mirror; `install\.backups` is preserved throughout. Trees exceeding
100,000 entries are refused before deletion. Snapshots must exclude `.backups`.
An `inProgress` journal survives host termination and causes the copy to restart
from the retained snapshot. I/O or permission failure records `failed`;
operators must inspect and repair it rather than treating it as restored.

File restoration is not a filesystem transaction. Power loss or I/O failure can
leave partial application files, but the external journal/snapshot remain for
recovery. Local atomic replacement and file flush are used; do not assume
distributed filesystem or hardware power-loss guarantees.

## Outbox and HTTP acknowledgement

Only after recovery settles does Bowl create `outbox.json`:

```json
{
  "protocolVersion": 1,
  "attemptId": "4b0a8245-204d-464a-b86b-65c8f4519a8e",
  "eventId": "4b0a8245-204d-464a-b86b-65c8f4519a8e-terminal-v1",
  "payload": { "recordId": 42, "status": 3, "type": 1 },
  "state": "pending",
  "attempts": 1,
  "nextAttemptUtc": "2026-10-07T05:00:10+00:00",
  "acknowledgedAtUtc": null,
  "lastError": "HTTP 503"
}
```

`state` is `pending`, `acknowledged`, or `localOnly`. No endpoint and
`monitorUnavailable` are local-only, never fake network acknowledgements.
The HTTP JSON shape is exactly `recordId`, `status`, `type`; status remains
2 Success / 3 Failure, type remains 1 poll / 2 push. Structured diagnostics and
dumps are never uploaded. `Idempotency-Key` carries the stable event ID; a
legacy server can ignore it, so delivery is **at least once**, not exactly once.

Each invocation sends at most once per eligible attempt. Before sending, persist
the attempt counter and next retry time (5, 10, 20, ... seconds, capped at one
hour). Each HTTP request has a five-second budget, disables redirects/cookies,
and accepts only 2xx. Failure persists a sanitized error code; response bodies
are not read into diagnostics. An acknowledged record is retained rather than
deleted, so restart does not recreate it. A crash after HTTP 2xx but before the
local acknowledgement can resend the same ID.

A crash after result persistence but before outbox creation is reconciled by
either entry point. `--retry <limit>` accepts 1..100, scans at most 10,000 attempt
directories, skips acknowledged/local-only/not-due records, and reports batch
exhaustion. It does not resume observations without a result; explicitly use
`--attempt` for those. Missing/locked/malformed attempts are reported and cannot
silently become successes.

No unbounded queue eviction occurs. New observation is refused above 10,000
attempts; lock registration is capped at 10,000 installations; each JSON record
is limited to 1 MiB. Operators must monitor disk space and archive old
acknowledged attempts. Keep the current per-installation owner attempt and its
request/result until superseded; missing ownership history requires manual
inspection and is never silently bypassed. Pending evidence is never deleted
automatically. An existing scheduler must invoke retries; there is no daemon
or newly installed system service.

## Credentials, deployment and exit codes

The optional credential environment-variable **name**, not its value, is in the
request. Bowl reads a Bearer token from its own inherited environment. Tokens
are only sent to HTTPS URLs. URLs cannot contain user information, query or
fragment. Never put secrets on the command line, in a URL or in the request.
Provision host credentials separately; Update's in-process HTTP callbacks
cannot cross the process boundary.

Protect local paths, stack traces and binaries with ACLs. The existing diagnostic
library and bundled procdump tools are a separate explicit opt-in, not a host
startup dependency. No full dump upload option is enabled by this protocol.

For `--attempt`, exit 0 means supervised application/file success; exit 1 means
a recorded non-success; exit 2 means invocation/ownership/storage failure or
deferred recovery. HTTP delivery is independent: **exit 0 is not delivery
acknowledgement**, inspect `outbox.state`. A retry sweep returns 2 on
refused/unavailable attempts or exhausted batch/scan limits; pending network
delivery remains visible in the outbox even when the sweep itself completed.
