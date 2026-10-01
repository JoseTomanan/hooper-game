#!/usr/bin/env python3
"""Generate mirrored agent skills from their canonical repository sources."""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path
from typing import Sequence


MANIFEST_PATH = Path(".agent-skills/manifest.json")
CONDITIONAL_PATTERN = re.compile(
    r"<!-- agent-skill:if ([a-z0-9_-]+) -->(.*?)<!-- agent-skill:endif -->",
    re.DOTALL,
)
VALUE_PATTERN = re.compile(r"\{\{([a-z0-9_]+)\}\}")


def load_manifest(repo_root: Path) -> dict:
    return json.loads((repo_root / MANIFEST_PATH).read_text(encoding="utf-8"))


def render_generated(source: str, platform_name: str, adapter: dict[str, str]) -> bytes:
    def select_platform(match: re.Match[str]) -> str:
        return match.group(2) if match.group(1) == platform_name else ""

    rendered = CONDITIONAL_PATTERN.sub(select_platform, source)

    def substitute_value(match: re.Match[str]) -> str:
        key = match.group(1)
        if key not in adapter:
            raise ValueError(f"adapter {platform_name} is missing value: {key}")
        return adapter[key]

    rendered = VALUE_PATTERN.sub(substitute_value, rendered)
    if "<!-- agent-skill:" in rendered or "{{" in rendered:
        raise ValueError(f"unresolved template directive for platform: {platform_name}")
    return rendered.encode("utf-8")


def expected_outputs(repo_root: Path, manifest: dict) -> dict[Path, bytes]:
    outputs: dict[Path, bytes] = {}
    adapters = {
        name: json.loads((repo_root / platform["adapter"]).read_text(encoding="utf-8"))
        for name, platform in manifest["platforms"].items()
    }
    for entry in manifest["files"]:
        classification = entry["classification"]
        for platform_name, platform in manifest["platforms"].items():
            output = repo_root / platform["output_root"] / entry["path"]
            if classification == "shared":
                source = (repo_root / entry["source"]).read_text(encoding="utf-8")
                outputs[output] = source.encode("utf-8")
            elif classification == "generated":
                source = (repo_root / entry["source"]).read_text(encoding="utf-8")
                outputs[output] = render_generated(source, platform_name, adapters[platform_name])
            elif classification == "platform-specific":
                source = (repo_root / entry["sources"][platform_name]).read_text(encoding="utf-8")
                outputs[output] = source.encode("utf-8")
            else:
                raise ValueError(f"unsupported classification: {classification}")
    return outputs


def relative_display(path: Path, repo_root: Path) -> str:
    return path.relative_to(repo_root).as_posix()


def generate(repo_root: Path, outputs: dict[Path, bytes]) -> None:
    for path, content in outputs.items():
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(content)


def find_drift(outputs: dict[Path, bytes]) -> list[Path]:
    return [
        path
        for path, expected in outputs.items()
        if not path.exists() or path.read_bytes() != expected
    ]


def find_unclassified(repo_root: Path, manifest: dict, outputs: dict[Path, bytes]) -> list[Path]:
    expected = set(outputs)
    found: list[Path] = []
    for platform in manifest["platforms"].values():
        output_root = repo_root / platform["output_root"]
        if not output_root.exists():
            continue
        found.extend(path for path in output_root.rglob("*") if path.is_file() and path not in expected)
    return sorted(found)


def find_stale_claims(
    manifest: dict, outputs: dict[Path, bytes]
) -> list[tuple[Path, int, str]]:
    findings: list[tuple[Path, int, str]] = []
    rules = [
        (
            re.compile(rule["pattern"], re.IGNORECASE if rule.get("ignore_case") else 0),
            rule["message"],
        )
        for rule in manifest.get("staleness_checks", [])
    ]
    for path, content in outputs.items():
        for line_number, line in enumerate(content.decode("utf-8").splitlines(), start=1):
            for pattern, message in rules:
                if pattern.search(line):
                    findings.append((path, line_number, message))
    return findings


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("generate", "check", "report"))
    return parser


def main(argv: Sequence[str] | None = None, *, repo_root: Path | None = None) -> int:
    options = build_parser().parse_args(argv)
    root = Path.cwd() if repo_root is None else Path(repo_root)
    manifest = load_manifest(root)
    outputs = expected_outputs(root, manifest)

    if options.command == "generate":
        generate(root, outputs)
        return 0

    if options.command == "report":
        for entry in manifest["files"]:
            classification = entry["classification"]
            explanation = (
                "byte-identical"
                if classification == "shared"
                else entry.get("difference_reason", "UNEXPLAINED")
            )
            print(f"{classification}\t{entry['path']}\t{explanation}")
        return 0

    drift = find_drift(outputs)
    unclassified = find_unclassified(root, manifest, outputs)
    stale_claims = find_stale_claims(manifest, outputs)
    if not drift and not unclassified and not stale_claims:
        return 0
    for path in drift:
        print(f"stale generated agent skill: {relative_display(path, root)}", file=sys.stderr)
    for path in unclassified:
        print(f"unclassified agent skill: {relative_display(path, root)}", file=sys.stderr)
    for path, line_number, message in stale_claims:
        print(
            f"stale agent guidance: {relative_display(path, root)}:{line_number}: {message}",
            file=sys.stderr,
        )
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
