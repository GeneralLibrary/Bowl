# Diagnostic outbox protocol 1

The diagnostic watchdog and GeneralUpdate consumer share
`{install}/fail/{version}/{guid}/`. This is separate from Bowl.Host's
`outbox.json` protocol. Use a trusted local filesystem with atomic same-directory
rename and .NET cross-process file sharing; all participants must have access
to the same paths and permanent lock files. Network filesystems, untrusted
writers, and processes that ignore this protocol are not supported.

## Locking and publication

Case names are GUIDs in D format. Parse the GUID, format it as lowercase D, and
use its first two hexadecimal characters as the stripe: acquire exclusive,
nonblocking `FileStream(OpenOrCreate, ReadWrite, FileShare.None)` on
`{install}/fail/.locks/{stripe}.lock`. Never delete, rename, replace, or clean
up lock files, even when idle. Lock collisions serialize unrelated cases; busy
consumers skip and retry later. The watchdog chooses another GUID when its
stripe is busy. An OS process exit releases the lock, not the evidence.

The watchdog takes the stripe lock **before** creating the case and keeps it
through capture, report generation, and the optional synchronous hook. Hooks
must finish all writes before exiting and must not launch background writers
or manipulate the manifest, case directory, or lock files. Missing, failing,
timed-out or unconfirmed hooks leave an unpublished case. On success, the
watchdog writes and flushes `manifest.json.tmp`, renames it to
`manifest.json` in the same directory, and releases the lock. A process crash
or publication failure can leave an incomplete case; it is retained for
operator inspection, not automatically published.

The manifest retains schema 2 and adds the integer `outboxProtocol: 1`. It has
`guid`, `kind`, `targetName`, `version`, nullable `exitCode`, `createdAtUtc`,
`files`, `state` and `attempts`. The GUID matches the directory and version
matches its parent; target and version are supplied to `bowl watch`. `files`
contains top-level evidence file names present after the hook, excluding the
manifest and its temporary file; it may be empty. It is not a recursive list.

## Association boundary

Schema 2 cases are **unassociated**. The watchdog receives a process name,
installation path and version, not an authenticated upgrade attempt or
authorized record. Neither these values, timestamps, evidence, hook output,
nor fields added by a local writer establish an attempt-to-case relationship.
Do not infer one or send a record type or ID from them. Bowl.Host's separate
protocol 1 request and reporting outbox are not an authentication or
authorization bridge to this diagnostic outbox.

The current upgrade producer does not provide a protected, authorized
tenant/record/attempt context, an authenticated version handshake, or a fenced
owner for this purpose. A future association requires an agreed threat model,
trusted producer and consumer identities, protected credentials and transport,
authorization of tenant, application and record, replay protection, and an
exact binding to the attempt and case. The binding must be atomically published
with the case and frozen before the first upload. These are prerequisites, not
an implemented protocol. A test fixture or self-declared capability is not
proof that the production chain satisfies them.

An unassociated first upload cannot be retroactively associated by retrying
the same GUID: the current server's duplicate handling does not add or compare
the record binding. Deployments requiring association must not upload these
cases until a trusted end-to-end protocol exists. A future consumer must retain
cases with an explicit but unknown or invalid association rather than silently
upload them without it; legacy schema 2 cases remain unassociated.

The watchdog accepts schema 2 and rejects an explicitly different numeric
schema, but ignores unknown configuration keys. This is compatibility parsing,
not a security handshake: a newer producer must not rely on extra fields or
silently downgrade to an old watchdog. The lock protocol coordinates
cooperative participants only; it does not authenticate writers or hooks.

Consumers must acquire the stripe before inspecting a case and hold it while
validating, packaging, uploading, and deleting a successfully acknowledged
case. Treat manifest and paths as untrusted: reject invalid GUIDs, mismatched
identity, invalid or duplicate names, path traversal, links/reparse points,
missing files and unknown protocols. Persist the exact ZIP used for a case and
retry those same bytes: the server checks SHA-256 consistency for a GUID. Do
not include generated ZIPs, locks or manifests in the evidence list.

## Migration and retention

Before deploying, stop **all** old watchdogs and uploaders for the installation.
Old binaries do not honor locks. Consumers must default to skipping schema 2
cases without `outboxProtocol: 1`; absence of a busy lock cannot prove that an
old writer is finished. A consumer's explicit legacy migration mode may read
old cases under the stripe lock only after old writers and uploaders have been
stopped. Invalid IDs and incomplete or damaged cases require operator review.

No age- or count-based GC removes pending, giveup, unknown or incomplete
cases. To bound new directory growth, a permanent `.locks/admission.lock`
serializes count-and-create: at most 10 case directories per version and 100
across versions, including active and legacy cases. Existing over-limit data
is retained. Admission contention, storage errors or capacity exhaustion
cause an explicit warning and a degraded exit-code-only watch for that target
session; the application and supervisor continue, but that session has no
dump or outbox case. Capacity is checked again at the next watch session. This
limits case count, not bytes or pre-existing data; operators must monitor disk
space, restore upload service, and inspect/recover or explicitly dispose of
orphaned evidence. The watchdog never silently evicts it.
