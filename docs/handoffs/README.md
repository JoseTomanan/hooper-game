# Handoffs — cross-session scratch state

This folder holds **handoff documents**: the in-flight state one Claude Code
session leaves for the next when a piece of work spans multiple sessions
(typically a big change — networking, the ball physics — that won't finish in one
sitting).

**Everything in this folder except this README is gitignored.** Handoffs are
scratch, not durable documentation. They capture "where I am right now," which
goes stale the moment the work lands. Durable knowledge belongs in the other
three layers, not here:

- **Why an architecture choice was made** → an ADR in `docs/adr/`.
- **What a unit of work must achieve** → the GitHub issue (scope + acceptance
  criteria).
- **Why a specific change was made** → the commit body (with `Closes #X`).

## When to write one

When you stop mid-change and the next agent would otherwise have to re-derive
state that lives in none of the three durable layers above.

## What a good handoff contains

Only what is *not* already in AGENTS.md, the ADRs, the issues, or the code:

- The exact next task, and where you were interrupted.
- Build/run state ("compiles clean as of `<sha>`", "runs in-editor").
- Anything verified the hard way (e.g. an engine API checked against live docs
  because Context7 was unavailable — see ADR-0001).
- Gotchas and traps the next agent will otherwise hit.
- Remaining human (`hitl`) editor steps that only the user can do.

## Naming

`docs/handoffs/<topic>.md`, e.g. `M1b-networking.md`. One file per ongoing
strand of work; overwrite/update it as the strand progresses. Cleanup of existing
scratch requires separate explicit authorization; the audit below never removes
or rewrites files.

## Audit before pickup

Run `python tools/audit_handoffs.py` from the checkout before opening handoff
bodies. The tool inventories entries recursively in this directory, excluding
README documents, in sorted `active`, `stale`, and `unknown` groups with relative
filenames, per-entry reasons, and issue-state evidence. Discovery is bounded to
1,000 entries and eight directory levels; skipped subtrees are reported unknown
so a partial inventory cannot imply completeness. Only open active/unknown
handoffs relevant to the current task. A stale classification is
discovery guidance, not evidence that every statement in the handoff is obsolete
or permission to delete it.

The default repo root is the tool's checkout, independent of the current working
directory. `--repo-root <checkout>` selects another checkout, including an empty
isolated worktree. `--offline` disables GitHub lookup; `--json` emits the same
inventory as JSON. There is no prune, rewrite, or backfill mode in v1. CI tests
temporary fixtures and stubbed GitHub responses, never a developer's ignored
scratch files.

## Version 1 identity metadata

For newly authored Markdown handoffs, put this small front matter at the very
start of the file:

```markdown
---
handoff_version: 1
repo: JoseTomanan/hooper-game
issues: [410]
baseline: 1ed5122e7c6dc474a1131d363b750c299cef679e
---
# Interrupted work
```

`baseline` is optional; replace the example with the actual full 40-character
commit SHA, or omit the line. A baseline records context only and never proves
completion. `issues` is a JSON array of 1–16 positive integer issue numbers.
Duplicate numbers are deduplicated. `handoff_version`, `repo`, and `issues`
are required. This is a deliberately restricted key/value format, not general
YAML: unknown or duplicate keys, unsupported versions, invalid issue lists,
and another repository make identity unknown. Do not automatically backfill
legacy files.

The reader consumes at most 4,096 bytes and 32 lines, stopping at the closing
`---`. It never reads the body after that delimiter. An absent header permits
legacy filename discovery for top-level handoffs only: a single leading positive
issue number and an alphabetic topic, such as `410-handoff-audit.md`. Nested
legacy filenames are ambiguous because subdirectories can use chapter ordinals
(for example `anim-clips/10-spin.md`); they require explicit identity metadata.
Additional standalone numeric tokens make the name ambiguous
(`321-320-blender-ik-and-mirror.md`).
Malformed explicit metadata cannot fall back to a filename. Arbitrary body
references, probe filenames, modification age, and ancestral commits are not
identity or completion evidence. Non-Markdown scratch, unsupported directory
entries, links, unreadable files, and other unsupported entries are reported
unknown without opening their bodies. Redirected handoff roots are refused.

Each distinct issue is looked up once per run through the GitHub CLI. The adapter
has a 5-second per-request timeout, a 20-second total lookup budget, and at most
64 requests; remaining issues become unknown. It projects only issue number,
state, and the pull-request marker from the official
[GitHub issue endpoint](https://docs.github.com/en/rest/issues/issues#get-an-issue)
using [`gh api --jq`](https://cli.github.com/manual/gh_api).

All referenced issues must resolve to matching, complete issue responses before
classification: any missing, malformed, inaccessible, or incomplete response
makes the entry `unknown`, even if another issue is open. Otherwise any open
issue means `active`, and all closed means `stale`. Pull requests are not accepted
as associated issues. No identity means `unknown`.
