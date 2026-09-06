# Spike 0013 — rendered gameplay evidence capture (#365)

**Status:** First hosted attempt NO-GO; remediation pending rerun

## Contract

`tests/integration/RenderedEvidenceCapture.tscn` instances the shipped
`Main.tscn`. Its court, `Main/Camera3D`, `NetworkManager`, `Ball`, and spawned
`Player.tscn` rigs are therefore production nodes. The runner records a
stationary-to-moving dribble, both hand states and a direction change, a real
fadeaway shot, a pivot, and a BehindTheBack rendered by a separate ENet client.
It does not pronounce those clips good or acceptable.

Each PNG is saved only after `RenderingServer.frame_post_draw`, then validated
for dimensions, bytes, non-black/non-flat pixels, an in-frustum subject, the
same-tick live AnimationTree state, and the display move id. `manifest.json`
records the commit, engine version, tick rate, renderer setting, exact camera
transform/FOV, input and commit events, state projections, output byte lengths,
and the render frame at which each image was saved.

## Repeat and mutation controls

`tests/integration/run-rendered-evidence.sh` creates a fresh output root, runs
the local scenario twice, and compares event physics frames and camera framing.
It deliberately runs `--capture-mutation=missing-intended-state`: that asks the
runner to observe a nonexistent AnimationTree state, so the command must fail.
If it succeeds, the shell wrapper fails the experiment. Pixel identity is not
compared across runs because it is not a cross-GPU claim.

The remote sample uses a real server and client process. The client requires
that it is not the server, that `Players/1` is remote, that the remote display
move id is `behindtheback`, and that its own live AnimationTree actually reaches
a BehindTheBack state before writing the image. A single offline process cannot
satisfy those checks.

## Commands

On Linux with Godot 4.7.1 .NET and Xvfb:

```bash
chmod +x tests/integration/run-rendered-evidence.sh
tests/integration/run-rendered-evidence.sh godot
```

The command uses `xvfb-run`, Mesa software OpenGL, `--rendering-method
gl_compatibility`, and `--write-movie`. Those are command-line experiment
settings, not `project.godot` changes. The images, AVI capture, manifests, and
logs are written under a new `mktemp` root locally, or `rendered-evidence/` in
CI. The CI job uploads that root for seven days even when the experiment fails.

Godot documents that a viewport texture can be black or stale before
`frame_post_draw`; the runner waits for that signal before `GetImage().SavePng`.
Godot also documents `Camera3D.IsPositionInFrustum`, `IsPositionBehind`, and
`UnprojectPosition`, which the subject-framing gate uses. See [Viewport capture
timing](https://docs.godotengine.org/en/4.7/classes/class_viewport.html) and
[Camera3D projection](https://docs.godotengine.org/en/4.7/classes/class_camera3d.html).

## Hosted result — GO/NO-GO

**NO-GO, first attempt:** GitHub Actions run
[34005408933](https://github.com/JoseTomanan/hooper-game/actions/runs/34005408933),
job `rendered-evidence-spike`, failed after 9 seconds. Its uploaded artifact is
`rendered-evidence-34005408933`. The exact command was
`tests/integration/run-rendered-evidence.sh godot` with
`RENDERED_EVIDENCE_ROOT=rendered-evidence` and `HARNESS_PORT=23461`.

The blocker was the Movie Maker writer creating `capture.avi` before the scene
entered `_Ready`; the freshness guard then mistook that writer-owned file for
stale evidence and stopped before any gameplay frame. The initial failure path
also tried to serialize camera provenance before resolving `Main/Camera3D`.
Both are runner defects, not a hosted-renderer conclusion: the guard now allows
only that expected pre-created AVI and manifest creation tolerates
pre-initialization failure. A rerun is required before the experiment can make
any claim about Xvfb/Mesa rendered capture.

A result is **GO** only when the rendered-evidence job exits zero and its
uploaded artifact contains two passing local manifests, a passing remote-client
manifest, non-empty PNG/AVI files, and both expected failing mutation logs.
Otherwise it remains **NO-GO** with the current blocker recorded here; the local
command remains useful in either outcome.

## Visual observations

Pending artifact inspection. The future observation table must record frame and
image filename separately from inferred cause and from any human-only judgment.
In particular, a visible animation defect is an observation; a claim about why
it occurred is an inference; “feels right” remains unproven feel for #173.

## Relationship to deferred visual work

This supplies evidence for [#301](https://github.com/JoseTomanan/hooper-game/issues/301)
without changing #301's explicit human-directed reference override or closing
it. It may also make asynchronous review easier for #178, #184, #185 and #173;
none of those visual/feel issues is accepted by this spike.
