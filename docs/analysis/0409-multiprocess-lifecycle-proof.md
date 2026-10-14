# #409 — Semantic readiness and shared peer lifecycle

- **Issue:** #409
- **ADRs:** ADR-0015/0016; no architectural decision changed
- **Date:** Measurements 2026-10-13; verification continued 2026-10-14
- **Purpose:** Record lifecycle failure controls and same-environment timings for
  seven runner families, covering ten catalog cases. This is state/tooling proof,
  with no gameplay, milestone, or feel acceptance.

## Method and retained evidence

The baseline is `3d4229ff25b7f3f55bc240ba602b91ce197fc1f3`. The same Windows
machine ran each affected case once, sequentially in the table's order, before
and after migration. Both runs built `IncludeIntegrationHarness=true` and used
Godot `4.7.1.stable.mono.official.a13da4feb`, Python 3.14.0, and
`C:/Program Files/Git/bin/bash.exe`. Commands were explicit subprocess argument
lists; elapsed time used `time.monotonic()` around the complete adapter invocation.
All ten cases exited zero in both runs; both exit-vector variants passed their
first attempt. The after measurement used commit `02797a1`.

Local evidence is retained under `.godot/409-baseline/` and `.godot/409-after/`:
`timings.json` records each command, elapsed time, exit and artifact paths;
each case's directory holds the adapter console and copies of native logs.
After-run directories also hold separate peer stdout logs. These scratch
measurement helpers and logs are not committed. The public reproduction surface
remains the catalog and unchanged shell entry points. For example:

```powershell
python tools/harness_catalog.py run --id net-handshake --godot "$env:GODOT" --bash "C:/Program Files/Git/bin/bash.exe"
python -m unittest discover -s tests/repository -p 'test_*.py' -v
dotnet build "HOOPER GAME.csproj" --configuration Debug -p:IncludeIntegrationHarness=true
dotnet test tests/Hooper.Ball.Tests/Hooper.Ball.Tests.csproj --configuration Debug
python tools/harness_catalog.py run --all --godot "$env:GODOT" --bash "C:/Program Files/Git/bin/bash.exe" --run-id 409-exhaustive
```

Resolve `GODOT` to the pinned console binary before running. In this session its
User-scope environment value supplied the path. Native logs remain workspace-local
and every peer/attempt receives a UUID filename. Existing catalog artifact globs
and CI artifact uploads discover both stdout and native logs without metadata
changes. Ports, scenario arguments, case IDs, tags, timeouts, sharding and paired
controls retain their existing contracts.

## Timing results

Times are seconds, rounded to three decimals. Each replacement readiness time
includes launch/startup until the successful server signal is observed.

| Catalog case | Before | After | Observed readiness wait |
|---|---:|---:|---:|
| net-handshake | 7.180 | 3.464 | 2.595 |
| net-state-sync | 7.233 | 3.046 | 1.984 |
| net-node-replication | 6.894 | 2.900 | 2.012 |
| net-behindtheback-sweep | 8.096 | 4.131 | 2.039 |
| net-defensive-telegraph-telegraph | 8.221 | 4.244 | 2.063 |
| net-defensive-telegraph-control | 8.442 | 4.486 | 2.037 |
| net-exitvector-rpc-poisoned | 9.071 | 5.211 | 2.059 |
| net-exitvector-rpc-steady | 9.132 | 5.222 | 2.038 |
| net-rig-presentation-left | 8.195 | 4.669 | 2.249 |
| net-rig-presentation-right | 8.147 | 4.574 | 2.268 |
| **Total** | **80.610** | **41.948** | **21.343** |

The original scripts imposed **60 seconds** of unconditional bind sleep over
these ten invocations. Those sleeps are removed. Observed replacement readiness
waiting totaled **21.343 seconds**, leaving **38.657 seconds** of redundant fixed
waiting in this sample. Independently, complete adapter times improved by
**38.663 seconds** in aggregate. This is one local serial before/after sample,
with no statistical uncertainty estimate; boot caching and scheduling can vary.
It does not measure hosted workflow critical-path savings. The numbers are not
portable to another runtime, machine, workload, retry count or catalog selection.

`SERVER_BIND_WAIT` is retired and prints a retirement notice when supplied.
It no longer changes launch timing. `HARNESS_READY_TIMEOUT` instead sets a
positive finite readiness deadline (default 15 seconds). `HARNESS_PORT`, scenario
defaults and exit-vector `MAX_ATTEMPTS` (default 3) remain available.

## Readiness and verdict controls

`tests/repository/test_multiprocess_harness.py` uses real Python children to test
delayed readiness, successful socket bind before client connect, server death,
missing readiness, false readiness, missing/wrong-role PASS, PASS followed by
failure/crash, timeout, stale-log isolation, partial launch, interruption,
descendant cleanup after leader exit, late auxiliary crash during cleanup, and
interruption during final evidence validation. Both cleanup races were first
reproduced red, then passed after the fix. `test_network_harness_policy.py` tests
server/two-peer ownership, deterministic retry exhaustion and rejection of
readiness, crash, unrelated-assertion and auxiliary failures as retry candidates.

The real Godot handshake was also mutated temporarily using the exact existing
entry point, with retained results in `.godot/409-controls/results.json`:

| Mutation/control | Inputs | Observed failure |
|---|---|---|
| Suppress the post-bind READY print | `HARNESS_READY_TIMEOUT=6`, `HARNESS_PORT=24509` | Exit 1; readiness timeout at 6.013 s; server peer-up and suppression marker present; client never launched |
| Print READY before bind and suppress the real marker; force bind failure | Same 6 s deadline, port 24511 occupied by exclusive dual-stack IPv6 UDP socket | Exit 1 in 2.706 s outer elapsed; `CreateServer` returned `CantCreate`; server RESULT FAIL; no client PASS |

The exclusive socket used `AF_INET6`, `IPV6_V6ONLY=0`,
`SO_EXCLUSIVEADDRUSE=1`, bound to `('::', 24511)`. An earlier IPv4-only occupation
did not force Godot's bind to fail; its retained attempt is excluded from the
bind-failure evidence. Source was restored byte-for-byte and the harness-enabled
assembly rebuilt green before the positive after-run and exhaustive sweep.

READY is only launch sequencing. Required proof peers must naturally exit zero
and supply the existing role-specific scenario PASS marker. Client-owned families
remain client-owned, exit-vector remains server-owned, and rig presentation
requires both peers. A naturally exited auxiliary must also show its own normal
completion evidence; an intentional auxiliary stop is recorded separately from
the pre-cleanup exit. Observed auxiliary crashes/assertion failures invalidate a
green proof, including at the actual cleanup boundary.

Exit-vector retries accept only its documented composition-divergence failure,
with natural server exit 1 and the specific FAIL/RESULT evidence. Each attempt
retains independent logs and reaps peers before another attempt. A deterministic
divergence fails every bounded attempt. Other failure classes are not retried.

## Implementation sources and containment

The local Python runtime is 3.14.0; the implementation uses APIs also supported
by the repository's Linux CI Python. Official sources establish the mechanisms:

- [Python subprocess argument lists, file redirection and bounded waits](https://docs.python.org/3.14/library/subprocess.html#subprocess.Popen): no shell interpretation or unread PIPE buffer; stdout/stderr goes to retained files.
- [Python monotonic clock](https://docs.python.org/3.14/library/time.html#time.monotonic): deadlines cannot reverse on wall-clock adjustment.
- [Python signals](https://docs.python.org/3.14/library/signal.html#signal.signal): CLI TERM becomes an interruption; finalization defers further signals until every peer is stopped.
- [Python process groups](https://docs.python.org/3.14/library/os.html#os.killpg): the catalog's shared cleanup helper kills remaining POSIX group members even after their leader exits. Default inner cleanup is bounded below the catalog's three-second TERM grace.
- [Godot ENet server creation](https://docs.godotengine.org/en/4.7/classes/class_enetmultiplayerpeer.html#class-enetmultiplayerpeer-method-create-server) and [C# signals](https://docs.godotengine.org/en/4.7/tutorials/scripting/c_sharp/c_sharp_signals.html): raw harness markers follow `Error.Ok` and peer installation; production harnesses subscribe to the existing `ServerStarted` before startup, without changing production networking.
- [Windows Job Objects](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects): descendants remain owned after their leader exits; kill-on-close also contains them if the adapter dies. The peer starts suspended, is assigned before it can spawn descendants, then resumes through [documented thread discovery](https://learn.microsoft.com/en-us/windows/win32/api/tlhelp32/ns-tlhelp32-threadentry32) and [ResumeThread](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-resumethread).

## Verification status

All ten affected cases and their listed variants/controls passed before and after.
Both ordinary and harness-enabled game builds passed with zero warnings/errors.
Full unit tests passed 1,200 with five intentional characterization skips.
The repository suite ran 82 tests successfully, with one Windows platform skip
for the POSIX outer-termination test; skill mirror synchronization also passed.

Two incomplete local `--all` attempts are retained and excluded from proof. The
first stopped without a catalog summary after 227 native-log cases; its cause is
unidentified. The second restart overlapped the ordinary build during a session
limit/pause race and encountered a missing harness class. Verification resumed
only after an explicit harness-enabled rebuild and confirmation of no leftovers.
The authoritative four catalog shards completed sequentially, each with unbuffered
output, unique run IDs and retained `.godot/409-exhaustive-shardN.log`/`.json`.
The aggregate `.godot/409-exhaustive-shards-summary.json` confirms exactly 384
unique cases, with no missing or extra cases: 382 passed. Shards 2, 3 and 4 passed
88/88, 108/108 and 99/99 respectively. All ten migrated cases passed.

Shard 1 ran all 89 selected cases: 87 passed and two failed. Neither failed family
is migrated by #409, and their scripts, scenes and production code are unchanged:

- `dedicated-game-journey-score-rpc-disabled`: client-a emitted its scenario PASS,
  then crashed in `godotsharp_internal_object_get_associated_gchandle` through
  `GodotObject.Dispose(Boolean)`/`Finalize()`. The adapter correctly rejected its
  exit 139. Evidence: `.godot/harness-logs/dedicated-game-score-rpc-disabled-z1nGlk/`
  and `.godot/dedicated-game-score-rpc-disabled-700ZJX/`. An older upstream report
  contains the [same finalizer signature](https://github.com/godotengine/godot/issues/83247);
  that match alone does not establish this crash's root cause.
- `committed-replay-crossover-contact-0ms`: the existing sample guard rejected
  `rows=39 corrections=10 committedCorrections=1 startup=1 active=0`, rather than
  accepting vacuous Active-phase evidence. Native evidence is retained under
  `.godot/committed-replay/native/d05db3555fed/` and the matching catalog trial.

These failures have not been waived or silently retried. Baseline `main` at
`3d4229f` has a [successful hosted CI run](https://github.com/JoseTomanan/hooper-game/actions/runs/38241197766).
The [hosted branch run at `a6ae2b6`](https://github.com/JoseTomanan/hooper-game/actions/runs/38490830866)
passed build/unit/repository checks, rendered evidence and shards 2–4. Shard 1
passed 88/89; its sole failure was `committed-replay-gather-contact-0ms`, another
unchanged replay probe. Its diagnostics recorded Startup/Active/Recovery maximum
correction of zero; the trial failed. The workflow artifact omitted that probe's
native-log directory, so those diagnostics do not establish the exact native
failure message or root cause. The aggregate integration check is red.

Two independent final reviews of `a6ae2b6` found no high-confidence introduced
correctness defects: one covered lifecycle/containment/cleanup, the other
scenario contracts/readiness/proof ownership/retry policy. Focused tests passed
in both reviews. No production, relay or scenario assertion changes were made
to address the unrelated exhaustive failures. This final evidence-only update
does not waive them or turn a retry into proof. Compile or READY alone is never
proof; the local exhaustive and initial hosted integration gates are not green.
