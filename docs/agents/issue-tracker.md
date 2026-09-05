# Issue tracker: GitHub

Issues and PRDs for this repo live as **GitHub issues** on
`JoseTomanan/hooper-game`. GitHub Issues is the sole task tracker (TASKS.md no
longer exists — see AGENTS.md §3). Use the `gh` CLI for all operations.

## Conventions

- **Create an issue**: `gh issue create --title "..." --body-file <utf8-file>`. Use a temporary UTF-8 file for multiline bodies so shell quoting preserves the exact text.
- **Read an issue**: `gh issue view <number> --comments`, filtering comments by `jq` and also fetching labels.
- **List issues**: `gh issue list --state open --json number,title,body,labels,comments --jq '[.[] | {number, title, body, labels: [.labels[].name], comments: [.comments[].body]}]'` with appropriate `--label` and `--state` filters.
- **Comment on an issue**: `gh issue comment <number> --body "..."`
- **Apply / remove labels**: `gh issue edit <number> --add-label "..."` / `--remove-label "..."`
- **Close**: `gh issue close <number> --comment "..."`
- **Parent / dependency links**: cite the parent and blockers in the brief AND set GitHub's native sub-issue / blocked-by relationships. Parentage does not imply a blocking dependency. Use issue database IDs (not issue numbers) with the REST `sub_issues` and `dependencies/blocked_by` endpoints; read back the links after writing them.

Infer the repo from `git remote -v` — `gh` does this automatically when run inside a clone.

## Repo-specific rules that bind these skills

These come from AGENTS.md §3 and the ADRs; the engineering skills must honour them:

- **`afk` vs `hitl` issues are single-purpose** ([ADR-0013](../adr/0013-afk-hitl-separate-issues.md)).
  An issue is *either* an `afk` build issue (closes on merge) *or* a `hitl`
  verify issue (closes only when proven). **Never file or leave an issue carrying
  both labels.** If work has a build half and a verify half, split it.
- **Done means proven, not written** ([ADR-0015](../adr/0015-autonomous-merge-proven-by-harness.md)/[ADR-0016](../adr/0016-headless-verification-harness.md)).
  A `hitl` issue whose acceptance criteria are state-checkable closes when the
  headless harness asserts them green in CI; irreducibly *feel* criteria close at
  the consolidated human-scheduled pass #173 (ADR-0021), except an issue with
  an explicit separate human-verification direction such as #301. Never close
  on code/compile alone, and never treat deferred feel as accepted.
- **Closing-keyword placement.** Exactly one artifact closes an issue and carries
  `Closes #X` in its *body* (never a commit subject line): a single-commit fix's
  commit body, or — for multi-commit work — the PR body. Commits on a branch use
  `Refs #X`, never a closing keyword.

## When a skill says "publish to the issue tracker"

Create a GitHub issue.

## When a skill says "fetch the relevant ticket"

Run `gh issue view <number> --comments`.
