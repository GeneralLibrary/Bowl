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
  "rollbackPlan": {
    "restoreFiles": ["Example.exe", "lib\\example.dll"],
    "removeFiles": ["lib\\new-module.dll"],
    "protectedPaths": ["data", "settings.json"]
  },
  "report": {
    "url": "https://updates.example.test/report",
    "recordId": 42,
    "type": 1,
    "appKeyEnvironmentVariable": "MYPRODUCT_APP_KEY",
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

`report.appKeyEnvironmentVariable` is the optional name of an environment
variable containing the application credential (1..256 characters, not
whitespace or control characters, and without `=`). Never put its value in a
protocol file. Bowl reads the value only at send time. Every retry process must
inherit the variable; if missing, delivery remains pending. App keys require
HTTPS, except literal loopback IP addresses for local integration testing.
Older requests may omit it.

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

**Automatic restoration is currently unavailable.** Bowl cannot yet verify
exclusive ownership of the installation tree or prevent another writer from
changing a file between validation and recovery. It returns `rollback: failed`
and `rollbackError: RecoveryPlanException` without stopping the application or
modifying installation files, regardless of the supplied plan. Existing
`inProgress` recoveries are also refused. A trusted per-installation fence,
enforceable permissions, crash-safe write-ahead identity records and coordinated
updater support are prerequisites for enabling restoration. The legacy
`rollbackPlan` lists paths but cannot establish content identity or protect
against concurrent writers; it must not be treated as authorization. Older
Bowl binaries may perform unsafe mirror deletion or unverified file restoration;
deploy this Host before testing. An `inProgress` journal is not automatically
replayed. A failed rollback blocks later attempts for the same installation;
operators must repair the installation and ownership state under an exclusive
maintenance procedure before admitting another update.

The repository also contains **unwired experimental primitives** for a future
Windows-controlled mode. `ControlledLease` uses a pre-existing shared registry
and persists an active owner separately from its exclusive file handle;
`ControlledJournal` persists individual monotonic recovery intents;
`ControlledWorker` assigns a suspended child to a kill-on-close Windows Job
before resuming it; and `ControlledTree` can pin and inspect one filesystem
entry's owner, DACL, reparse flag and link count; `HoldTree` holds such handles
for its root, existing descendants and ancestors. `ControlledChannel` can
check each side's actual Windows named-pipe peer PID and process start time.
These are not a public wire contract and do not authorize any installation
writes or recovery. In particular, the primitives do not yet establish trusted
service configuration, canonicalize all Windows path aliases, bind the peer to
the lease and Job, bind a journal to an immutable plan, or implement recovery.
The SID set passed to tree inspection must not originate from an untrusted
request. Do not configure production systems to depend on them.

An isolated `ControlledRecovery` algorithm is exercised by tests only. It
expects a separately authenticated journal with original and updated SHA-256
hashes and an immutable snapshot, refuses conflicting file contents, and
persists recovery intent before each file replacement or deletion. It is not
wired to `--attempt`: hash checks alone cannot prevent a concurrent writer
between a check and filesystem mutation. A durable, exclusive security boundary
and a plan/journal binding are mandatory before any production integration.

The `Bowl.TestPeer` test executable has a separate `test-recover` command for
cross-repository process tests, not an authorization or production entry point.
It accepts a test root under the current temporary directory at
`BowlControlledIntegration/<guid>`, that same canonical attempt GUID, and a
GeneralUpdate `state/apply-<guid-without-hyphens>.json` plan. It uses only
the test root's `install`, `backup`, `stage`, and `state` directories, and
requires the backup to mirror the original relative paths. It persists a
separate recovery journal and a SHA-256 binding to the exact plan bytes. The
test caller must keep the entire tree isolated from other writers. Exit code 0
means recovery completed; exit code 2 means it was refused or failed. The
`--pause <index> <intent|applied|complete>` option is solely for process-kill
testing. This does not establish safety against concurrent filesystem changes,
service identity, malicious plans, or power loss.

The test peer also exposes `test-supervise <root> <mode> [worker-exe]` on
Windows. It is an isolated fixture, not a production service or reporting
protocol. It holds a test lease, launches one worker in a kill-on-close Job,
authenticates both pipe process identities, and sends `GO` only after `READY`.
The worker receives `test-controlled-worker <pipe> <host-pid> <host-start-O>
<root> <attempt> <mode>`; pipe messages are UTF-8 lines `READY`, `GO`, and
`DONE`. A successful worker must send `DONE` and exit 0. Failure with exactly
one plan triggers the test recovery adapter, and produces
`state/test-supervisor-result.json` with `outcome` and `rollback`. An exit code
of 0 indicates success or successful recovery; 2 indicates refusal or failure,
and early errors need not produce a result file. The 15-second deadline covers
handshake, execution and exit. Each attempt is single-use: after supervisor
death or any failure, it cannot be restarted via this fixture.

This fixture does not establish trusted ACLs, service identity, authenticated
payloads, a trusted global registry, crash-safe job handoff or production
result delivery. The worker's Job terminates it on supervisor death, but the
pipe alone cannot interrupt a synchronous filesystem operation. Production
controlled execution remains unavailable and must fail closed.

The optional fixture-only `state/test-report.json` configures a literal HTTP
loopback `url`, numeric `recordId` and `type`, and optionally
`appKeyEnvironmentVariable`. After a terminal test result, the supervisor writes
the request, result, and existing Host outbox under `state/reporting`, then
uses the Host delivery implementation. `test-report-retry <root>` retries that
outbox when due; it does not resume a worker or recovery. These files and the
test credential must not be used for production. A network failure leaves a
pending outbox, while acknowledged reports require the existing business
acknowledgement when an app key is configured.

For future controlled admission, the machine registry selector now rejects
locations other than the Windows ProgramData known folder's
`Bowl/ControlledRegistry`, and fresh lease admission refuses an active owner
even for the same attempt. Neither validates the registry permissions on its
own nor proves a previous Job has stopped. The replacement-compatible tree
inspection pins directories and validates existing files without retaining
their handles. It does not establish ACL inheritance, guard newly created
entries, prevent untrusted mutation by itself, or constitute production
authorization. Fresh admission only detects replay against the current owner
record, not all historical attempts.

The experimental `Bowl.Host --controlled-check <absolute-config-path>` checks
bootstrap configuration and service identity but **always exits 2** without
starting a worker or modifying an installation. The path must be chosen by the
service installer, not an untrusted request. This is a diagnostic probe, not
an admission, execution, or worker authorization protocol.

Bowl does not restart the old application. The request lacks a verified old
executable identity, launch arguments and working directory, and there is no
durable restart/ownership protocol to prevent duplicate launches after a crash.
An external owner may arrange restart after verifying a successful recovery and
ensuring the updater and other installation writers are stopped.

## Outbox and HTTP acknowledgement

Only after recovery settles does Bowl create `outbox.json`:

```json
{
  "protocolVersion": 1,
  "attemptId": "4b0a8245-204d-464a-b86b-65c8f4519a8e",
  "eventId": "4b0a8245-204d-464a-b86b-65c8f4519a8e-terminal-v1",
  "payload": {
    "recordId": 42, "status": 3, "type": 1,
    "diagnostics": {
      "outcome": "updateFailure", "stage": "failed", "rollback": true,
      "rollbackError": null, "exceptionType": "System.IO.IOException", "hResult": -1
    }
  },
  "state": "pending",
  "attempts": 1,
  "nextAttemptUtc": "2026-10-07T05:00:10+00:00",
  "acknowledgedAtUtc": null,
  "lastError": "HTTP 503"
}
```

`state` is `pending`, `acknowledged`, or `localOnly`. No endpoint and
`monitorUnavailable` are local-only, never fake network acknowledgements.
The HTTP JSON retains `recordId`, `status`, `type`; status remains 2 Success /
3 Failure, type remains 1 poll / 2 push. The HTTP body includes `appKey`
when the configured environment variable is available; its value is not
persisted in the outbox.
Failures additionally include `diagnostics` with outcome, last known stage,
rollback (`true` for succeeded, `false` for failed, `null` for not requested)
and bounded rollback error, a whitelist of standard exception
types, and numeric HRESULT if available. Arbitrary error category, message,
stack trace, paths, and dumps are never uploaded. A missing or unrecognized
exception type or rollback error is null; an unrecognized stage is `unknown`.
Success without an appKey retains the original three-field shape. A server must
ignore unknown fields to accept extended reports; if it rejects them the event
remains pending. Existing persisted legacy outboxes are retried unchanged (and
thus cannot be retroactively enriched with diagnostics). AppKey is added at
send time even for an old outbox if its immutable request specifies the variable.
`Idempotency-Key` carries the stable event ID; a legacy server can ignore it,
so delivery is **at least once**, not exactly once.

Each invocation sends at most once per eligible attempt. Before sending, persist
the attempt counter and next retry time (5, 10, 20, ... seconds, capped at one
hour). Each HTTP request has a five-second budget, disables redirects/cookies,
and accepts an empty 2xx response only for legacy requests without an appKey
environment variable. For requests with an appKey, it requires a JSON response
with `code: 200`, `body: true`, and `isSuccess: true`. Other nonempty legacy
responses require `code: 200`, `body: true`, and, if present, `isSuccess: true`. Other
nonempty responses remain pending, including HTTP 200 business failures.
Responses are limited to 16 KiB and never copied into diagnostics. Failure
persists a sanitized error code. An acknowledged record is retained rather than
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
