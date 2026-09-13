#!/usr/bin/env python3
"""Reject IL2CPP output that cannot initialize System.IO.PathInternal."""

from __future__ import annotations

import re
import sys
from pathlib import Path


def method_body(text: str, symbol: str) -> str | None:
    """Return the balanced C++ body for a generated method definition."""
    for match in re.finditer(rf"\b{re.escape(symbol)}\s*\([^;]*?\)\s*\{{", text, re.S):
        start = match.end() - 1
        depth = 0
        for index in range(start, len(text)):
            if text[index] == "{":
                depth += 1
            elif text[index] == "}":
                depth -= 1
                if depth == 0:
                    return text[start:index + 1]
    return None


def verify(root: Path) -> None:
    sources = sorted(root.glob("*.cpp")) + sorted(root.glob("*.c"))
    texts = [source.read_text(encoding="utf-8", errors="replace") for source in sources]
    path_body: str | None = None
    for text in texts:
        if "PathInternal_GetIsCaseSensitive" in text:
            match = re.search(r"\b(PathInternal_GetIsCaseSensitive_m[0-9A-F]+)\s*\(", text)
            if match:
                path_body = method_body(text, match.group(1))
                if path_body is not None:
                    break

    if path_body is None:
        raise SystemExit("IL2CPP smoke: PathInternal.GetIsCaseSensitive was not generated")

    roots = re.findall(r"\b(FileStream__ctor_m[0-9A-F]+)\s*\(", path_body)
    if not roots:
        raise SystemExit("IL2CPP smoke: PathInternal no longer calls a recognizable FileStream constructor")

    # PathInternal calls one convenience overload, which can call another.
    # The original regression lived in that second constructor, so checking
    # only the direct call produces a dangerously reassuring false positive.
    # Follow every generated FileStream constructor edge until the chain ends.
    pending = list(dict.fromkeys(roots))
    verified: list[str] = []
    while pending:
        symbol = pending.pop(0)
        if symbol in verified:
            continue
        body = next((method_body(text, symbol) for text in texts if symbol in text), None)
        if body is None:
            raise SystemExit(f"IL2CPP smoke: generated constructor definition not found: {symbol}")
        if "il2cpp_codegen_get_not_supported_exception" in body:
            chain = " -> ".join(verified + [symbol])
            raise SystemExit(
                f"IL2CPP smoke: {chain} reaches an unsupported-method stub; Android would abort at boot"
            )
        verified.append(symbol)
        for called in re.findall(r"\b(FileStream__ctor_m[0-9A-F]+)\s*\(", body):
            if called not in verified and called not in pending:
                pending.append(called)

    print("[docker] verified Android System.IO constructor chain: " + " -> ".join(verified))


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit(f"usage: {Path(sys.argv[0]).name} GENERATED_CPP_DIR")
    verify(Path(sys.argv[1]))
