"""Guard the metadata-first issue-discovery contract used by AFK orchestration."""

import importlib.util
import sys
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
ORCHESTRATORS = (
    ".claude/agents/orchestrator.md",
    ".codex/agents/orchestrator.toml",
)
TRACKER_GUIDANCE = "docs/agents/issue-tracker.md"
MEASUREMENT_TOOL = ROOT / "tools" / "measure_issue_discovery_payload.py"


def between(source: str, start: str, end: str) -> str:
    """Return one operational section, excluding unrelated prose."""
    return source.split(start, 1)[1].split(end, 1)[0]


def discovery_stages(source: str, end: str) -> tuple[str, str]:
    stage_one = between(source, "#### Stage 1 — lightweight discovery", "#### Stage 2")
    stage_two = between(source, "#### Stage 2 — full candidate hydration", end)
    return stage_one, stage_two


class IssueDiscoveryContractTests(unittest.TestCase):
    def assert_two_stage_contract(self, path: str, source: str, end: str) -> None:
        stage_one, stage_two = discovery_stages(source, end)

        for field in (
            "number",
            "title",
            "state",
            "labels",
            "milestone",
            "updatedAt",
            "parent",
            "subIssues",
            "blockedBy",
        ):
            self.assertIn(field, stage_one, f"{path}: Stage 1 must fetch {field}")

        self.assertNotIn("body", stage_one.lower(), f"{path}: Stage 1 must not fetch bodies")
        self.assertNotIn(
            "comments", stage_one.lower(), f"{path}: Stage 1 must not fetch comments"
        )

        for field in (
            "body",
            "comments",
            "labels",
            "parent",
            "subIssues",
            "blockedBy",
            "closedByPullRequestsReferences",
        ):
            self.assertIn(field, stage_two, f"{path}: Stage 2 must hydrate {field}")

        normalized = " ".join(stage_two.split()).lower()
        self.assertRegex(
            normalized,
            r"immediately before.{0,120}final (?:selection|readiness)",
            f"{path}: shortlisted candidates must be fresh at final judgment",
        )
        self.assertRegex(
            normalized,
            r"(?:missing|ambiguous|truncated).{0,180}(?:full fetch|hydrate|stop)",
            f"{path}: incomplete metadata must fail open to hydration or a safe stop",
        )
        self.assertIn(
            "exhaust every stage 2 connection",
            normalized,
            f"{path}: full hydration must consume every candidate connection page",
        )
        self.assertRegex(
            normalized,
            r"afk.{0,120}hitl|hitl.{0,120}afk",
            f"{path}: final readiness must re-check AFK/HITL separation",
        )

    def test_tracker_guidance_defines_the_two_stage_contract(self):
        source = (ROOT / TRACKER_GUIDANCE).read_text(encoding="utf-8")
        self.assert_two_stage_contract(TRACKER_GUIDANCE, source, "## Repo-specific rules")

    def test_both_orchestrators_follow_the_same_two_stage_contract(self):
        for path in ORCHESTRATORS:
            with self.subTest(path=path):
                source = (ROOT / path).read_text(encoding="utf-8")
                step_one = between(
                    source,
                    "### 1. Refresh state and pick the next ready issue",
                    "### 2. Decompose",
                )
                self.assert_two_stage_contract(path, step_one, "#### Final selection")
                final_selection = step_one.split("#### Final selection", 1)[1]
                self.assertRegex(
                    " ".join(final_selection.split()).lower(),
                    r"dispatch.{0,160}(?:stage 2|full candidate|hydrated)",
                    f"{path}: dispatch must consume a hydrated candidate",
                )

    def test_payload_measurement_is_repeatable_and_compares_equal_issue_sets(self):
        spec = importlib.util.spec_from_file_location(
            "measure_issue_discovery_payload", MEASUREMENT_TOOL
        )
        self.assertIsNotNone(spec, "measurement tool must be importable")
        self.assertIsNotNone(spec.loader, "measurement tool must have a loader")
        module = importlib.util.module_from_spec(spec)
        sys.modules[spec.name] = module
        spec.loader.exec_module(module)

        report = module.compare_payloads(
            lightweight=b'[ {"number": 1}, {"number": 2} ]',
            legacy=b'[ {"number": 1, "body": "large"}, {"number": 2, "body": "large"} ]',
        )

        self.assertEqual(2, report.issue_count)
        self.assertEqual({1, 2}, report.issue_numbers)
        self.assertGreater(report.legacy_bytes, report.lightweight_bytes)
        self.assertGreater(report.reduction_percent, 0)


if __name__ == "__main__":
    unittest.main()
