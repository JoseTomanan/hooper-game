# ADR-0026 — Held post posture and a committed turnaround shot

- **Status:** Accepted
- **Date:** 2026-10-12
- **Superseded-by:** —

## Context

[#348](https://github.com/JoseTomanan/hooper-game/issues/348) records the
maintainer's 2026-10-11 approval of post entry/exit, controlled backward
movement and one committed turnaround finish, including bounded authoritative
facing away from the rim. Protection #345 and contact #370 have landed.
This is a slice under #355; M11 remains on hold.

The approved Agent Brief and ADR-0002/0003/0008/0009/0010/0012/0023/0025
govern this slice. Under ADR-0014 tier 2, back-to-basket positioning and a
planted turn to shoot ground the structure. Tier 3's commitment model grounds
finite braking and a visible Startup/Active/Recovery timeline. The references
establish structure, not final tuning or feel.

## Decision

`move_post` is a held request, initially T and D-pad Down. It is effective
only for a live Dribbling holder outside a committed move and reset. Post
takes precedence over protection and sprint, without their stacked benefits.
It never starts a dribble. Authority derives eligibility each tick.

An owed ordinary pivot completes first; its completion tick does not perform
a second post turn. Post then turns toward the floor-plane direction away
from the rim at a bounded rate, including at zero stick input. A zero rim
direction retains finite heading. Commanded translation is withheld until
facing is within 2 degrees of the away target. Existing velocity brakes
finitely through Decel. Established translation retains the stick's
world-space meaning, capped initially at 1.5 m/s in every direction (one
quarter of the 6 m/s neutral ceiling). Initial post turn rate is 360 degrees/s
with the existing non-linear profile. Exit resumes ordinary heading/pivot
rules from actual heading and velocity. Toggling does not erase pivot debt.

A fresh shoot edge in established post requests explicit move `turnaround`.
Early or invalid requests are rejected, without fallback. Each simulating
role snapshots a fixed rim target at accepted Begin; authority derives it
from its own position and rim, never client aim. Startup turns toward this
target at 360 degrees/s along the shortest signed arc; an exact 180-degree
tie chooses positive yaw. Subsequent input cannot retarget it. Startup
brakes through the existing finite GatherDecel.

Fixed, unfeintable frame data is 48/4/20 ticks at 60 Hz. The 0.8 s Startup
allows a maximum 0.5 s half-turn plus the existing 0.3 s shot windup. Active
and Recovery reuse jump-shot durations. Release is the single
JustEnteredActive pulse through the shot-family path. Begin cradles the
dribble once. Steal, OOB, contact and posture release may void the ball
payload but never cancel or re-phase the timeline. No fadeaway displacement
is added.

The existing Begin-speed sample and distance/movement/contest/facing/scatter/
seed/rim pipeline determine accuracy. Actual authoritative release heading
supplies facing; there is no post bonus, exemption or separate make model.
The unchanged contact kernel governs both movement paths.

Prediction records held post and historical eligibility/context per tick,
restores authoritative post state before replay, and reproduces turnaround
motion from supplied historical phase and fixed-target samples. Remote
presentation consumes authoritative effective posture, heading and move
identity. Latest raw opponent reuse remains an accepted approximation;
moving-opponent history and smoothing redesign are outside this slice.

Turnaround admission additionally requires explicit established entry state
and no owed pivot; an away-facing angle alone cannot clear a pending plant
through Begin. Buffered turnaround samples carry a Begin sequence token.
Once authority confirms that same token, its fixed target replaces the
predicted target for that instance's replay. Unconfirmed future instances
retain their own samples. Correction does not match stale phase counters.

Existing explicit JumpShot/Layup requests retain their admission contracts.
They receive no turnaround turn or post accuracy benefit; actual heading
feeds the ordinary facing penalty. Honest post input selects turnaround;
authority rejects an invalid turnaround without substituting another shot.
Rejecting old shot identities from a stale held-post packet would introduce
a release-and-shoot cross-channel race and change unrelated move contracts.

Distinct post/turnaround presentation belongs to this build, with committed
presentation taking precedence. Rendered artifacts support #173; magnitudes
remain provisional for #238. Neither artifact review nor state proof accepts
feel.

## Alternatives considered

- Cosmetic heading or independent aim: rejected; the rim-relative posture
  chooses facing while the stick retains world-space translation.
- Instant entry/turn or conditional phase stretching: rejected; these remove
  commitment or make the timeline unpredictable.
- Guaranteed back-down/strength leverage: deferred; set defenders deny
  progress under ADR-0025 without new contact tuning or solver collision.
- JumpShot fallback: rejected by ADR-0023's identity invariant.
- Accuracy immunity/parallel formula: rejected; orientation earns its effect
  through ADR-0009's existing facing term.
- Drop step, up-and-under, post fadeaway, fouls and stamina: outside this slice.

## Consequences

The spacing spine gains one bounded posture and finish. The held request,
historical replay context and remote effective state must stay synchronized.
Proof must discriminate dropped input, instant rotation, erased pivot debt,
contact bypass, fallback moves, duplicate release and accuracy exemption.
Full harness/network proof still gates closure; this record alone is not
implementation evidence.

## Clarification 2026-10-24 — Normal-shot exclusion and #428

The original exclusion of explicit JumpShot/Layup requests from the post
turnaround remains: neither gains post admission, the 48-tick Startup,
360-degrees/s constant-rate turn or any post accuracy benefit. #428 separately
approves normal JumpShot's own fixed-target Startup turn at the ordinary
non-linear heading rate, recorded in ADR-0010's 2026-10-24 amendment. Its
18/4/20 timing and pump-fake remain. Layup is unchanged. Both shot families
continue to evaluate ordinary accuracy from actual release heading.
