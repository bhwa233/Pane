# Pane Traffic Service

This x64 .NET Framework 4.8 sidecar is built separately from the NativeAOT Pane app.
Its ETW event reader, process attribution, aggregation, storage and named-pipe server come
from Galaxy Tools `fd8f11eaa55322f5d2d1c02d234d48596afe41f2`, under `native/traffic`.
The original terms are in `LICENSE.galaxy-tools.txt`. The locked project graph retains
TraceEvent 4.0.0 and Microsoft.Data.Sqlite 10.0.14. The Pane UI connects through
the pipe without linking this project. The standard installer does not ship it.

```powershell
dotnet restore src/Pane.Traffic/Service/Traffic.Service.csproj --locked-mode
dotnet build src/Pane.Traffic/Service/Traffic.Service.csproj -c Release --no-restore
```

Build a standalone internal x64 ZIP with PowerShell 7 and the .NET SDK:

```powershell
pwsh -NoProfile -File src/Pane.Traffic/build-preview.ps1
```

Each run uses a fresh directory under `out/traffic-preview` (or `-OutputRoot`).
The ZIP contains the Release service and runtime dependencies, the two NuGet lock
files, and license/notice texts provided by the restored packages. The adjacent
manifest gives the archive and per-file SHA-256 hashes. The ZIP is for local
testing only, not a reviewed redistribution bundle. It does not include Pane,
register a service, request elevation, or enable collection.

Add `-WithSetupPreflight` to build a separate NativeAOT `Pane.TrafficSetup.exe`
in the run's `setup-preflight` folder. The build runs `--verify-payload` against
the ZIP and manifest embedded in that executable; it does not read either file
from the working directory. Exit codes are 0 for a valid payload, 1 for payload
failure, and 2 for an unsupported command. For a signing candidate, pass
`-SetupSignerSha256` with the SHA-256 digest of the release signing certificate's
DER bytes. Sign the finished EXE separately, then run `--verify-release`. It checks
Windows Authenticode trust and the certificate pin before checking the embedded
payload. An absent pin, untrusted signature, or different signer returns 3. The
build does not sign the executable or change certificate/device trust. The helper
is not part of the application installer and does not request elevation itself.
Run it directly in a terminal to see the failure stage and reason on stderr.

To exercise the same signature verifier without staging or installing anything,
provide a PE file with a trusted embedded Authenticode signature and a separate
unsigned PE file. Set `$signerPin` to the SHA-256 of the signing certificate's
DER bytes, not the file hash or certificate thumbprint.

```powershell
dotnet run --project tests/Pane.App.Tests -c Release -- --traffic-release-trust $signedFixture $signerPin $unsignedFixture
```

The test verifies the expected signer and rejects a different pin, the unsigned
file, and a signed copy with one code byte changed. It never executes the
fixtures or modifies certificate trust.

`--stage-release` additionally requires an administrator token (exit 4 if not
elevated after signature verification). It verifies the same embedded bytes,
creates a protected staging tree, and rechecks the file inventory, digests and
ACLs before renaming it to `%ProgramData%\Pane\traffic\service\sha256-<digest>`.
An existing version is verified and reused, never repaired in place. Staging
does not register, start or stop a service, create user grants, or write history.
Validate successful writes and rejection of a modified existing version in an
isolated Windows VM with a signed build and an administrator token.

For a first installation in that VM, run the signed helper with `--install-release`
from an administrator session. It stages the payload, registers `PaneTraffic`, and
grants `own` access to the account running the helper. An existing service is left
alone (exit 5). Before setting startup to automatic, the helper verifies the SCM
process and uses Pane's pipe client to read `own` status as the installing account.
This check does not enable collection or acquire a lease. On startup failure it
attempts to clean up only the newly registered service and any grant file it
created. It does not upgrade existing services.

Both first install and service startup reject a grant file with an untrusted
owner, access granted outside SYSTEM and Administrators, or invalid own-scope
entries. Inheritance from the protected data directory is allowed. Grant values
must be `false` and keys must be canonical Windows account SIDs. An invalid
existing file is not rewritten or silently accepted by the installer.

### First-install acceptance on a Windows VM

Use a disposable VM with a trusted signed helper and a dedicated test account.
From an elevated session for that same account, run `--check-first-install`
before `--install-release`. The preflight reports the signature result, effective
account and elevation, whether `PaneTraffic` is already registered, and the
verified embedded archive's SHA-256. It does not write to the service directory,
create grants, or start, stop or register a service. Exit 0 confirms these checks
only; 3 means the release signature is not trusted, 4 means the session is not
elevated, and 5 means the service is already registered. Invalid payloads or
unreadable state return 1. Confirm the shown account is the intended Pane user;
successful preflight does not establish that the service will start or capture.
In PowerShell, invoke the helper with `&` and inspect `$LASTEXITCODE` and stderr
after each command. Close Pane before testing so it cannot hold a
collection lease. Run the following from a non-elevated shell in the same account:

```powershell
dotnet run --project tests/Pane.App.Tests -c Release -- --traffic-service-check
dotnet run --project tests/Pane.App.Tests -c Release -- --traffic-service-capture
```

The first command is read-only. It uses the same SCM and named-pipe identity checks
as Pane and requires a successful `own` status without acquiring a lease. Exit 3
means `PaneTraffic` is not installed; other failures return 1. The second command
requires collection initially disabled, generates bounded TCP traffic on `127.0.0.1`,
and checks that both directions appear for the probe's process instance in live data
and persisted history. It disables capture again on exit, but retains test history.
A failing run, including on a machine that does not produce usable loopback events,
does not pass the capture check. Run the read-only check again after a Windows reboot;
review actual Pane panel/card behavior, second-account isolation and performance separately.

For a repeatable performance load, build `tests/Pane.App.Tests` once in Release,
then run its executable with `--traffic-loopback-load` from the measured account.
It transfers 600 fixed 32 KiB chunks on loopback over roughly 60 seconds and logs
the PID, UTC sampling window, elapsed time and payload bytes sent and received.
It does not start or contact either product's collector. Start the product's
collection before the run; check that the collector actually attributes both
directions to the probe. A successful transfer alone is not an ETW result.

For a single internal installer containing Pane, native capture, and this same
archive, run `scripts/package.ps1 -WithNativeCapture -IsolatedPreview -WithTrafficService`
with the local build policy. The ZIP and its manifest are copied into `TrafficService/`
as inert files. The same command also exports the unsigned
`traffic-preflight/Pane.TrafficSetup.exe` alongside the installer, with the identical
ZIP and manifest embedded in the helper. `package.json` records the relative helper path;
`SHA256SUMS.txt` covers both executables. The installer does not install the helper,
unpack the ZIP there, register or start a service, request elevation for traffic
monitoring, or grant collection permissions.

The Windows service name is `PaneTraffic`, production pipe is `pane-traffic-v1`,
and its protected data directory is `%ProgramData%\Pane\traffic\data`. It does not
read Galaxy's data or use its Windows service. The installer must provide protected
code and data directories and an explicit SID grant before production use.

The local pipe uses UTF-8, newline-delimited JSON with `v: 1`, `id`, `op` and `scope: "own"`.
Responses echo `v` and `id`, and return `ok` plus `data` or `error`. The server resolves
the caller from its Windows pipe token and requires that SID to be granted. Live operations
are `status`, `scope.setEnabled`, `lease.acquire`, `lease.renew`, `lease.release`, and
`snapshot`. Renew on the same connection using the acquired `generation` and `collectorEpoch`.
The service measures lease expiry using Windows uptime rather than calendar time.
An enable or disable request persists the new state before publishing it. If that write
fails, the service responds with `scope-write-failed` and leaves the previous setting
in effect. A connected client can read `status` and retry without reconnecting.
The Pane client verifies the server PID against SCM and checks the protected service
configuration before writing to the pipe. If the current user already enabled
collection, the traffic module renews its lease while running and requests ranking
snapshots only while a ranking view is open. Pane requests `snapshot` with
`paginate: false` to read a bounded first page without retaining a cursor or
holding a snapshot for subsequent pages. Omit the field to use snapshot pagination.
The detail view uses `snapshot.ranking.active` for its active-only filter. It applies
the same search and sorting options as `snapshot.ranking`, but only includes rows
with a positive current receive or send rate before applying the row limit. Missing
rates do not count as activity; cumulative byte totals do not affect the filter.
With `rankingMode: "app-run"`, these ranking operations group matching rows by
`appRunId` before sorting and applying the row limit. Each application run's
totals appear once. Grouped rows report `appRunMemberCount` and up to 12
`appRunMembers` with the matching process instance IDs, PIDs and names. Search
by PID to narrow the result to a process that is not in the member preview.
The desktop card keeps its ordinary snapshot request.
The server returns no process rows and null rates if ETW is unavailable.
The read-only `history` operation queries the current caller's SQLite ledger without
acquiring a lease or enabling collection. It accepts ISO 8601 `from` and `to` timestamps,
`resolutionSec`, `limit`, and an optional `processInstanceId` from a snapshot or history
row. Filtering uses the instance identity rather than the PID and keeps coverage at the
account level. Reuse the same query with `nextCursor` to read another page. Cursors are
single-use, scoped to the caller and query, and expire
after 30 seconds. `recordedTotals.rxBytes` and `recordedTotals.txBytes` are decimal
integer strings for all stored rows in the filtered query, repeated unchanged on
each page. They do not account for unobserved traffic; use `coverageSummary`
separately to show gaps. Coverage and stored byte totals are distinct from live rate data.
`durableThrough` advances with a persisted batch of traffic or coverage, not with an
idle flush. An empty run checkpoint is only written when removing previously stored runs.
If a background history write or checkpoint fails, the service reports `history-write`
in status and stops writing history for that service run. It does not replay the failed
batch. Live sampling and reads of committed history continue; a service restart after
the storage problem is resolved starts recording new history.
`clearHistory` with `confirm: true` clears only the authenticated caller's time buckets
and coverage across all ranges. It invalidates that caller's history cursors and does
not change collection settings or live counters.

`connections` is an on-demand lookup for a process instance from the current user's
live rankings. Pass that `processInstanceId` and `kind: "tcp"` or `kind: "udp-local"`.
The service verifies the current owner and creation time before and after reading the
Windows endpoint tables. It returns `endpoints` and `truncated`, with at most 64 rows;
UDP contains local bindings only. It requires the account's process collection setting
to be enabled, but does not start ETW, acquire a lease, or persist endpoints. Machine scope
is not supported. The background collector does not scan endpoint tables or cache
addresses or ports.

For a non-elevated transport smoke check, launch the built executable with
`--console --isolate <absolute-temp-directory> --pipe pane-traffic-test-<unique-id>`
with stdin kept open. After `traffic-isolated-ready` on stderr, connect with the current
user token and request `PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize`
instead of generic write access. Closing stdin stops the process. This mode does not
install a service or prove that production ETW capture works. Only the isolated pipe
allows the console process's own SID to create subsequent pipe instances; the
production pipe does not grant that permission to ordinary clients.
