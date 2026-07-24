#!/usr/bin/env python3
"""Fail CI if tracked configuration or source files contain likely credentials."""

from __future__ import annotations

import json
import pathlib
import re
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parents[1]
SENSITIVE_KEY = re.compile(r"(?i)(token|key|secret|password|credential)$")
SAFE_KEY_NAMES = {"PublicKey", "ApiKeyEnvironmentVariable"}
TOKEN_PATTERNS = {
    "Discord bot token": re.compile(r"\b[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{20,}\b"),
    "GitHub token": re.compile(r"\b(?:gh[opsur]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,})\b"),
    "OpenAI-style key": re.compile(r"\bsk-[A-Za-z0-9_-]{20,}\b"),
    "Groq key": re.compile(r"\bgsk_[A-Za-z0-9_-]{20,}\b"),
    "OpenRouter key": re.compile(r"\bsk-or-v1-[A-Za-z0-9]{20,}\b"),
}


def tracked_files() -> list[pathlib.Path]:
    output = subprocess.check_output(
        ["git", "ls-files", "-z"], cwd=ROOT
    ).decode("utf-8", errors="surrogateescape")
    return [ROOT / name for name in output.split("\0") if name]


def is_configuration_json(path: pathlib.Path) -> bool:
    name = path.name.casefold()
    return name.endswith(".json") and (
        name.startswith("appsettings") or name == "secrets.json"
    )


def inspect_json(value: object, path: str, findings: list[str]) -> None:
    if isinstance(value, dict):
        for key, child in value.items():
            child_path = f"{path}:{key}" if path else key
            if (
                key not in SAFE_KEY_NAMES
                and SENSITIVE_KEY.search(key)
                and isinstance(child, str)
                and child.strip()
            ):
                findings.append(f"non-empty sensitive setting: {child_path}")
            inspect_json(child, child_path, findings)
    elif isinstance(value, list):
        for index, child in enumerate(value):
            inspect_json(child, f"{path}[{index}]", findings)


def main() -> int:
    findings: list[str] = []
    for file_path in tracked_files():
        relative = file_path.relative_to(ROOT)
        try:
            raw = file_path.read_bytes()
        except OSError as error:
            findings.append(f"{relative}: could not be scanned ({error})")
            continue

        is_config_json = is_configuration_json(relative)
        try:
            text = raw.decode("utf-8-sig")
        except UnicodeDecodeError as error:
            if is_config_json:
                findings.append(f"{relative}: configuration is not valid UTF-8 ({error})")
            text = raw.decode("utf-8", errors="ignore")

        for label, pattern in TOKEN_PATTERNS.items():
            if pattern.search(text):
                findings.append(f"{relative}: likely {label}")

        if is_config_json:
            try:
                inspect_json(json.loads(text), str(relative), findings)
            except json.JSONDecodeError as error:
                findings.append(f"{relative}: invalid JSON ({error})")

    if findings:
        print("Credential scan failed:", file=sys.stderr)
        for finding in findings:
            print(f"  - {finding}", file=sys.stderr)
        return 1

    print("Credential scan passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
