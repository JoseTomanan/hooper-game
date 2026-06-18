# ADR-0002 — Networking model: server-authoritative + client prediction

- **Status:** Accepted
- **Date:** 2026-05-28
- **Superseded-by:** —

---

## Context

### Corrected reasoning — read this before treating the decision as obvious

An earlier version of this project justified "no rollback" by pointing at
S&box's simple peer-to-peer networking layer: rollback felt unnecessary there
because S&box handled synchronization differently. **That justification is void.**
We are no longer on S&box (see ADR-0001). The decision to use server-authoritative
prediction stands — but now for the right reasons: **dedicated-server architecture**.

### The actual choice

The game targets self-hosted dedicated servers (see ADR-0005). Two networking
models exist for real-time competitive games:

1. **Server-authoritative + client-side prediction + lag compensation** — the
   CS / Source lineage. The server owns truth; clients predict locally and
   reconcile to server corrections. Built for a client↔server topology.

2. **Peer rollback / GGPO** — both peers simulate ahead, roll back on
   divergence, and re-simulate. Built for a peer↔peer topology; each peer
   is simultaneously "the server" for its own inputs.

The project requires dedicated servers where one authoritative process drives
simulation. Rollback is a 2-peer model; adapting it to a dedicated-server
topology requires the dedicated server to also participate in rollback, which
adds significant complexity for no benefit — dedicated-server games already
get the key property rollback is chasing (no single peer favoured) by design.

### Godot-specific note

Godot's high-level multiplayer API (MultiplayerApi, MultiplayerSpawner/
Synchronizer, RPCs) is a transport + replication primitive layer. It does
**not** give you prediction or lag compensation for free. The tick loop,
client prediction, server reconciliation, and lag compensation are all custom
C# built on top of it. Treat the high-level API as transport only.

## Decision

Use **server-authoritative gameplay with client-side prediction and lag
compensation**, implemented in custom C# on top of Godot's MultiplayerApi/ENet.
Do **not** use peer rollback / GGPO.

The server owns the canonical simulation state. Clients predict locally so the
controlling player experiences zero input lag, then reconcile to server
corrections when they arrive.

## Consequences

**Easier:**
- Clean authority model: the server is the arbiter; no split-brain.
- Correct fit for dedicated-server topology (ADR-0005).
- Input lag is hidden from the controlling player via local prediction.

**Harder / accepted tradeoffs:**
- We hand-build the full prediction + reconciliation + lag-compensation stack in
  C#. This is the highest-risk technical system in the project; prove it in
  isolation as Milestone 1 before building anything on top of it.
- Godot's multiplayer layer gives us transport + RPC plumbing only; the rest is
  ours. This is accepted — we were always writing custom netcode.
- Rollback is permanently off the table unless the project moves away from
  dedicated-server architecture.

## Note — topology (added by ADR-0007)

This ADR's examples were written against the **listen-server** topology used through
Milestones 1b–5 (host = server + player 1). [ADR-0007](0007-dedicated-server-topology-discovery.md)
adds a **headless dedicated server with no local player**, where every player node is a
remote client driven through the same server-authoritative RPC path described here. The
authority model is unchanged — the server still owns all truth — the server simply has no
locally-controlled player. See ADR-0007 for the topology mechanics and the LAN discovery
protocol.
