"""Guard the instructions that authorize an autopilot milestone activation."""

import re
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
ORCHESTRATORS = (
    ".claude/agents/orchestrator.md",
    ".codex/agents/orchestrator.toml",
)
CHANGE_CONTROL = (
    ".agents/skills/hooper-change-control/SKILL.md",
    ".claude/skills/hooper-change-control/SKILL.md",
)
DOCS_AND_WRITING = (
    ".agents/skills/hooper-docs-and-writing/SKILL.md",
    ".claude/skills/hooper-docs-and-writing/SKILL.md",
)


def between(source: str, start: str, end: str) -> str:
    """Return one operational section, excluding unrelated history."""
    return source.split(start, 1)[1].split(end, 1)[0]


def activation_guidance(path: str, source: str) -> str:
    if path in ORCHESTRATORS:
        return "\n".join((
            between(source, "- **ADR-0017**", "- **ADR-0014**"),
            between(source, "### 5. Milestone closure and activation", "### 6."),
        ))
    if path in CHANGE_CONTROL:
        return "\n".join((
            between(source, "**Feel is never auto-accepted as feel.**", "### The Stop-hook"),
            between(source, "## 8. Milestone activation", "## 9."),
        ))
    return between(source, "### AGENTS.md §2 — the milestone table", "### CONTEXT.md")


class MilestoneActivationContractTests(unittest.TestCase):
    def test_operational_guidance_uses_harness_closure_and_deferred_feel(self):
        for path in ORCHESTRATORS + CHANGE_CONTROL + DOCS_AND_WRITING:
            with self.subTest(path=path):
                source = (ROOT / path).read_text(encoding="utf-8")
                guidance = re.sub(r"\s+", " ", activation_guidance(path, source)).lower()
                self.assertNotRegex(
                    guidance,
                    r"(?:one per.milestone human feel pass|"
                    r"human feel.acceptance pass per milestone|"
                    r"human feel pass.{0,35}(?:completed|confirmed|required)|"
                    r"(?:incl\.?|after|only once).{0,80}human feel pass)",
                    f"{path}: a human feel pass must not gate epic closure or successor activation",
                )
                for label, pattern in (
                    ("green CI", r"\bci\b"),
                    ("applicable headless harness", r"\bharness\b"),
                    ("independent code review", r"(?:/code-review|code.review)"),
                    ("predecessor epic issue closed", r"(?:\bepic issue\b.{0,100}\bclos(?:ed|ure)\b|\bclos(?:ed|ure)\b.{0,100}\bepic issue\b)"),
                    ("consolidated human-scheduled feel issue", r"#173"),
                ):
                    self.assertRegex(guidance, pattern, f"{path}: activation guidance must name {label}")
                self.assertRegex(
                    source.lower(), r"feel is never auto.accepted",
                    f"{path}: feel must remain a human judgment",
                )

    def test_orchestrator_stop_conditions_preserve_holds_and_feel_ownership(self):
        for path in ORCHESTRATORS:
            with self.subTest(path=path):
                source = (ROOT / path).read_text(encoding="utf-8")
                normalized = re.sub(r"\s+", " ", source).lower()
                stop_conditions = re.sub(
                    r"\s+", " ", between(source, "### 6.", "## Guardrails")
                ).lower()
                running_state = re.sub(
                    r"\s+", " ", source.split("## Running state", 1)[1]
                ).lower()

                self.assertNotIn("a milestone's feel pass coming due", normalized)
                self.assertRegex(stop_conditions, r"explicit human hold")
                self.assertRegex(stop_conditions, r"genuine design call")
                self.assertRegex(normalized, r"current m11 hold.{0,160}adr #105")
                self.assertRegex(running_state, r"#173")
                self.assertRegex(normalized, r"feel is never auto.accepted")

    def test_ci_runs_the_milestone_activation_contract(self):
        workflow = (ROOT / ".github/workflows/ci.yml").read_text(encoding="utf-8")
        self.assertIn(
            "python3 -m unittest discover -s tests/repository "
            "-p test_milestone_activation_contract.py -v",
            re.sub(r"\s+", " ", workflow),
        )


if __name__ == "__main__":
    unittest.main()
