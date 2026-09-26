#!/usr/bin/env python3
"""Validated, repo-root anchored catalog for every headless harness invocation."""

from __future__ import annotations

import argparse
import math
import os
import re
import secrets
import shlex
import shutil
import signal
import subprocess
import sys
from dataclasses import dataclass, replace
from datetime import datetime, timezone
from pathlib import Path
from typing import Iterable, Sequence


ROOT = Path(__file__).resolve().parents[1]
_ID_RE = re.compile(r"^[a-z0-9]+(?:-[a-z0-9]+)*$")


@dataclass(frozen=True)
class SceneMetadata:
    resource: str

    @property
    def path(self) -> str:
        return self.resource.removeprefix("res://")


@dataclass(frozen=True)
class LogPolicy:
    kind: str
    value: str


@dataclass(frozen=True)
class HarnessCase:
    id: str
    topology: str
    scenes: tuple[SceneMetadata, ...]
    argv: tuple[str, ...]
    tags: tuple[str, ...]
    timeout_seconds: float
    log_policy: LogPolicy
    artifact_globs: tuple[str, ...]
    paired_control_ids: tuple[str, ...] = ()


_BASELINE_SINGLE_ROWS = """SmokeTest.tscn|
InputMapDefensiveActionsTest.tscn|
StealTurnoverTest.tscn|success
StealTurnoverTest.tscn|whiff
StealTurnoverTest.tscn|recovery-reset
HeldStealTest.tscn|held-vulnerable
HeldStealTest.tscn|held-immune-outside-window
HeldStealTest.tscn|pumpfake-now-exposed
HeldStealTest.tscn|held-static-vulnerable
HeldStealTest.tscn|held-static-immune-out-of-reach
HeldStealTest.tscn|held-static-immune-shielded
HeldStealTest.tscn|held-static-immune-wrong-side
TransitStealTest.tscn|transit-steal
TransitStealTest.tscn|out-of-reach-recovery
TransitStealTest.tscn|normal-window-unchanged
TransitStealTest.tscn|transit-steal-behind-the-back
TransitStealTest.tscn|out-of-reach-recovery-behind-the-back
TransitStealTest.tscn|transit-steal-between-the-legs
TransitStealTest.tscn|out-of-reach-recovery-between-the-legs
TransitStealTest.tscn|transit-steal-spin
TransitStealTest.tscn|out-of-reach-recovery-spin
StealFacingMappingTest.tscn|face-to-face
StealFacingMappingTest.tscn|side-by-side
BlockTurnoverTest.tscn|success
BlockTurnoverTest.tscn|success-lastactive
BlockTurnoverTest.tscn|whiff
BlockTurnoverTest.tscn|out-of-range
BlockTurnoverTest.tscn|control-make
BlockTurnoverTest.tscn|control-make-default-geometry
TerminalNoHolderTest.tscn|terminal-rebound
TerminalNoHolderTest.tscn|terminal-oob
TerminalNoHolderTest.tscn|nonwinning-make
TerminalNoHolderTest.tscn|live-rebound
TerminalNoHolderTest.tscn|oob-award
LayupTest.tscn|uncontested-make
LayupTest.tscn|block-success
LayupTest.tscn|block-whiff
LayupTest.tscn|range-gate-inside
LayupTest.tscn|range-gate-tolerated
LayupTest.tscn|range-gate-rejected
EuroStepTest.tscn|euro-step-beats-committed-defender
EuroStepTest.tscn|euro-step-read-still-contests
EuroStepTest.tscn|euro-step-whiff-recovery
EuroStepAnimTest.tscn|
JabStepTest.tscn|legal-held
JabStepTest.tscn|legal-dead-held
JabStepTest.tscn|illegal-dribbling
InAndOutTest.tscn|quick-return-empty-hand
InAndOutTest.tscn|quick-return-ball-hand
InAndOutTest.tscn|handside-unchanged
InAndOutTest.tscn|fallback-toward-ball-hand
InAndOutTest.tscn|dead-dribble-gate
InAndOutTest.tscn|feint-refused
InAndOutTest.tscn|reconstruct
SpinTest.tscn|rotation
SpinTest.tscn|handside-swap-timing
SpinTest.tscn|dead-dribble-gate
SpinTest.tscn|exit-burst-continues-entry-line
SpinTest.tscn|sweep-path
SpinTest.tscn|real-input-trigger
SpinTest.tscn|reconstruct
SpinTest.tscn|remote-exit-vector-preferred
ContestScatterTest.tscn|contest-active
ContestScatterTest.tscn|no-contest
FadeawayTriggerTest.tscn|mid-pivot
FadeawayTriggerTest.tscn|squared-up
BlowByWindowTest.tscn|whiff-triggers
BlowByWindowTest.tscn|suppressed
BlowByWindowTest.tscn|control
OobTurnoverTest.tscn|held-turnover
OobTurnoverTest.tscn|defender-exempt
OobTurnoverTest.tscn|both-oob
OobTurnoverTest.tscn|wall-placement
TripleThreatTest.tscn|dead-dribble
TripleThreatTest.tscn|production-drive
AwardFreshnessTest.tscn|stale-blocked-fresh-works
CradleRaceTest.tscn|out-of-order
CradleRaceTest.tscn|in-order
CradleRaceTest.tscn|later-legit-drive
StepBackCradleRaceTest.tscn|out-of-order
StepBackCradleRaceTest.tscn|in-order
StepBackCradleRaceTest.tscn|no-drive-ever
CrossoverSweepTest.tscn|crossover-sweep
CrossoverSweepTest.tscn|possession-change-no-sweep
CrossoverSweepTest.tscn|remote-handside-sweep
PivotPlantTest.tscn|exports
PivotPlantTest.tscn|flick-180
PivotPlantTest.tscn|held-135
PivotPlantTest.tscn|no-plant-boundary
PivotPlantTest.tscn|committed-cancel
PivotAnimTest.tscn|pivot-enters-exits
PivotAnimTest.tscn|control-locomotion
MoveKindAnimTest.tscn|clipped-reaches-permove
MoveKindAnimTest.tscn|unclipped-stays-generic
ReboundGrabTest.tscn|grab-fires
ReboundGrabTest.tscn|made-basket-no-grab
ReboundGrabTest.tscn|consecutive-rebounds
ReboundGrabTest.tscn|rebound-clip-contract
ReboundGrabTest.tscn|rebound-display-duration
ReboundGrabTest.tscn|rebound-latch-mutation-control
DribbleLoopTest.tscn|dribble-entered
DribbleLoopTest.tscn|no-ball-locomotion
DribbleLoopTest.tscn|held-no-dribble
DribbleLoopTest.tscn|move-outranks-dribble
DribbleHandAlignmentTest.tscn|hand-alignment-left
DribbleHandAlignmentTest.tscn|hand-alignment-right
DribbleHandAlignmentTest.tscn|hand-follows-authoritative-flip
DribbleHandAlignmentTest.tscn|dribble-states-own-clips
RigScaleHarnessTest.tscn|independent-scaling
LocomotionClipTest.tscn|
JumpshotAnimTest.tscn|jumpshot-phases
JumpshotAnimTest.tscn|fadeaway-active
JumpshotAnimTest.tscn|no-fadeaway-when-squared-up
JumpshotAnimTest.tscn|no-placeholder-leak
JumpshotAnimTest.tscn|jumpshot-airborne-active
JumpshotAnimTest.tscn|control-jumpshot-grounded-startup
JumpshotAnimTest.tscn|jumpshot-track-completeness
JumpshotAnimTest.tscn|fadeaway-leans-back
JumpshotAnimTest.tscn|control-jumpshot-active-is-upright
JumpshotAnimTest.tscn|fadeaway-segment-length
JumpshotAnimTest.tscn|fadeaway-track-completeness
CrossoverAnimTest.tscn|crossover-left-origin
CrossoverAnimTest.tscn|crossover-right-origin
CrossoverAnimTest.tscn|crossover-single-polarity
CrossoverAnimTest.tscn|no-unsuffixed-crossover-state
CrossoverAnimTest.tscn|crossover-track-completeness
CrossoverAnimTest.tscn|crossover-active-distinct-from-siblings
CrossoverAnimTest.tscn|crossover-polarity-content
BehindTheBackAnimTest.tscn|btb-left-origin
BehindTheBackAnimTest.tscn|btb-right-origin
BehindTheBackAnimTest.tscn|btb-single-polarity
BehindTheBackAnimTest.tscn|no-unsuffixed-btb-state
BehindTheBackAnimTest.tscn|control-unsuffixed-probe
BehindTheBackAnimTest.tscn|btb-segment-lengths
BehindTheBackAnimTest.tscn|btb-no-placeholder-leak
BehindTheBackAnimTest.tscn|btb-poses-the-skeleton
StealAnimTest.tscn|steal-left-reach
StealAnimTest.tscn|steal-right-reach
StealAnimTest.tscn|steal-constant-polarity
StealAnimTest.tscn|steal-face-to-face-reads-true
StealAnimTest.tscn|steal-segment-lengths
StealAnimTest.tscn|steal-startup-fills-window
StealAnimTest.tscn|steal-no-placeholder-leak
StealAnimTest.tscn|steal-poses-the-skeleton
StealAnimTest.tscn|no-unsuffixed-steal-state
StealAnimTest.tscn|control-unsuffixed-probe
LayupAnimTest.tscn|layup-phases
LayupAnimTest.tscn|layup-no-placeholder-leak
LayupAnimTest.tscn|layup-segment-lengths
LayupAnimTest.tscn|layup-edges
LayupAnimTest.tscn|layup-startup-differs-from-recovery
LayupAnimTest.tscn|layup-airborne-active
LayupAnimTest.tscn|control-layup-grounded-startup
LayupAnimTest.tscn|layup-arm-extends-overhead
LayupAnimTest.tscn|control-layup-arm-low-startup
ContestAnimTest.tscn|contest-phases
ContestAnimTest.tscn|contest-no-placeholder-leak
ContestAnimTest.tscn|contest-segment-lengths
ContestAnimTest.tscn|contest-edges
ContestAnimTest.tscn|contest-startup-differs-from-recovery
ContestAnimTest.tscn|contest-stays-grounded
ContestAnimTest.tscn|control-layup-leaves-ground
ContestAnimTest.tscn|contest-arms-rise
ContestAnimTest.tscn|control-contest-arms-low-startup
JabStepAnimTest.tscn|jabstep-phases
JabStepAnimTest.tscn|jabstep-no-placeholder-leak
JabStepAnimTest.tscn|jabstep-segment-lengths
JabStepAnimTest.tscn|jabstep-edges
JabStepAnimTest.tscn|jabstep-startup-differs-from-recovery
JabStepAnimTest.tscn|jabstep-torso-pitches-forward-in-active
JabStepAnimTest.tscn|control-jabstep-torso-modest-in-startup
JabStepAnimTest.tscn|jabstep-differs-from-retreatdribble
RetreatDribbleAnimTest.tscn|retreatdribble-phases
RetreatDribbleAnimTest.tscn|retreatdribble-no-placeholder-leak
RetreatDribbleAnimTest.tscn|retreatdribble-segment-lengths
RetreatDribbleAnimTest.tscn|retreatdribble-edges
RetreatDribbleAnimTest.tscn|retreatdribble-startup-differs-from-recovery
RetreatDribbleAnimTest.tscn|retreatdribble-torso-goes-back-in-active
RetreatDribbleAnimTest.tscn|control-retreatdribble-torso-modest-in-startup
RetreatDribbleAnimTest.tscn|retreatdribble-hips-stay-in-place
StepBackAnimTest.tscn|stepback-phases
StepBackAnimTest.tscn|stepback-no-placeholder-leak
StepBackAnimTest.tscn|stepback-segment-lengths
StepBackAnimTest.tscn|stepback-edges
StepBackAnimTest.tscn|stepback-startup-differs-from-recovery
StepBackAnimTest.tscn|stepback-active-displaces-back
StepBackAnimTest.tscn|control-stepback-startup-displaces-forward
StepBackAnimTest.tscn|stepback-recovery-hands-off-to-jumpshot
BlockAnimTest.tscn|block-phases
BlockAnimTest.tscn|block-no-placeholder-leak
BlockAnimTest.tscn|block-segment-lengths
BlockAnimTest.tscn|block-airborne-active
BlockAnimTest.tscn|control-block-grounded-startup
InAndOutAnimTest.tscn|inandout-phases
InAndOutAnimTest.tscn|inandout-no-placeholder-leak
InAndOutAnimTest.tscn|inandout-segment-lengths
InAndOutAnimTest.tscn|inandout-edges
InAndOutAnimTest.tscn|inandout-startup-differs-from-recovery
InAndOutAnimTest.tscn|inandout-stays-unsuffixed
InAndOutAnimTest.tscn|inandout-ball-hand-goes-in-then-out
InAndOutAnimTest.tscn|inandout-offhand-stays-out-in-active
InAndOutAnimTest.tscn|control-inandout-ballhand-does-come-in
BetweenTheLegsAnimTest.tscn|betweenthelegs-phases
BetweenTheLegsAnimTest.tscn|btl-right-origin
BetweenTheLegsAnimTest.tscn|btl-single-polarity
BetweenTheLegsAnimTest.tscn|no-unsuffixed-btl-state
BetweenTheLegsAnimTest.tscn|control-unsuffixed-probe
BetweenTheLegsAnimTest.tscn|betweenthelegs-segment-lengths
BetweenTheLegsAnimTest.tscn|betweenthelegs-no-placeholder-leak
BetweenTheLegsAnimTest.tscn|betweenthelegs-startup-differs-from-recovery
BetweenTheLegsAnimTest.tscn|control-identical-clips-read-as-identical
BetweenTheLegsAnimTest.tscn|btl-poses-the-skeleton
HesitationAnimTest.tscn|hesitation-phases
HesitationAnimTest.tscn|hesitation-no-placeholder-leak
HesitationAnimTest.tscn|hesitation-segment-lengths
HesitationAnimTest.tscn|hesitation-edges
HesitationAnimTest.tscn|hesitation-startup-differs-from-recovery
HesitationAnimTest.tscn|hesitation-clip-drives-the-rig
HesitationAnimTest.tscn|hesitation-active-raises-hips
HesitationAnimTest.tscn|control-hesitation-recovery-lowers-hips
HesitationAnimTest.tscn|hesitation-active-is-held
HesitationAnimTest.tscn|control-hesitation-startup-is-not-held
SpinAnimTest.tscn|spin-phases
SpinAnimTest.tscn|spin-no-placeholder-leak
SpinAnimTest.tscn|spin-segment-lengths
SpinAnimTest.tscn|spin-edges
SpinAnimTest.tscn|spin-stays-unsuffixed
SpinAnimTest.tscn|spin-startup-differs-from-recovery
SpinAnimTest.tscn|spin-clip-drives-the-rig
SpinAnimTest.tscn|spin-clip-does-not-rotate-root
SpinAnimTest.tscn|spin-shoulder-twist-reverses
SpinAnimTest.tscn|control-spin-startup-twist-does-not-reverse
DriveGatherAnimTest.tscn|drivegather-phases
DriveGatherAnimTest.tscn|drivegather-no-placeholder-leak
DriveGatherAnimTest.tscn|drivegather-segment-lengths
DriveGatherAnimTest.tscn|drivegather-edges
DriveGatherAnimTest.tscn|drivegather-startup-differs-from-recovery
DriveGatherAnimTest.tscn|drivegather-clip-drives-the-rig
DriveGatherAnimTest.tscn|drivegather-active-brings-both-hands-to-ball
DriveGatherAnimTest.tscn|control-drivegather-startup-hands-apart
DriveGatherAnimTest.tscn|drivegather-active-displaces-forward
DriveGatherAnimTest.tscn|control-drivegather-startup-loads-back
DriveGatherAnimTest.tscn|drivegather-recovery-hands-off-to-layup
MovingCrossoverTest.tscn|retains-speed
MovingCrossoverTest.tscn|stationary-forward-exit
MovingCrossoverTest.tscn|hesitation-still-hard-zeroes
MovingCrossoverTest.tscn|remote-pending-stick
BehindTheBackTest.tscn|shielded-sweep
BehindTheBackTest.tscn|narrower-exit-cone
BehindTheBackTest.tscn|dead-dribble-gate
BetweenTheLegsTest.tscn|through-legs-dip
BetweenTheLegsTest.tscn|finesse-selects-betweenthelegs
BetweenTheLegsTest.tscn|dead-dribble-gate
StepBackTest.tscn|step-back-gathers
StepBackTest.tscn|retreat-dribble-no-gather
StepBackTest.tscn|retreat-dribble-dead-dribble-gate
DriveGatherTest.tscn|gather-bleed
DriveGatherTest.tscn|heading-turn
DriveGatherTest.tscn|gather-to-layup-chain
DriveGatherTest.tscn|dead-dribble-gate"""

_BASELINE_MULTIPROCESS_ROWS = (
    ("net-handshake", "tests/integration/run-net-handshake.sh godot", "NetHandshakeTest.tscn", (".godot/harness-logs/net-handshake-*.log",)),
    ("net-state-sync", "tests/integration/run-net-state-sync.sh godot", "NetStateSyncTest.tscn", (".godot/harness-logs/net-state-sync-*.log",)),
    ("net-node-replication", "tests/integration/run-net-node-replication.sh godot", "NetNodeReplicationTest.tscn", (".godot/harness-logs/net-node-replication-*.log",)),
    ("dedicated-roster", "tests/integration/run-dedicated-roster.sh godot", "DedicatedRosterTest.tscn", (".godot/harness-logs/dedicated-roster-*/*", ".godot/harness-coordination/dedicated-roster-*/*")),
    ("dedicated-discovery-join", "tests/integration/run-dedicated-discovery-join.sh godot", "DedicatedDiscoveryJoinTest.tscn", (".godot/harness-logs/dedicated-discovery-join-*/*", ".godot/harness-coordination/dedicated-discovery-join-*/*")),
    ("net-behindtheback-sweep", "tests/integration/run-net-behindtheback-sweep.sh godot", "NetBehindTheBackSweepTest.tscn", (".godot/harness-logs/net-behindtheback-sweep-*.log",)),
    ("net-defensive-telegraph-telegraph", "tests/integration/run-net-defensive-telegraph.sh godot telegraph", "NetDefensiveTelegraphTest.tscn", (".godot/harness-logs/net-defensive-telegraph-telegraph-*.log",)),
    ("net-defensive-telegraph-control", "tests/integration/run-net-defensive-telegraph.sh godot control", "NetDefensiveTelegraphTest.tscn", (".godot/harness-logs/net-defensive-telegraph-control-*.log",)),
    ("net-exitvector-rpc-poisoned", "tests/integration/run-net-exitvector-rpc.sh godot poisoned", "NetExitVectorRpcTest.tscn", (".godot/harness-logs/net-exitvector-rpc-poisoned-*.log",)),
    ("net-exitvector-rpc-steady", "tests/integration/run-net-exitvector-rpc.sh godot steady", "NetExitVectorRpcTest.tscn", (".godot/harness-logs/net-exitvector-rpc-steady-*.log",)),
    ("dedicated-game-journey-healthy", "tests/integration/run-dedicated-game-journey.sh godot healthy", "DedicatedGameJourneyTest.tscn", (".godot/harness-logs/dedicated-game-healthy-*/*", ".godot/dedicated-game-healthy-*/*")),
    ("dedicated-game-journey-discovery-disabled", "tests/integration/run-dedicated-game-journey.sh godot discovery-disabled", "DedicatedGameJourneyTest.tscn", (".godot/harness-logs/dedicated-game-discovery-disabled-*/*", ".godot/dedicated-game-discovery-disabled-*/*")),
    ("dedicated-game-journey-score-rpc-disabled", "tests/integration/run-dedicated-game-journey.sh godot score-rpc-disabled", "DedicatedGameJourneyTest.tscn", (".godot/harness-logs/dedicated-game-score-rpc-disabled-*/*", ".godot/dedicated-game-score-rpc-disabled-*/*")),
)


def _slug(value: str) -> str:
    value = re.sub(r"([a-z0-9])([A-Z])", r"\1-\2", value)
    return re.sub(r"[^a-z0-9]+", "-", value.lower()).strip("-")


_ADDITIONAL_SINGLE_ROWS = ""
_ADDITIONAL_MULTIPROCESS_ROWS: tuple[tuple[str, str, str, tuple[str, ...]], ...] = ()


def _single_cases(rows: str) -> Iterable[HarnessCase]:
    for row in rows.splitlines():
        if not row:
            continue
        scene_name, scenario = row.split("|", 1)
        resource = f"res://tests/integration/{scene_name}"
        scene_id = _slug(scene_name.removesuffix(".tscn"))
        case_id = f"{scene_id}-{scenario}" if scenario else scene_id
        argv = ("godot", "--headless", "--path", ".", resource)
        if scenario:
            argv += ("--", f"--harness-scenario={scenario}")
        tags = ["single", scene_id]
        if "anim-test" in scene_id:
            tags.append("animation")
        yield HarnessCase(
            id=case_id,
            topology="single",
            scenes=(SceneMetadata(resource),),
            argv=argv,
            tags=tuple(tags),
            timeout_seconds=60.0,
            log_policy=LogPolicy("native-log", ".godot/harness-runs/{run_id}/{case_id}.log"),
            artifact_globs=(f".godot/harness-runs/**/{case_id}.log",),
        )


def _multiprocess_cases(rows: Iterable[tuple[str, str, str, tuple[str, ...]]]) -> Iterable[HarnessCase]:
    for case_id, command, scene_name, artifacts in rows:
        tags = ["multiprocess", "network"]
        if case_id.startswith("dedicated-"):
            tags.append("dedicated")
        yield HarnessCase(
            id=case_id,
            topology="multiprocess",
            scenes=(SceneMetadata(f"res://tests/integration/{scene_name}"),),
            argv=tuple(shlex.split(command)),
            tags=tuple(tags),
            timeout_seconds=240.0,
            log_policy=LogPolicy("adapter-owned", ";".join(artifacts)),
            artifact_globs=artifacts,
        )


_CONTROL_COMPONENTS = (
    (
        "held-steal-test-held-vulnerable",
        "held-steal-test-held-immune-outside-window",
        "held-steal-test-pumpfake-now-exposed",
    ),
    (
        "held-steal-test-held-static-vulnerable",
        "held-steal-test-held-static-immune-out-of-reach",
        "held-steal-test-held-static-immune-shielded",
        "held-steal-test-held-static-immune-wrong-side",
    ),
    (
        "transit-steal-test-transit-steal",
        "transit-steal-test-out-of-reach-recovery",
        "transit-steal-test-normal-window-unchanged",
        "transit-steal-test-transit-steal-behind-the-back",
        "transit-steal-test-out-of-reach-recovery-behind-the-back",
        "transit-steal-test-transit-steal-between-the-legs",
        "transit-steal-test-out-of-reach-recovery-between-the-legs",
        "transit-steal-test-transit-steal-spin",
        "transit-steal-test-out-of-reach-recovery-spin",
    ),
    ("steal-turnover-test-success", "steal-turnover-test-whiff"),
    ("steal-facing-mapping-test-face-to-face", "steal-facing-mapping-test-side-by-side"),
    ("contest-scatter-test-contest-active", "contest-scatter-test-no-contest"),
    ("fadeaway-trigger-test-mid-pivot", "fadeaway-trigger-test-squared-up"),
    ("cradle-race-test-out-of-order", "cradle-race-test-in-order", "cradle-race-test-later-legit-drive"),
    ("step-back-cradle-race-test-out-of-order", "step-back-cradle-race-test-in-order", "step-back-cradle-race-test-no-drive-ever"),
    ("layup-test-block-success", "layup-test-block-whiff"),
    ("layup-test-range-gate-inside", "layup-test-range-gate-tolerated", "layup-test-range-gate-rejected"),
    ("euro-step-test-euro-step-beats-committed-defender", "euro-step-test-euro-step-read-still-contests"),
    (
        "dribble-loop-test-dribble-entered",
        "dribble-loop-test-no-ball-locomotion",
        "dribble-loop-test-held-no-dribble",
    ),
    ("jumpshot-anim-test-fadeaway-active", "jumpshot-anim-test-no-fadeaway-when-squared-up"),
    ("step-back-test-step-back-gathers", "step-back-test-retreat-dribble-no-gather"),
    ("move-kind-anim-test-clipped-reaches-permove", "move-kind-anim-test-unclipped-stays-generic"),
    (
        "terminal-no-holder-test-terminal-rebound",
        "terminal-no-holder-test-terminal-oob",
        "terminal-no-holder-test-nonwinning-make",
        "terminal-no-holder-test-live-rebound",
        "terminal-no-holder-test-oob-award",
    ),
    (
        "block-turnover-test-success",
        "block-turnover-test-success-lastactive",
        "block-turnover-test-whiff",
        "block-turnover-test-out-of-range",
        "block-turnover-test-control-make",
        "block-turnover-test-control-make-default-geometry",
    ),
    ("blow-by-window-test-suppressed", "blow-by-window-test-control"),
    ("pivot-anim-test-pivot-enters-exits", "pivot-anim-test-control-locomotion"),
    ("rebound-grab-test-grab-fires", "rebound-grab-test-made-basket-no-grab"),
    ("rebound-grab-test-rebound-clip-contract", "rebound-grab-test-rebound-latch-mutation-control"),
    ("jumpshot-anim-test-jumpshot-airborne-active", "jumpshot-anim-test-control-jumpshot-grounded-startup"),
    ("jumpshot-anim-test-fadeaway-leans-back", "jumpshot-anim-test-control-jumpshot-active-is-upright"),
    ("behind-the-back-anim-test-no-unsuffixed-btb-state", "behind-the-back-anim-test-control-unsuffixed-probe"),
    ("steal-anim-test-no-unsuffixed-steal-state", "steal-anim-test-control-unsuffixed-probe"),
    ("layup-anim-test-layup-airborne-active", "layup-anim-test-control-layup-grounded-startup"),
    ("layup-anim-test-layup-arm-extends-overhead", "layup-anim-test-control-layup-arm-low-startup"),
    ("contest-anim-test-contest-stays-grounded", "contest-anim-test-control-layup-leaves-ground"),
    ("contest-anim-test-contest-arms-rise", "contest-anim-test-control-contest-arms-low-startup"),
    ("jab-step-anim-test-jabstep-torso-pitches-forward-in-active", "jab-step-anim-test-control-jabstep-torso-modest-in-startup"),
    ("retreat-dribble-anim-test-retreatdribble-torso-goes-back-in-active", "retreat-dribble-anim-test-control-retreatdribble-torso-modest-in-startup"),
    ("step-back-anim-test-stepback-active-displaces-back", "step-back-anim-test-control-stepback-startup-displaces-forward"),
    ("block-anim-test-block-airborne-active", "block-anim-test-control-block-grounded-startup"),
    ("in-and-out-anim-test-inandout-offhand-stays-out-in-active", "in-and-out-anim-test-control-inandout-ballhand-does-come-in"),
    ("between-the-legs-anim-test-no-unsuffixed-btl-state", "between-the-legs-anim-test-control-unsuffixed-probe"),
    ("between-the-legs-anim-test-betweenthelegs-startup-differs-from-recovery", "between-the-legs-anim-test-control-identical-clips-read-as-identical"),
    ("hesitation-anim-test-hesitation-active-raises-hips", "hesitation-anim-test-control-hesitation-recovery-lowers-hips"),
    ("hesitation-anim-test-hesitation-active-is-held", "hesitation-anim-test-control-hesitation-startup-is-not-held"),
    ("spin-anim-test-spin-shoulder-twist-reverses", "spin-anim-test-control-spin-startup-twist-does-not-reverse"),
    ("drive-gather-anim-test-drivegather-active-brings-both-hands-to-ball", "drive-gather-anim-test-control-drivegather-startup-hands-apart"),
    ("drive-gather-anim-test-drivegather-active-displaces-forward", "drive-gather-anim-test-control-drivegather-startup-loads-back"),
    ("net-defensive-telegraph-telegraph", "net-defensive-telegraph-control"),
    ("net-exitvector-rpc-poisoned", "net-exitvector-rpc-steady"),
    ("dedicated-game-journey-healthy", "dedicated-game-journey-discovery-disabled", "dedicated-game-journey-score-rpc-disabled"),
)


def _attach_controls(cases: Sequence[HarnessCase]) -> tuple[HarnessCase, ...]:
    controls = {case.id: set(case.paired_control_ids) for case in cases}
    for component in _CONTROL_COMPONENTS:
        for case_id in component:
            controls[case_id].update(other for other in component if other != case_id)
    return tuple(replace(case, paired_control_ids=tuple(sorted(controls[case.id]))) for case in cases)


MIGRATION_BASELINE_CATALOG = tuple((
    *_single_cases(_BASELINE_SINGLE_ROWS),
    *_multiprocess_cases(_BASELINE_MULTIPROCESS_ROWS),
))
CATALOG = _attach_controls(tuple((
    *MIGRATION_BASELINE_CATALOG,
    *_single_cases(_ADDITIONAL_SINGLE_ROWS),
    *_multiprocess_cases(_ADDITIONAL_MULTIPROCESS_ROWS),
)))


class CatalogError(ValueError):
    pass


def validate_catalog(cases: Sequence[HarnessCase], repo_root: Path = ROOT) -> None:
    if not cases:
        raise CatalogError("catalog must not be empty")
    ids: dict[str, str] = {}
    invocations: set[tuple[str, tuple[str, ...]]] = set()
    by_id = {case.id: case for case in cases}
    integration_root = (repo_root / "tests" / "integration").resolve()
    for case in cases:
        folded = case.id.casefold()
        if not _ID_RE.fullmatch(case.id) or folded in ids:
            raise CatalogError(f"invalid or colliding case id: {case.id}")
        ids[folded] = case.id
        if case.topology not in {"single", "multiprocess"}:
            raise CatalogError(f"{case.id}: invalid topology {case.topology}")
        if not case.argv:
            raise CatalogError(f"{case.id}: argv is required")
        if not case.scenes or not math.isfinite(case.timeout_seconds) or case.timeout_seconds <= 0:
            raise CatalogError(f"{case.id}: scenes and positive timeout are required")
        if not case.tags or any(not _ID_RE.fullmatch(tag) for tag in case.tags):
            raise CatalogError(f"{case.id}: invalid tags")
        if not case.log_policy.kind or not case.log_policy.value or not case.artifact_globs:
            raise CatalogError(f"{case.id}: concrete logs and artifacts are required")
        if any(not artifact or Path(artifact).is_absolute() or ".." in Path(artifact).parts for artifact in case.artifact_globs):
            raise CatalogError(f"{case.id}: artifact globs must stay beneath the repository root")
        normalized = (case.topology, tuple(part.casefold() for part in case.argv))
        if normalized in invocations:
            raise CatalogError(f"{case.id}: duplicate normalized invocation")
        invocations.add(normalized)
        for scene in case.scenes:
            scene_path = (repo_root / scene.path).resolve()
            if (
                not scene.resource.startswith("res://tests/integration/")
                or not scene_path.is_relative_to(integration_root)
                or not scene_path.is_file()
            ):
                raise CatalogError(f"{case.id}: missing scene {scene.resource}")
        if case.topology == "multiprocess":
            adapter = case.argv[0]
            adapter_path = (repo_root / adapter).resolve()
            if (
                len(case.argv) < 2
                or case.argv[1] != "godot"
                or not adapter.endswith(".sh")
                or not adapter_path.is_relative_to(integration_root)
                or not adapter_path.is_file()
                or case.log_policy.kind != "adapter-owned"
                or case.log_policy.value != ";".join(case.artifact_globs)
            ):
                raise CatalogError(f"{case.id}: missing adapter {adapter}")
        elif (
            case.argv[:4] != ("godot", "--headless", "--path", ".")
            or len(case.scenes) != 1
            or len(case.argv) not in {5, 7}
            or case.argv[4] != case.scenes[0].resource
            or (len(case.argv) == 7 and (case.argv[5] != "--" or not case.argv[6].startswith("--harness-scenario=")))
            or case.log_policy != LogPolicy("native-log", ".godot/harness-runs/{run_id}/{case_id}.log")
        ):
            raise CatalogError(f"{case.id}: malformed direct Godot metadata")
        for control_id in case.paired_control_ids:
            if control_id == case.id:
                raise CatalogError(f"{case.id}: cannot control itself")
            other = by_id.get(control_id)
            if other is None or case.id not in other.paired_control_ids:
                raise CatalogError(f"{case.id}: asymmetric or missing control {control_id}")


def select_cases(cases: Sequence[HarnessCase], ids: Sequence[str] = (), scenes: Sequence[str] = (), tags: Sequence[str] = ()) -> tuple[HarnessCase, ...]:
    id_set = {value.casefold() for value in ids}
    scene_set = {value.casefold() for value in scenes}
    tag_set = {value.casefold() for value in tags}
    selected = []
    for case in cases:
        scene_aliases = {item.resource.casefold() for item in case.scenes}
        scene_aliases.update(Path(item.path).name.casefold() for item in case.scenes)
        scene_aliases.update(Path(item.path).stem.casefold() for item in case.scenes)
        if id_set and case.id.casefold() not in id_set:
            continue
        if scene_set and not (scene_set & scene_aliases):
            continue
        if tag_set and not (tag_set & {tag.casefold() for tag in case.tags}):
            continue
        selected.append(case)
    if not (ids or scenes or tags):
        return tuple(selected)
    selected_ids = {case.id for case in selected}
    by_id = {case.id: case for case in cases}
    pending = list(selected_ids)
    while pending:
        for control_id in by_id[pending.pop()].paired_control_ids:
            if control_id not in selected_ids:
                selected_ids.add(control_id)
                pending.append(control_id)
    return tuple(case for case in cases if case.id in selected_ids)


def _unknown_selectors(
    cases: Sequence[HarnessCase],
    ids: Sequence[str],
    scenes: Sequence[str],
    tags: Sequence[str],
) -> tuple[tuple[str, str], ...]:
    known_ids = {case.id.casefold() for case in cases}
    known_scenes = {
        alias.casefold()
        for case in cases
        for scene in case.scenes
        for alias in (scene.resource, Path(scene.path).name, Path(scene.path).stem)
    }
    known_tags = {tag.casefold() for case in cases for tag in case.tags}
    return tuple(
        (kind, value)
        for kind, values, known in (
            ("id", ids, known_ids),
            ("scene", scenes, known_scenes),
            ("tag", tags, known_tags),
        )
        for value in values
        if value.casefold() not in known
    )


@dataclass(frozen=True)
class RunResult:
    case_id: str
    status: str
    exit_code: int | None
    log_policy: LogPolicy
    artifacts: tuple[str, ...]


def process_group_options(platform: str = os.name) -> dict[str, object]:
    if platform == "nt":
        return {"creationflags": getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0x00000200)}
    return {"start_new_session": True}


def terminate_process_tree(process: subprocess.Popen[object], platform: str = os.name, grace_seconds: float = 3.0) -> None:
    if process.poll() is not None:
        return
    try:
        if platform == "nt":
            process.send_signal(getattr(signal, "CTRL_BREAK_EVENT", signal.SIGTERM))
        else:
            os.killpg(process.pid, signal.SIGTERM)
        process.wait(timeout=grace_seconds)
        return
    except (OSError, subprocess.TimeoutExpired):
        pass
    if platform == "nt":
        subprocess.run(
            ["taskkill", "/PID", str(process.pid), "/T", "/F"],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            check=False,
        )
    else:
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except OSError:
            process.kill()
    try:
        process.wait(timeout=grace_seconds)
    except (OSError, subprocess.TimeoutExpired):
        process.kill()


def build_command(case: HarnessCase, repo_root: Path, godot: str, bash: str, run_id: str) -> list[str]:
    if case.topology == "single":
        log_path = repo_root / case.log_policy.value.format(run_id=run_id, case_id=case.id)
        return [godot, "--log-file", str(log_path), *case.argv[1:]]
    return [bash, case.argv[0], godot, *case.argv[2:]]


def run_cases(
    cases: Sequence[HarnessCase],
    repo_root: Path,
    godot: str,
    bash: str,
    *,
    popen_factory=subprocess.Popen,
    run_id: str,
) -> tuple[int, tuple[RunResult, ...]]:
    try:
        (repo_root / ".godot" / "harness-runs" / run_id).mkdir(parents=True, exist_ok=True)
    except OSError as error:
        print(f"setup error: {error}", file=sys.stderr)
        return 2, ()
    results: list[RunResult] = []
    overall = 0
    for case in cases:
        print(f"=== RUN  {case.id}")
        try:
            command = build_command(case, repo_root, godot, bash, run_id)
            process = popen_factory(command, cwd=repo_root, **process_group_options())
        except KeyboardInterrupt:
            return 130, tuple(results)
        except Exception as error:
            print(f"=== ERROR {case.id}: {error}", file=sys.stderr)
            results.append(RunResult(case.id, "ERROR", None, case.log_policy, case.artifact_globs))
            overall = 2
            continue
        try:
            exit_code = process.wait(timeout=case.timeout_seconds)
        except subprocess.TimeoutExpired:
            terminate_process_tree(process)
            exit_code = 124
            status = "TIMEOUT"
            if overall != 2:
                overall = 1
        except KeyboardInterrupt:
            terminate_process_tree(process)
            results.append(RunResult(case.id, "CANCELLED", 130, case.log_policy, case.artifact_globs))
            return 130, tuple(results)
        except Exception as error:
            terminate_process_tree(process)
            print(f"=== ERROR {case.id}: {error}", file=sys.stderr)
            results.append(RunResult(case.id, "ERROR", None, case.log_policy, case.artifact_globs))
            overall = 2
            continue
        else:
            status = "PASS" if exit_code == 0 else "FAIL"
            if exit_code != 0 and overall != 2:
                overall = 1
        print(f"=== {status} {case.id} (exit {exit_code})")
        results.append(RunResult(case.id, status, exit_code, case.log_policy, case.artifact_globs))
    return overall, tuple(results)


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)
    list_parser = subparsers.add_parser("list")
    for flag in ("id", "scene", "tag"):
        list_parser.add_argument(f"--{flag}", action="append", default=[])
    run_parser = subparsers.add_parser("run")
    run_parser.add_argument("--all", action="store_true")
    run_parser.add_argument("--godot", default=os.environ.get("GODOT", "godot"))
    run_parser.add_argument("--bash", default=os.environ.get("BASH", "bash"))
    for flag in ("id", "scene", "tag"):
        run_parser.add_argument(f"--{flag}", action="append", default=[])
    return parser


def _default_run_id() -> str:
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
    return f"{stamp}-{os.getpid()}-{secrets.token_hex(4)}"


def _resolve_executable(value: str, repo_root: Path, which) -> str | None:
    candidate = Path(value)
    if candidate.is_absolute() or any(sep in value for sep in ("/", "\\")):
        resolved = candidate if candidate.is_absolute() else repo_root / candidate
        return str(resolved) if resolved.is_file() else None
    located = which(value)
    return str(located) if located is not None else None


def main(
    argv: Sequence[str] | None = None,
    *,
    repo_root: Path = ROOT,
    cases: Sequence[HarnessCase] = CATALOG,
    popen_factory=subprocess.Popen,
    which=shutil.which,
    run_id_factory=_default_run_id,
) -> int:
    try:
        try:
            args = _parser().parse_args(argv)
        except SystemExit as error:
            return int(error.code)
        validate_catalog(cases, repo_root)
        unknown = _unknown_selectors(cases, args.id, args.scene, args.tag)
        if unknown:
            formatted = ", ".join(f"--{kind}={value}" for kind, value in unknown)
            print(f"unrecognized catalog selector(s): {formatted}", file=sys.stderr)
            return 2
        selected = select_cases(cases, args.id, args.scene, args.tag)
        if (args.id or args.scene or args.tag) and not selected:
            print("catalog selection is empty", file=sys.stderr)
            return 2
        if args.command == "list":
            for case in selected:
                scenes = ",".join(scene.resource for scene in case.scenes)
                print(f"{case.id}\t{case.topology}\t{scenes}\t{','.join(case.tags)}")
            return 0
        filters_present = bool(args.id or args.scene or args.tag)
        if args.all == filters_present:
            print("run requires either --all or one-or-more filters (never both)", file=sys.stderr)
            return 2
        godot = _resolve_executable(args.godot, repo_root, which)
        if godot is None:
            print(f"missing Godot executable: {args.godot}", file=sys.stderr)
            return 2
        bash = _resolve_executable(args.bash, repo_root, which)
        if any(case.topology == "multiprocess" for case in selected) and bash is None:
            print(f"missing bash executable: {args.bash}", file=sys.stderr)
            return 2
        code, results = run_cases(selected, repo_root, godot, bash or args.bash, popen_factory=popen_factory, run_id=run_id_factory())
        print("==================== SUMMARY ====================")
        for result in results:
            print(f"{result.status:9} {result.case_id} log={result.log_policy.kind}:{result.log_policy.value} artifacts={','.join(result.artifacts)}")
        print(f"RESULT: {sum(result.status == 'PASS' for result in results)} passed / {len(results)} run")
        return code
    except CatalogError as error:
        print(f"catalog error: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
