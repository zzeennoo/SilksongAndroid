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
    texts: list[tuple[Path, str]] = []
    path_body: str | None = None
    for source in sources:
        text = source.read_text(encoding="utf-8", errors="replace")
        texts.append((source, text))
        if "PathInternal_GetIsCaseSensitive" in text:
            match = re.search(r"\b(PathInternal_GetIsCaseSensitive_m[0-9A-F]+)\s*\(", text)
            if match:
                path_body = method_body(text, match.group(1))
                if path_body is not None:
                    break

    if path_body is None:
        raise SystemExit("IL2CPP smoke: PathInternal.GetIsCaseSensitive was not generated")

    ctor = re.search(r"\b(FileStream__ctor_m[0-9A-F]+)\s*\(", path_body)
    if ctor is None:
        raise SystemExit("IL2CPP smoke: PathInternal no longer calls a recognizable FileStream constructor")

    symbol = ctor.group(1)
    for _, text in texts:
        if symbol not in text:
            continue
        body = method_body(text, symbol)
        if body is not None:
            if "il2cpp_codegen_get_not_supported_exception" in body:
                raise SystemExit(
                    f"IL2CPP smoke: {symbol} is an unsupported-method stub; Android would abort at boot"
                )
            print(f"[docker] verified Android System.IO implementation: {symbol}")
            return

    # The constructor's source might sort after the PathInternal source. Read
    # any files not reached before the early break above.
    seen = {path for path, _ in texts}
    for source in sources:
        if source in seen:
            continue
        text = source.read_text(encoding="utf-8", errors="replace")
        if symbol not in text:
            continue
        body = method_body(text, symbol)
        if body is not None:
            if "il2cpp_codegen_get_not_supported_exception" in body:
                raise SystemExit(
                    f"IL2CPP smoke: {symbol} is an unsupported-method stub; Android would abort at boot"
                )
            print(f"[docker] verified Android System.IO implementation: {symbol}")
            return


    raise SystemExit(f"IL2CPP smoke: generated constructor definition not found: {symbol}")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit(f"usage: {Path(sys.argv[0]).name} GENERATED_CPP_DIR")
    verify(Path(sys.argv[1]))
