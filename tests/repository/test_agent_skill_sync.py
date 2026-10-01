"""Behavioral contract for canonical agent-skill generation and drift checks."""

import contextlib
import importlib.util
import io
import json
import shutil
import sys
import unittest
import uuid
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
MODULE_PATH = ROOT / "tools" / "sync_agent_skills.py"


def load_sync_module():
    spec = importlib.util.spec_from_file_location("sync_agent_skills", MODULE_PATH)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"cannot import {MODULE_PATH}")
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


class AgentSkillSyncCliTests(unittest.TestCase):
    def test_generate_is_deterministic_and_check_reports_drift_without_writing(self):
        sync = load_sync_module()
        root = ROOT / ".godot" / f"agent-skill-sync-{uuid.uuid4().hex}"
        root.mkdir(parents=True)
        self.addCleanup(shutil.rmtree, root, True)
        try:
            source = root / ".agent-skills" / "canonical" / "sample" / "SKILL.md"
            source.parent.mkdir(parents=True)
            source.write_text("shared guidance\n", encoding="utf-8")
            adapters = root / ".agent-skills" / "adapters"
            adapters.mkdir()
            for platform in ("codex", "claude"):
                (adapters / f"{platform}.json").write_text("{}\n", encoding="utf-8")
            manifest = {
                "schema_version": 1,
                "platforms": {
                    "codex": {
                        "output_root": ".agents/skills",
                        "adapter": ".agent-skills/adapters/codex.json",
                    },
                    "claude": {
                        "output_root": ".claude/skills",
                        "adapter": ".agent-skills/adapters/claude.json",
                    },
                },
                "files": [
                    {
                        "path": "sample/SKILL.md",
                        "classification": "shared",
                        "source": ".agent-skills/canonical/sample/SKILL.md",
                    }
                ],
            }
            manifest_path = root / ".agent-skills" / "manifest.json"
            manifest_path.write_text(json.dumps(manifest), encoding="utf-8")

            self.assertEqual(0, sync.main(["generate"], repo_root=root))
            codex_output = root / ".agents" / "skills" / "sample" / "SKILL.md"
            claude_output = root / ".claude" / "skills" / "sample" / "SKILL.md"
            self.assertEqual(b"shared guidance\n", codex_output.read_bytes())
            self.assertEqual(codex_output.read_bytes(), claude_output.read_bytes())

            claude_output.write_text("drifted\n", encoding="utf-8")
            before = claude_output.read_bytes()
            stderr = io.StringIO()
            with contextlib.redirect_stderr(stderr):
                result = sync.main(["check"], repo_root=root)

            self.assertEqual(1, result)
            self.assertEqual(before, claude_output.read_bytes())
            self.assertIn(".claude/skills/sample/SKILL.md", stderr.getvalue())
        finally:
            shutil.rmtree(root, ignore_errors=True)


class AgentSkillRepositoryContractTests(unittest.TestCase):
    def test_tracked_outputs_match_canonical_sources_and_staleness_policy(self):
        sync = load_sync_module()
        stderr = io.StringIO()
        with contextlib.redirect_stderr(stderr):
            result = sync.main(["check"], repo_root=ROOT)
        self.assertEqual(0, result, stderr.getvalue())

    def test_parity_report_explains_every_generated_difference(self):
        sync = load_sync_module()
        stdout = io.StringIO()
        with contextlib.redirect_stdout(stdout):
            self.assertEqual(0, sync.main(["report"], repo_root=ROOT))
        report = stdout.getvalue()
        self.assertNotIn("UNEXPLAINED", report)
        manifest = sync.load_manifest(ROOT)
        self.assertEqual(len(manifest["files"]), len(report.splitlines()))

    def test_generated_source_uses_adapter_values_and_explicit_platform_sections(self):
        sync = load_sync_module()
        root = ROOT / ".godot" / f"agent-skill-sync-{uuid.uuid4().hex}"
        root.mkdir(parents=True)
        try:
            source = root / ".agent-skills" / "canonical" / "sample" / "SKILL.md"
            source.parent.mkdir(parents=True)
            source.write_text(
                "Run `{{mcp_list_command}}`.\n"
                "<!-- agent-skill:if codex -->Codex-only guidance.\n"
                "<!-- agent-skill:endif -->"
                "<!-- agent-skill:if claude -->Claude-only guidance.\n"
                "<!-- agent-skill:endif -->",
                encoding="utf-8",
            )
            adapters = root / ".agent-skills" / "adapters"
            adapters.mkdir()
            (adapters / "codex.json").write_text(
                json.dumps({"mcp_list_command": "codex mcp list"}), encoding="utf-8"
            )
            (adapters / "claude.json").write_text(
                json.dumps({"mcp_list_command": "claude mcp list"}), encoding="utf-8"
            )
            manifest = {
                "schema_version": 1,
                "platforms": {
                    "codex": {
                        "output_root": ".agents/skills",
                        "adapter": ".agent-skills/adapters/codex.json",
                    },
                    "claude": {
                        "output_root": ".claude/skills",
                        "adapter": ".agent-skills/adapters/claude.json",
                    },
                },
                "files": [
                    {
                        "path": "sample/SKILL.md",
                        "classification": "generated",
                        "source": ".agent-skills/canonical/sample/SKILL.md",
                    }
                ],
            }
            (root / ".agent-skills" / "manifest.json").write_text(
                json.dumps(manifest), encoding="utf-8"
            )

            self.assertEqual(0, sync.main(["generate"], repo_root=root))
            codex = (root / ".agents/skills/sample/SKILL.md").read_text(encoding="utf-8")
            claude = (root / ".claude/skills/sample/SKILL.md").read_text(encoding="utf-8")
            self.assertEqual("Run `codex mcp list`.\nCodex-only guidance.\n", codex)
            self.assertEqual("Run `claude mcp list`.\nClaude-only guidance.\n", claude)
        finally:
            shutil.rmtree(root, ignore_errors=True)

    def test_platform_specific_sources_are_explicitly_classified(self):
        sync = load_sync_module()
        root = ROOT / ".godot" / f"agent-skill-sync-{uuid.uuid4().hex}"
        root.mkdir(parents=True)
        try:
            canonical = root / ".agent-skills" / "canonical" / "sample"
            canonical.mkdir(parents=True)
            (canonical / "codex.md").write_text("Codex only\n", encoding="utf-8")
            (canonical / "claude.md").write_text("Claude only\n", encoding="utf-8")
            adapters = root / ".agent-skills" / "adapters"
            adapters.mkdir()
            for platform in ("codex", "claude"):
                (adapters / f"{platform}.json").write_text("{}\n", encoding="utf-8")
            manifest = {
                "schema_version": 1,
                "platforms": {
                    "codex": {
                        "output_root": ".agents/skills",
                        "adapter": ".agent-skills/adapters/codex.json",
                    },
                    "claude": {
                        "output_root": ".claude/skills",
                        "adapter": ".agent-skills/adapters/claude.json",
                    },
                },
                "files": [
                    {
                        "path": "sample/SKILL.md",
                        "classification": "platform-specific",
                        "sources": {
                            "codex": ".agent-skills/canonical/sample/codex.md",
                            "claude": ".agent-skills/canonical/sample/claude.md",
                        },
                        "difference_reason": "the runtimes require unrelated instructions",
                    }
                ],
            }
            (root / ".agent-skills" / "manifest.json").write_text(
                json.dumps(manifest), encoding="utf-8"
            )

            self.assertEqual(0, sync.main(["generate"], repo_root=root))
            self.assertEqual(
                "Codex only\n",
                (root / ".agents/skills/sample/SKILL.md").read_text(encoding="utf-8"),
            )
            self.assertEqual(
                "Claude only\n",
                (root / ".claude/skills/sample/SKILL.md").read_text(encoding="utf-8"),
            )
        finally:
            shutil.rmtree(root, ignore_errors=True)

    def test_check_rejects_files_not_classified_by_the_manifest(self):
        sync = load_sync_module()
        root = ROOT / ".godot" / f"agent-skill-sync-{uuid.uuid4().hex}"
        root.mkdir(parents=True)
        try:
            source = root / ".agent-skills/canonical/sample/SKILL.md"
            source.parent.mkdir(parents=True)
            source.write_text("shared\n", encoding="utf-8")
            adapters = root / ".agent-skills/adapters"
            adapters.mkdir()
            platforms = {}
            for platform, output_root in (
                ("codex", ".agents/skills"),
                ("claude", ".claude/skills"),
            ):
                adapter = adapters / f"{platform}.json"
                adapter.write_text("{}\n", encoding="utf-8")
                platforms[platform] = {
                    "output_root": output_root,
                    "adapter": adapter.relative_to(root).as_posix(),
                }
            manifest = {
                "schema_version": 1,
                "platforms": platforms,
                "files": [
                    {
                        "path": "sample/SKILL.md",
                        "classification": "shared",
                        "source": source.relative_to(root).as_posix(),
                    }
                ],
            }
            (root / ".agent-skills/manifest.json").write_text(
                json.dumps(manifest), encoding="utf-8"
            )
            self.assertEqual(0, sync.main(["generate"], repo_root=root))
            unclassified = root / ".claude/skills/unclassified/SKILL.md"
            unclassified.parent.mkdir(parents=True)
            unclassified.write_text("unexplained\n", encoding="utf-8")

            stderr = io.StringIO()
            with contextlib.redirect_stderr(stderr):
                result = sync.main(["check"], repo_root=root)

            self.assertEqual(1, result)
            self.assertIn("unclassified agent skill: .claude/skills/unclassified/SKILL.md", stderr.getvalue())
        finally:
            shutil.rmtree(root, ignore_errors=True)

    def test_check_rejects_manifest_defined_stale_claims(self):
        sync = load_sync_module()
        root = ROOT / ".godot" / f"agent-skill-sync-{uuid.uuid4().hex}"
        root.mkdir(parents=True)
        try:
            source = root / ".agent-skills/canonical/sample/SKILL.md"
            source.parent.mkdir(parents=True)
            source.write_text(
                "Bad: `.Codex/hooks/verify-green.sh`.\n"
                "Good: `.codex/hooks/verify-green.sh`.\n",
                encoding="utf-8",
            )
            adapters = root / ".agent-skills/adapters"
            adapters.mkdir()
            platforms = {}
            for platform, output_root in (
                ("codex", ".agents/skills"),
                ("claude", ".claude/skills"),
            ):
                adapter = adapters / f"{platform}.json"
                adapter.write_text("{}\n", encoding="utf-8")
                platforms[platform] = {
                    "output_root": output_root,
                    "adapter": adapter.relative_to(root).as_posix(),
                }
            manifest = {
                "schema_version": 1,
                "platforms": platforms,
                "files": [
                    {
                        "path": "sample/SKILL.md",
                        "classification": "shared",
                        "source": source.relative_to(root).as_posix(),
                    }
                ],
                "staleness_checks": [
                    {
                        "pattern": r"\.Codex/",
                        "message": "uppercase Codex paths do not exist",
                    }
                ],
            }
            (root / ".agent-skills/manifest.json").write_text(
                json.dumps(manifest), encoding="utf-8"
            )
            self.assertEqual(0, sync.main(["generate"], repo_root=root))

            stderr = io.StringIO()
            with contextlib.redirect_stderr(stderr):
                result = sync.main(["check"], repo_root=root)

            self.assertEqual(1, result)
            self.assertIn("uppercase Codex paths do not exist", stderr.getvalue())
            self.assertIn(".agents/skills/sample/SKILL.md:1", stderr.getvalue())
            self.assertNotIn(".agents/skills/sample/SKILL.md:2", stderr.getvalue())
        finally:
            shutil.rmtree(root, ignore_errors=True)

    def test_report_explains_each_files_classification_and_intentional_difference(self):
        sync = load_sync_module()
        root = ROOT / ".godot" / f"agent-skill-sync-{uuid.uuid4().hex}"
        root.mkdir(parents=True)
        try:
            canonical = root / ".agent-skills/canonical"
            (canonical / "shared").mkdir(parents=True)
            (canonical / "generated").mkdir(parents=True)
            (canonical / "shared/SKILL.md").write_text("same\n", encoding="utf-8")
            (canonical / "generated/SKILL.md").write_text("{{platform_name}}\n", encoding="utf-8")
            adapters = root / ".agent-skills/adapters"
            adapters.mkdir()
            platforms = {}
            for platform, output_root, display_name in (
                ("codex", ".agents/skills", "Codex"),
                ("claude", ".claude/skills", "Claude Code"),
            ):
                adapter = adapters / f"{platform}.json"
                adapter.write_text(json.dumps({"platform_name": display_name}), encoding="utf-8")
                platforms[platform] = {
                    "output_root": output_root,
                    "adapter": adapter.relative_to(root).as_posix(),
                }
            manifest = {
                "schema_version": 1,
                "platforms": platforms,
                "files": [
                    {
                        "path": "shared/SKILL.md",
                        "classification": "shared",
                        "source": ".agent-skills/canonical/shared/SKILL.md",
                    },
                    {
                        "path": "generated/SKILL.md",
                        "classification": "generated",
                        "source": ".agent-skills/canonical/generated/SKILL.md",
                        "difference_reason": "platform display name",
                    },
                ],
            }
            (root / ".agent-skills/manifest.json").write_text(
                json.dumps(manifest), encoding="utf-8"
            )

            stdout = io.StringIO()
            with contextlib.redirect_stdout(stdout):
                result = sync.main(["report"], repo_root=root)

            self.assertEqual(0, result)
            self.assertIn("shared\tshared/SKILL.md\tbyte-identical", stdout.getvalue())
            self.assertIn(
                "generated\tgenerated/SKILL.md\tplatform display name", stdout.getvalue()
            )
        finally:
            shutil.rmtree(root, ignore_errors=True)


if __name__ == "__main__":
    unittest.main()
