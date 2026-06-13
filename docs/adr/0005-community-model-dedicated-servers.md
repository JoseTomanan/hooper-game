# ADR-0005 — Community model: self-hosted dedicated servers + server browser

- **Status:** Accepted
- **Date:** 2026-05-28
- **Superseded-by:** —

---

## Context

The game needs a community model — a mechanism by which players find and play
against each other. Three broad options were considered:

1. **Official matchmaking / ranked ladder** — a central service matches players.
   Simple for players, but requires persistent infrastructure, operating cost,
   and a launch-day player pool to work. Ranked systems also carry design weight
   (MMR, seasons, enforcement) that is premature for a pre-launch project.

2. **Player-modding / content platform** (S&box model) — the community grows
   through user-created content. Rejected: player-modding is not a pillar for
   this game, and we are no longer on S&box anyway (see ADR-0001).

3. **Self-hosted dedicated servers + server browser / discovery** (CS 1.6 model)
   — players run their own server processes; a browser or discovery mechanism
   lists live servers. Requires no central infrastructure at launch. The
   community is self-sustaining through player-run servers.

The CS 1.6 model is the **load-bearing community property**: it creates
player-run communities, regional servers, and organic competitive scenes without
requiring the developer to operate matchmaking infrastructure. This is the right
fit for a solo-developer project.

## Decision

The community model is **self-hosted dedicated servers** with a **server
browser / discovery mechanism** (CS 1.6 style). Players find games by browsing
live server listings, not through automated matchmaking.

At launch:
- No official ranked / matchmaking. Ranked is a post-launch milestone.
- We build and ship: a dedicated-server export (headless Godot process), and a
  basic server browser / discovery (minimum: LAN broadcast; stretch: a simple
  public listing).

This is largely engine-independent; the architecture (dedicated server process,
server browser client, discovery protocol) is custom-built and would be similar
in any engine.

## Consequences

**Easier:**
- No central matchmaking infrastructure to operate at launch.
- Self-sustaining community: players who want to compete spin up their own
  servers; the developer is not a bottleneck.
- Fits naturally with server-authoritative netcode (ADR-0002): the dedicated
  server is just the authoritative process run headlessly.

**Harder / accepted tradeoffs:**
- We must ship a dedicated-server export and a server browser, both of which
  require engineering effort (Milestone 6).
- Cold-start problem: early players need to either host themselves or find
  someone hosting. No automated pool to draw from.
- Without official ranked, competitive integrity relies on community-run
  enforcement (admins, community rules). This is accepted — it is the CS 1.6
  model intentionally.
