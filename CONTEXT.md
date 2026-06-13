# CONTEXT.md — Domain Glossary

Lookup table of project-specific terms. One entry per term: bold name, one-to-two sentence definition, `see:` pointer to the authoritative source. Not prose — not a design doc.

**Sources:** [CLAUDE.md](CLAUDE.md), [ADR-0001](docs/adr/0001-engine-godot-csharp.md) – [ADR-0005](docs/adr/0005-community-model-dedicated-servers.md).

Do not add design reasoning here. If a decision belongs somewhere, it belongs in an ADR.

---

## Core loop

**Duel** — The fundamental unit of play: a 1v1 contest resolved by footwork, spacing, and commitment reads. The game is built around this interaction, not around basketball simulation. see: [CLAUDE.md §1](CLAUDE.md)

**Spine / spacing spine** — The load-bearing layer of the duel: footwork and spatial positioning. All other systems (committed moves, timing windows, stamina) live inside this layer, not co-equal with it. see: [CLAUDE.md §1](CLAUDE.md)

**Separation creation vs. denial** — The core 1v1 contest. The ball-handler tries to create space between themselves and the defender; the defender tries to deny it. Every movement and committed-move decision exists in service of this contest. see: [CLAUDE.md §1](CLAUDE.md)

**Commitment / mind-game layer** — The layer on top of the spacing spine: both players read the other's intended move and commit to a response; wrong reads are punished. This layer exists because committed moves cannot be flow-cancelled. see: [CLAUDE.md §1](CLAUDE.md), [ADR-0003](docs/adr/0003-input-model-hybrid.md)

**Punish window** — The frames during a committed move's recovery phase where the opponent can act before the committing player regains control. Wrong reads are resolved here. see: [ADR-0003](docs/adr/0003-input-model-hybrid.md)

**Timing windows** — A subordinate system: discrete input windows for shot release, steal, and block. They live inside the spacing spine and are not co-equal with it. see: [CLAUDE.md §1](CLAUDE.md)

**Shot release timing window** — During a shot attempt, the window of frames where releasing the shoot input produces a "perfect" (green) result with maximum accuracy; early or late releases degrade the shot. Modeled on NBA 2K's shot meter / green-window system. see: [CLAUDE.md §1](CLAUDE.md)

**Steal timing window** — When a ball-handler is exposed (mid-dribble, off-balance, etc.), the discrete window where a steal attempt succeeds cleanly; outside it the attempt produces a foul or a failed reach. Modeled on NBA 2K's steal system. see: [CLAUDE.md §1](CLAUDE.md)

**Block timing window** — During a shot attempt, the discrete window where pressing block legally contests or rejects the shot; outside it the attempt results in a foul or a miss. Modeled on NBA 2K's block system. see: [CLAUDE.md §1](CLAUDE.md)

**Stamina / resource** — A visible resource bar that degrades from executing committed moves and active defense, modeled on UFC Undisputed 3's stamina system. Depleted stamina slows and weakens committed moves. Subordinate to the spacing spine — it constrains options without replacing the commitment/mind-game layer. see: [CLAUDE.md §1](CLAUDE.md)

---

## Movement & spacing

**Continuous neutral game** — The free-movement phase of play, driven by the left analog stick. Fluid and cancellable at any moment with no frame commitment. Sustains the spacing spine between committed moves. see: [ADR-0003](docs/adr/0003-input-model-hybrid.md)

**Left analog stick** — The input surface for movement, positioning, and change of pace. Drives the continuous neutral game only; committed moves are on discrete buttons or right-stick gestures. see: [ADR-0003](docs/adr/0003-input-model-hybrid.md)

**Change of pace** — Varying movement speed within the continuous neutral game to manipulate the defender's positioning. Driven by left stick. see: [ADR-0003](docs/adr/0003-input-model-hybrid.md)

---

## Committed moves / input

**Committed move** — A discrete action (crossover, spin, hesitation, drive, etc.) with real startup and recovery frames. Once initiated it runs to completion — it cannot be flow-cancelled. The mechanism of the mind-game layer. see: [ADR-0003](docs/adr/0003-input-model-hybrid.md)

**Startup frames** — The wind-up phase of a committed move before the active phase begins. Borrowed directly from Tekken's frame data model. Must be visibly telegraphed by animation; do not blend them away. see: [ADR-0003](docs/adr/0003-input-model-hybrid.md)

**Active frames** — The frames where the move's effect is live (in Tekken: hitbox present; here: the move executes — the drive attempt lands, the crossover passes through, etc.). Borrowed from Tekken's frame data model. see: [ADR-0003](docs/adr/0003-input-model-hybrid.md)

**Recovery frames** — Frames after the active phase during which the player cannot act. The punish window falls here. Borrowed from Tekken's frame data model: a move that is "negative on whiff" gives the opponent free frames to punish. see: [ADR-0003](docs/adr/0003-input-model-hybrid.md)

**Startup / active / recovery state machine** — The three-phase frame structure of every committed move: wind-up → execution → cooldown. Taken from Tekken's frame data system. Startup and recovery are named in ADR-0003; active is the locked middle-state name adopted from Tekken. see: [ADR-0003](docs/adr/0003-input-model-hybrid.md)

**Feint-cancel window** — A discrete window within a committed move's startup phase where the player can deliberately abort the move, turning it into a feint: the startup animation plays (telegraphing a threat and baiting the opponent's read) but the move does not complete. Modeled on UFC Undisputed 3's feint mechanic (modifier + strike button to abort mid-startup). see: [ADR-0003](docs/adr/0003-input-model-hybrid.md)

**Right-stick committed-move vocabulary** — The set of committed moves expressed via right-stick gestures, modeled on NBA 2K's Pro Stick system. Familiar input surface; moves resolve as locked commitments with startup/active/recovery frames, not flow. The full gesture-to-move list is a content decision deferred to later milestones; the mechanic is locked. see: [ADR-0003](docs/adr/0003-input-model-hybrid.md)

**Size-up-as-feint** — Deliberately inputting a right-stick dribble move (hesitation, crossover, between-the-legs, spin, etc.) for its startup animation only, then aborting it via the feint-cancel window before the active phase fires. The startup frames play and telegraph a threat; the defender reads and commits; the move does not complete; the ball-handler exploits the resulting opening. Modeled on NBA 2K14's Pro Stick, where every right-stick input is already a discrete committed move with visible startup — there is no held size-up stance, so every size-up is inherently committed. All right-stick moves are feint-eligible. The feint-cancel input is the same modifier used for all committed-move feints. see: [ADR-0003](docs/adr/0003-input-model-hybrid.md)

**Flow-cancel** — Prohibited. The ability to cancel a committed move mid-execution by redirecting input (the model of modern "smooth" sports games). Explicitly rejected because it eliminates the punish window and therefore the mind game. see: [ADR-0003](docs/adr/0003-input-model-hybrid.md)

**Clunky-but-readable / legibility** — A design value, not a bug. Committed moves use deliberately clunkier animation than a "polished" title so startup frames remain readable by the opponent. Smoothing them away is an explicit anti-goal. see: [CLAUDE.md §1](CLAUDE.md), [ADR-0003](docs/adr/0003-input-model-hybrid.md)

---

## Ball

**Deterministic mini-physics ball** — A hand-authored physics layer for all owned ball moments (dribble attach, shot arc, bounce, rim/backboard interaction). Does not use Godot Physics or Jolt. Produces bit-identical results on server and all clients, so reconciliation is clean and the ball never snaps. see: [ADR-0004](docs/adr/0004-deterministic-ball-physics.md)

**Ball states** — The explicit state machine governing the ball: held, dribbling, in-flight, loose. Transitions are explicit; each state's math is hand-authored and self-contained. see: [ADR-0004](docs/adr/0004-deterministic-ball-physics.md)

**Dribble attach** — The ball state where the ball is bound to a player's dribble cycle. Governed by the mini-physics layer, not the engine physics solver. see: [ADR-0004](docs/adr/0004-deterministic-ball-physics.md)

**Shot arc** — The parabolic trajectory of the ball in-flight toward the basket. Hand-authored math within the mini-physics layer; the feel of the arc is explicitly designed, not emergent. see: [ADR-0004](docs/adr/0004-deterministic-ball-physics.md)

**Rim / backboard interaction** — Explicit collision math between the ball and the rim or backboard. Hand-authored; no engine physics solver is involved at any point. see: [ADR-0004](docs/adr/0004-deterministic-ball-physics.md)

**Authoritative ball state** — The server's canonical record of ball position and ball-state. Clients predict ball movement locally but reconcile to this. Godot Physics / Jolt must never influence authoritative ball state — even "visual-only" effects must be purely cosmetic and client-side. see: [ADR-0004](docs/adr/0004-deterministic-ball-physics.md)

---

## Networking

**Server-authoritative** — The server owns the canonical simulation state and is the sole arbiter of truth. Clients never unilaterally set game state. see: [ADR-0002](docs/adr/0002-networking-server-authoritative.md)

**Client-side prediction** — Clients apply their own inputs locally before the server confirms them, so the controlling player experiences zero perceptible input lag. see: [ADR-0002](docs/adr/0002-networking-server-authoritative.md)

**Server reconciliation** — When the server's confirmed state arrives, the client compares it to its predicted state and corrects any divergence. Corrections should be rare and small if prediction is working correctly. see: [ADR-0002](docs/adr/0002-networking-server-authoritative.md)

**Lag compensation** — Server-side technique for evaluating hit/interaction timing against the world state as the client saw it (accounting for network latency), not the server's current time. see: [ADR-0002](docs/adr/0002-networking-server-authoritative.md)

**Tick loop** — The fixed-rate update loop driving server simulation and client prediction. Client inputs and server corrections are exchanged per-tick. see: [ADR-0002](docs/adr/0002-networking-server-authoritative.md)

**Rollback / GGPO** — A peer-to-peer networking model where both peers simulate ahead and re-simulate on divergence. Explicitly rejected: it is a peer↔peer model and does not fit a dedicated-server topology. see: [ADR-0002](docs/adr/0002-networking-server-authoritative.md)

**MultiplayerApi / ENet** — Godot's high-level multiplayer layer, used as transport and RPC plumbing only. Does not provide prediction or lag compensation; those are custom C# built on top of it. see: [ADR-0002](docs/adr/0002-networking-server-authoritative.md), [ADR-0001](docs/adr/0001-engine-godot-csharp.md)

---

## Community model

**Self-hosted dedicated servers** — Players run their own server processes; the developer operates no central server infrastructure at launch. see: [ADR-0005](docs/adr/0005-community-model-dedicated-servers.md)

**Server browser / discovery** — The mechanism by which players find live games: a listing of active servers (minimum: LAN broadcast; stretch: public listing). The CS 1.6–style replacement for automated matchmaking. see: [ADR-0005](docs/adr/0005-community-model-dedicated-servers.md)

**CS 1.6 model** — Shorthand for the self-hosted + server-browser community structure: player-run servers, organic regional scenes, no developer-operated matchmaking. Named after Counter-Strike 1.6. see: [ADR-0005](docs/adr/0005-community-model-dedicated-servers.md)

**Headless Godot process** — The dedicated-server export: a Godot instance running without a display, acting as the authoritative simulation host. see: [ADR-0005](docs/adr/0005-community-model-dedicated-servers.md)

**Cold-start problem** — The bootstrapping challenge of the CS 1.6 model: early players must host themselves because there is no automated player pool. Accepted tradeoff. see: [ADR-0005](docs/adr/0005-community-model-dedicated-servers.md)

---

## Engineering conventions

**Decision Discipline / engineering-law enforcement** — The rule that any architectural decision must be captured in a new or updated ADR in the same commit as the code that enacts it. Claude Code must flag — never silently resolve — contradictions with locked ADRs before writing code. see: [CLAUDE.md §3](CLAUDE.md)

**Locked ADR** — An ADR with `Status: Accepted`. Its decision may not be changed without explicitly revisiting it and updating the status and superseded-by fields. see: [CLAUDE.md §3](CLAUDE.md)

**partial class** — Every Godot node script is a C# `partial` class extending a Godot node type (e.g. `public partial class PlayerController : CharacterBody3D`). Mandatory; Godot's source-generator tooling requires it. see: [ADR-0001](docs/adr/0001-engine-godot-csharp.md)

---

