# Dedicated game journey evidence

- Issue: #375; parent #366; original verification #32
- Governing decisions: ADR-0002, ADR-0007, ADR-0008, ADR-0016
- Date: 2026-10-07
- Purpose: record objective dedicated-game proof and its environment limits.
  Exported-build and cross-machine checks remain open; this evidence does not
  accept feel or close #32.

## Method

`DedicatedGameJourneyTest` loads the shipped `Main.tscn` in three separate
Godot .NET processes. The server uses the production `--dedicated --port`
bootstrap. Each client discovers its endpoint through `DiscoveryBroadcaster`
and the production browser, then activates that exact row. No direct harness
`JoinGame` call supplies a game address. Same-host socket isolation uses loopback
beacons, distinct scenario ports and fresh temporary coordination/log directories.
Client B starts after A closes its exclusive discovery listener.

Production `Input.ActionPress`/`ActionRelease` drives positioning, dribbling and
shooting. The target remains the scene's shipped **5**. Every trip requires:

1. A client dribble request, observed authoritative `Dribbling`, scorer holder,
   cleared possession and `HasDribbled == false`.
2. Movement release and settling, then one shoot press and actual input release.
3. A new authoritative `InFlight` edge, witnessed after those input receipts.
4. Exactly one point, observed consecutively by the server and both clients.
5. For makes 1–4, a synchronous fresh `Held` award to the same scorer, cleared
   and with the dead-dribble flag reset. The next trip's real input must start
   a live dribble; eligibility alone cannot pass.

Trip 5 must finish **5–0**, game-over, winner equal to the scorer and holder **0**
on the server. Both clients wait for a *new raw authoritative ball packet* after
observing game-over before acknowledging holder 0. Reliable score packets and
unreliable ball packets have independent arrival order. Terminal possession
events remain forbidden during the server's observation window.

The score mutation is armed only after both clients confirm a healthy replicated
two-player roster and 0–0 baseline. The server makes a real basket while both
clients must retain the entire 0–0/no-winner/nonterminal score mirror. A harness
partial observes actual `BallController.ReceiveState` calls and raw payloads,
independently of prediction. Each client must receive multiple new snapshots
after `server-scored`, including a new receipt after a 0.6-second observation
boundary, with the scorer's cleared holder. A frozen stream cannot pass. The
optional observer call and its state disappear from game-only builds.

No harness code assigns scores, winners or holders as proof. Coordination files
only carry receipts and barriers. The server releases clients after verifying
both proof receipts and waits for both PASS receipts before quitting. Release
closes the proof window; later disconnect-driven roster cleanup cannot reopen it.
Shell watchdogs reject nonzero role exits even after a printed PASS. EXIT cleanup
uses TERM, a three-second grace bound, then KILL and reap for surviving roles.

## Sources

The project pins `Godot.NET.Sdk/4.7.1` and targets .NET 8; the local runtime is
`4.7.1.stable.mono.official.a13da4feb`, SDK 8.0.421, Git Bash 5.2.37.

- [Godot 4.7 Input action simulation](https://docs.godotengine.org/en/4.7/classes/class_input.html#class-input-method-action-press):
  changes the polling state; it does not call `_Input`. Production player input
  polls `Input.GetVector` and action edges. The 0.3 dribble input is above the
  shipped 0.2 movement deadzone.
- [Godot 4.7 Node readiness](https://docs.godotengine.org/en/4.7/classes/class_node.html#class-node-method-ready):
  children become ready before their parent, so configure sockets on detached
  `Main` before adding it to the live tree.
- [Godot 4.7 RPCs](https://docs.godotengine.org/en/4.7/tutorials/networking/high_level_multiplayer.html#remote-procedure-calls)
  and [channels](https://docs.godotengine.org/en/4.7/tutorials/networking/high_level_multiplayer.html#channels):
  the observer reads the existing authority-only, remote-only ball receiver;
  no new wire protocol or authority decision is introduced.
- [GNU Bash signal handling](https://www.gnu.org/s/bash/manual/html_node/Signals.html):
  `wait` alone has no deadline. Cleanup escalates before reaping instead of
  waiting forever after TERM.

## Mapping to #32

| Original criterion | Automated evidence | Remaining environment claim |
|---|---|---|
| Headless server binary runs without a display window | Real Godot .NET binary runs `Main.tscn` with `--headless`, through production dedicated bootstrap, throughout the game. | A separately exported dedicated-server executable and its packaging/preset are not exercised. |
| Client discovers the server in the browser without manual IP | Both clients observe and activate the exact production row; the mismatched-listener control binds successfully but sees no row despite a subsequent beacon. | Loopback delivery does not prove limited broadcast across machines, firewall rules or physical LAN interfaces (ADR-0007). |
| Client connects via the browser and plays a game to win condition | Both browser-joined production players participate; five real shot trips, four live fresh possessions and authoritative/replicated 5–0 winner/holder agreement. | Rendered client builds and human feel remain outside this state proof. |

Keep #32 open for its exported-build/cross-machine residue. No Godot editor
setup is needed to reproduce #375's automated evidence.

## Reproduction and artifacts

Finish the build before launching any runtime; never replace its assembly during
a harness run. On Windows, resolve `GODOT` from its User-scope environment value
and select Git Bash explicitly if `bash` on PATH points to WSL.

```powershell
dotnet build "HOOPER GAME.csproj" --configuration Debug -p:IncludeIntegrationHarness=true
dotnet test "tests/Hooper.Ball.Tests/Hooper.Ball.Tests.csproj" --configuration Debug
$taskGodot = [Environment]::GetEnvironmentVariable('GODOT', 'User')
python tools/harness_catalog.py run --id dedicated-game-journey-healthy --godot $taskGodot --bash "C:/Program Files/Git/bin/bash.exe"
```

The catalog keeps all three paired journey scenarios together and CI already runs
that family in one integration shard. Its failure uploader preserves per-role
console/engine logs under `.godot/harness-logs/dedicated-game-*` and all receipts
under `.godot/dedicated-game-*`. The cleanup regression is included in
`python -m unittest discover -s tests/repository` and uses a real TERM-ignoring
role, checking that it survives the grace premise and is then killed and reaped.

## Results

Final restored-source checks:

- Ordinary game-only build and integration-enabled build: zero warnings/errors.
- Full xUnit suite: **1,155 passed, 5 intentionally skipped, 0 failed**.
- Repository contracts: **61 passed**, including the real cleanup regression.
- Healthy journey: **three consecutive exit-0 runs**, with no rebuild between
  them; all five ordered dribble/release/flight/peer-score trips verified.
- Score suppression and mismatched discovery listener: **exit 0** for both.
  The score-stale clients received **38 and 29** new raw snapshots after the
  server-scored marker, while remaining 0–0/no-winner/nonterminal.
- An earlier run launched while its assembly build was still active and the
  server exited 139 after printing PASS. That run was rejected. Its cause was
  not established; none of the three final runs with completed builds crashed.

Final healthy logs are `dedicated-game-healthy-UkeyxU`,
`dedicated-game-healthy-fL5Ojs`, and `dedicated-game-healthy-sPoJj0` under
`.godot/harness-logs/`. Their coordination directories are
`.godot/dedicated-game-healthy-x79BO7`, `-kmkZMs`, and `-GiwtW4` respectively.
Final mutation-control logs are `dedicated-game-score-rpc-disabled-M4cICj` and
`dedicated-game-discovery-disabled-22nIGx`; corresponding coordination directories
are `.godot/dedicated-game-score-rpc-disabled-bm4vUh` and
`.godot/dedicated-game-discovery-disabled-Qs4VrG`.

Temporary mutations were applied one at a time, built completely before launch,
then restored byte-for-byte. Their nonzero exits are intentional RED evidence:

| Mutation | Scenario and positively established premise | Discriminating result |
|---|---|---|
| Set `HasDribbled = true` in `AwardPossession` instead of resetting it | Healthy baseline, real shot, authoritative score 1 | Server rejects the first reset: cleared `Held` possession has a dead dribble. Adapter exits 1. |
| Arm score suppression in `healthy` after the same baseline barrier | Both healthy client baselines; real authoritative score 1 | Server stays 1–0, both clients stay 0–0; healthy game times out and exits 1. |
| Freeze the raw receipt observer after its first flight and return to `Held` | Score-suppression scenario reaches both baselines, flight and actual server score 1 | Neither client can produce a stale-with-live-packets receipt; all roles time out, adapter exits 1. This is an observer-stall control, not a claim that the socket was disconnected. |

Local RED role logs are retained under these `.godot/harness-logs/` directories:
`dedicated-game-healthy-lvW65J`, `dedicated-game-healthy-esKHRI`, and
`dedicated-game-score-rpc-disabled-UcWlI1`. On timeout runs, later errors caused
by server disconnect are incidental; the proof is the pre-disconnect healthy
setup followed by the specific failed reset or score/receipt divergence.
