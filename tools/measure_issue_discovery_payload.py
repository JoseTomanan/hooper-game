#!/usr/bin/env python3
"""Measure metadata-first issue discovery against the former full payload.

The two queries deliberately walk the same live set of open issues.  The
comparison refuses to report a reduction when the issue-number sets differ,
because repository state may change between the two requests.
"""

from __future__ import annotations

import argparse
from dataclasses import dataclass
import json
import os
import subprocess
import sys
from typing import Any


_ISSUE_METADATA = """
number
title
state
labels(first: 100) {
  nodes { name }
  pageInfo { hasNextPage endCursor }
}
milestone { number title state }
updatedAt
parent { number title state }
subIssues(first: 100) {
  nodes { number title state }
  pageInfo { hasNextPage endCursor }
}
blockedBy(first: 100) {
  nodes { number title state }
  pageInfo { hasNextPage endCursor }
}
"""


def _query(extra_fields: str = "") -> str:
    return f"""
query($owner: String!, $name: String!, $endCursor: String) {{
  repository(owner: $owner, name: $name) {{
    issues(
      first: 100
      after: $endCursor
      states: OPEN
      orderBy: {{field: UPDATED_AT, direction: DESC}}
    ) {{
      nodes {{
        {_ISSUE_METADATA}
        {extra_fields}
      }}
      pageInfo {{ hasNextPage endCursor }}
    }}
  }}
}}
""".strip()


LIGHTWEIGHT_QUERY = _query()
LEGACY_QUERY = _query(
    """
body
comments(first: 100) {
  nodes { body }
  pageInfo { hasNextPage endCursor }
}
"""
)


@dataclass
class PayloadComparison:
    issue_count: int
    issue_numbers: set[int]
    lightweight_bytes: int
    legacy_bytes: int
    reduction_percent: float


def _parse_json(payload: bytes, label: str) -> Any:
    try:
        text = payload.decode("utf-8")
    except UnicodeDecodeError as error:
        raise ValueError(f"{label} payload is not valid UTF-8") from error

    try:
        return json.loads(text)
    except json.JSONDecodeError as error:
        raise ValueError(f"{label} payload is not valid JSON: {error}") from error


def _page_issue_nodes(parsed: Any, label: str) -> list[dict[str, Any]]:
    """Return only outer repository issues, never related-issue nodes."""
    if isinstance(parsed, list) and (
        not parsed or all(isinstance(item, dict) and "number" in item for item in parsed)
    ):
        return parsed

    pages = parsed if isinstance(parsed, list) else [parsed]
    nodes: list[dict[str, Any]] = []
    for page_index, page in enumerate(pages, start=1):
        if not isinstance(page, dict):
            raise ValueError(f"{label} page {page_index} is not a JSON object")
        if page.get("errors"):
            raise ValueError(f"{label} GraphQL response contains errors: {page['errors']}")
        try:
            page_nodes = page["data"]["repository"]["issues"]["nodes"]
        except (KeyError, TypeError) as error:
            raise ValueError(
                f"{label} page {page_index} has no repository issue connection"
            ) from error
        if not isinstance(page_nodes, list) or not all(
            isinstance(node, dict) for node in page_nodes
        ):
            raise ValueError(f"{label} page {page_index} has invalid issue nodes")
        nodes.extend(page_nodes)
    return nodes


def _issue_numbers(payload: bytes, label: str) -> set[int]:
    nodes = _page_issue_nodes(_parse_json(payload, label), label)
    numbers: list[int] = []
    for index, node in enumerate(nodes, start=1):
        number = node.get("number")
        if isinstance(number, bool) or not isinstance(number, int):
            raise ValueError(f"{label} issue {index} has no integer number")
        numbers.append(number)
    if len(numbers) != len(set(numbers)):
        raise ValueError(f"{label} payload contains duplicate outer issue numbers")
    return set(numbers)


def compare_payloads(lightweight: bytes, legacy: bytes) -> PayloadComparison:
    """Compare UTF-8 JSON payload sizes after proving their issue sets match."""
    lightweight_numbers = _issue_numbers(lightweight, "lightweight")
    legacy_numbers = _issue_numbers(legacy, "legacy")
    if lightweight_numbers != legacy_numbers:
        missing_from_lightweight = sorted(legacy_numbers - lightweight_numbers)
        missing_from_legacy = sorted(lightweight_numbers - legacy_numbers)
        raise ValueError(
            "payload issue-number sets differ; repository state may have changed "
            f"(missing from lightweight={missing_from_lightweight}, "
            f"missing from legacy={missing_from_legacy})"
        )

    lightweight_bytes = len(lightweight)
    legacy_bytes = len(legacy)
    reduction_percent = (
        0.0
        if legacy_bytes == 0
        else (legacy_bytes - lightweight_bytes) / legacy_bytes * 100.0
    )
    return PayloadComparison(
        issue_count=len(lightweight_numbers),
        issue_numbers=lightweight_numbers,
        lightweight_bytes=lightweight_bytes,
        legacy_bytes=legacy_bytes,
        reduction_percent=reduction_percent,
    )


def _run_query(query: str, repo: str | None) -> bytes:
    environment = os.environ.copy()
    if repo is not None:
        environment["GH_REPO"] = repo

    # An argv list keeps the query and GitHub placeholders safe in PowerShell.
    command = [
        "gh",
        "api",
        "graphql",
        "--paginate",
        "--slurp",
        "-F",
        "owner={owner}",
        "-F",
        "name={repo}",
        "-f",
        f"query={query}",
    ]
    try:
        completed = subprocess.run(
            command,
            check=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            env=environment,
        )
    except FileNotFoundError as error:
        raise RuntimeError("gh CLI was not found on PATH") from error
    except subprocess.CalledProcessError as error:
        detail = error.stderr.decode("utf-8", errors="replace").strip()
        raise RuntimeError(f"GitHub GraphQL query failed: {detail}") from error
    return completed.stdout


def _assert_nested_connections_complete(payload: bytes, label: str) -> None:
    nodes = _page_issue_nodes(_parse_json(payload, label), label)
    for issue in nodes:
        number = issue.get("number", "unknown")
        connection_names = ["labels", "subIssues", "blockedBy"]
        if "comments" in issue:
            connection_names.append("comments")
        for connection_name in connection_names:
            connection = issue.get(connection_name)
            if not isinstance(connection, dict):
                raise ValueError(
                    f"{label} issue #{number} has no {connection_name} connection"
                )
            page_info = connection.get("pageInfo")
            if not isinstance(page_info, dict) or "hasNextPage" not in page_info:
                raise ValueError(
                    f"{label} issue #{number} has no {connection_name} pageInfo"
                )
            if page_info["hasNextPage"] is True:
                raise ValueError(
                    f"{label} issue #{number} has truncated {connection_name}; "
                    "refusing to report an incomplete measurement"
                )


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Compare lightweight and legacy open-issue GraphQL payload bytes."
    )
    parser.add_argument(
        "--repo",
        metavar="OWNER/REPO",
        help="repository to query (defaults to the current gh repository context)",
    )
    args = parser.parse_args(argv)

    try:
        lightweight = _run_query(LIGHTWEIGHT_QUERY, args.repo)
        legacy = _run_query(LEGACY_QUERY, args.repo)
        _assert_nested_connections_complete(lightweight, "lightweight")
        _assert_nested_connections_complete(legacy, "legacy")
        report = compare_payloads(lightweight, legacy)
    except (RuntimeError, ValueError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 1

    print(f"Issue count: {report.issue_count}")
    print(f"Lightweight bytes: {report.lightweight_bytes}")
    print(f"Legacy bytes: {report.legacy_bytes}")
    print(f"Reduction percent: {report.reduction_percent:.2f}%")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
