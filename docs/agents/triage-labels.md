# Triage Labels

The skills speak in terms of five canonical triage roles. This file maps those
roles to the actual label strings used in this repo's issue tracker.

This repo already runs a load-bearing `afk` / `hitl` convention
([ADR-0013](../adr/0013-afk-hitl-separate-issues.md)), so the two "ready" roles
reuse those existing labels rather than creating duplicates.

| Canonical role     | Label in our tracker | Meaning                                       |
| ------------------ | -------------------- | --------------------------------------------- |
| `needs-triage`     | `needs-triage`       | Maintainer needs to evaluate this issue       |
| `needs-info`       | `needs-info`         | Waiting on reporter for more information       |
| `ready-for-agent`  | `afk`                | Fully specified, ready for an AFK agent        |
| `ready-for-human`  | `hitl`               | Requires human (editor-level) verification     |
| `wontfix`          | `wontfix`            | Will not be actioned                          |

When a skill mentions a role (e.g. "apply the AFK-ready triage label"), use the
corresponding label string from the right-hand column.

## Notes

- As verified on 2026-09-05, all mapped labels already exist. Re-check with
  `gh label list` before creating any labels.
- The existing `blocked` label marks specified AFK work with unmet dependencies.
  Replace `afk` with `blocked` while an open prerequisite prevents pickup, and
  restore `afk` only after the prerequisites and any remaining scope gates are
  satisfied. Record the dependency using GitHub's native blocked-by relation
  as well as in the brief; a label alone does not name the blocker.
- Because `afk` and `hitl` are single-purpose (ADR-0013), never apply both to the
  same issue. `ready-for-agent` and `ready-for-human` are therefore mutually
  exclusive here too.

Edit the right-hand column if the vocabulary changes.
