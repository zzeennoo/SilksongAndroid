#!/usr/bin/env python3
"""Reject generated or linked IL2CPP code with a broken System.IO path."""

from __future__ import annotations

import argparse
import re
import subprocess
from pathlib import Path


PATH_RE = re.compile(r"\b(PathInternal_GetIsCaseSensitive_m[0-9A-F]+)\s*\(")
CTOR_RE = re.compile(r"\b(FileStream__ctor_m[0-9A-F]+)\s*\(")
RELEVANT_RE = re.compile(
    r"\b(?:PathInternal_GetIsCaseSensitive|FileStream__ctor)_m[0-9A-F]+\b"
)
UNSUPPORTED = "il2cpp_codegen_get_not_supported_exception"
BRANCH_RE = re.compile(r"(?:^|\s)(?:b|bl)\s+(.+?)\s*$", re.IGNORECASE)
EDGE_TARGET_RE = re.compile(
    r"(?:PathInternal_GetIsCaseSensitive|FileStream__ctor)_m[0-9A-F]+|"
    + re.escape(UNSUPPORTED)
)


def _balanced_end(text: str, start: int, opening: str, closing: str) -> int | None:
    """Return the index after a balanced C/C++ construct, ignoring literals/comments."""
    depth = 0
    state = "code"
    index = start
    while index < len(text):
        char = text[index]
        next_char = text[index + 1] if index + 1 < len(text) else ""
        if state == "line-comment":
            if char == "\n":
                state = "code"
        elif state == "block-comment":
            if char == "*" and next_char == "/":
                state = "code"
                index += 1
        elif state in ("string", "char"):
            if char == "\\":
                index += 1
            elif (state == "string" and char == '"') or (state == "char" and char == "'"):
                state = "code"
        elif char == "/" and next_char == "/":
            state = "line-comment"
            index += 1
        elif char == "/" and next_char == "*":
            state = "block-comment"
            index += 1
        elif char == '"':
            state = "string"
        elif char == "'":
            state = "char"
        elif char == opening:
            depth += 1
        elif char == closing:
            depth -= 1
            if depth == 0:
                return index + 1
        index += 1
    return None


def _skip_space_and_comments(text: str, index: int) -> int:
    while index < len(text):
        match = re.match(r"(?:\s+|//[^\n]*(?:\n|$)|/\*.*?\*/)", text[index:], re.S)
        if match is None:
            return index
        index += match.end()
    return index


def definitions(text: str, pattern: re.Pattern[str]) -> list[tuple[str, str, int]]:
    """Return every real method definition, not declarations or call sites."""
    found: list[tuple[str, str, int]] = []
    for match in pattern.finditer(text):
        opening = text.find("(", match.start(), match.end())
        parameters_end = _balanced_end(text, opening, "(", ")")
        if parameters_end is None:
            continue
        body_start = _skip_space_and_comments(text, parameters_end)
        if body_start >= len(text) or text[body_start] != "{":
            continue
        body_end = _balanced_end(text, body_start, "{", "}")
        if body_end is None:
            continue
        found.append((match.group(1), text[body_start:body_end], match.start()))
    return found


def is_constant_false_body(body: str) -> bool:
    """True only for the exact C++ shape emitted by ``ldc.i4.0; ret``."""
    without_comments = re.sub(r"//[^\n]*(?:\n|$)|/\*.*?\*/", "", body, flags=re.S)
    statement = re.fullmatch(
        r"\{\s*return\s+(.+?)\s*;\s*\}", without_comments.strip(), re.S,
    )
    if statement is None:
        return False
    expression = re.sub(r"\s+", "", statement.group(1))
    # Unity IL2CPP versions have emitted all of these for the same two IL
    # instructions: false, (bool)0, ((bool)0), and static_cast<bool>(0).
    # Normalize only those harmless wrappers. The full-body match above still
    # rejects declarations, branches, calls, or any second statement.
    while True:
        if expression.startswith("(") and _balanced_end(expression, 0, "(", ")") == len(expression):
            expression = expression[1:-1]
            continue
        cast = re.match(r"^\((?:bool|int32_t)\)(.+)$", expression)
        if cast:
            expression = cast.group(1)
            continue
        static_cast = re.fullmatch(r"static_cast<(?:bool|int32_t)>\((.+)\)", expression)
        if static_cast:
            expression = static_cast.group(1)
            continue
        break
    return expression.lower() == "false" or re.fullmatch(r"0[uUlL]*", expression) is not None


def _transitive(graph: dict[str, set[str]], symbol: str) -> set[str]:
    result: set[str] = set()
    pending = list(graph.get(symbol, ()))
    while pending:
        called = pending.pop()
        if called in result:
            continue
        result.add(called)
        pending.extend(graph.get(called, ()))
    return result


def analyze_sources(root: Path, require_case_insensitive_fallback: bool = False) -> dict[str, object]:
    sources = sorted(root.rglob("*.cpp")) + sorted(root.rglob("*.c"))
    if not sources:
        raise SystemExit(f"IL2CPP System.IO audit: no generated sources under {root}")

    path_definitions: dict[str, list[tuple[Path, str, int]]] = {}
    ctor_definitions: dict[str, list[tuple[Path, str, int]]] = {}
    source_texts: dict[Path, str] = {}
    for source in sources:
        text = source.read_text(encoding="utf-8", errors="replace")
        source_texts[source] = text
        for symbol, body, offset in definitions(text, PATH_RE):
            path_definitions.setdefault(symbol, []).append((source, body, offset))
        for symbol, body, offset in definitions(text, CTOR_RE):
            ctor_definitions.setdefault(symbol, []).append((source, body, offset))

    if not path_definitions:
        raise SystemExit("IL2CPP System.IO audit: PathInternal.GetIsCaseSensitive was not generated")

    graph: dict[str, set[str]] = {}
    direct_unsupported: set[str] = set()
    locations: dict[str, list[str]] = {}
    for symbol, items in {**path_definitions, **ctor_definitions}.items():
        graph[symbol] = set()
        locations[symbol] = []
        for source, body, offset in items:
            line = source_texts[source].count("\n", 0, offset) + 1
            locations[symbol].append(f"{source.relative_to(root)}:{line}")
            graph[symbol].update(CTOR_RE.findall(body))
            if UNSUPPORTED in body:
                direct_unsupported.add(symbol)

    # Every definition of a symbol must be the fallback before the symbol is
    # classified as patched. A duplicate old body is exactly what this audit
    # exists to keep out of the linker.
    patched_paths = {
        symbol for symbol, items in path_definitions.items()
        if all(is_constant_false_body(body) for _, body, _ in items)
    }
    roots_without_ctor = [
        symbol for symbol in path_definitions
        if not graph[symbol] and symbol not in patched_paths
    ]
    if roots_without_ctor:
        raise SystemExit(
            "IL2CPP System.IO audit: PathInternal definition(s) call no recognizable "
            "FileStream constructor: " + ", ".join(sorted(roots_without_ctor))
        )
    if require_case_insensitive_fallback:
        unpatched = set(path_definitions) - patched_paths
        if unpatched:
            raise SystemExit(
                "IL2CPP System.IO audit: PathInternal case-sensitivity fallback was not "
                "applied to: " + ", ".join(sorted(unpatched))
            )

    # Resolve the union of every definition for every symbol. A second body is
    # exactly the dangerous case: accepting the first one lets the linker pick
    # a path the audit never saw.
    for root_symbol in sorted(path_definitions):
        pending = [(called, [root_symbol, called]) for called in sorted(graph[root_symbol])]
        visited: set[str] = set()
        while pending:
            symbol, chain = pending.pop(0)
            if symbol in visited:
                continue
            visited.add(symbol)
            bodies = ctor_definitions.get(symbol)
            if not bodies:
                raise SystemExit(
                    "IL2CPP System.IO audit: generated constructor definition not found: "
                    f"{' -> '.join(chain)}"
                )
            if symbol in direct_unsupported:
                where = ", ".join(locations[symbol])
                raise SystemExit(
                    "IL2CPP System.IO audit: " + " -> ".join(chain) +
                    f" reaches an unsupported-method stub at {where}; Android would abort at boot"
                )
            for called in sorted(graph[symbol]):
                pending.append((called, chain + [called]))

    # Mark every constructor that is or can reach an unsupported stub. The
    # linked-binary pass uses this even for constructors not reached by the
    # generated source graph, because a stale object can introduce a new edge.
    unsafe = set(direct_unsupported)
    changed = True
    while changed:
        changed = False
        for symbol in ctor_definitions:
            if symbol not in unsafe and graph[symbol].intersection(unsafe):
                unsafe.add(symbol)
                changed = True

    reachable = set()
    for symbol in path_definitions:
        reachable.update(_transitive(graph, symbol))
    return {
        "paths": set(path_definitions),
        "ctors": set(ctor_definitions),
        "graph": graph,
        "unsafe": unsafe,
        "reachable": reachable,
        "locations": locations,
        "patched_paths": patched_paths,
        "path_definition_count": sum(map(len, path_definitions.values())),
        "reachable_definition_count": sum(
            len(ctor_definitions[symbol]) for symbol in reachable if symbol in ctor_definitions
        ),
    }


def parse_nm(output: str) -> set[str]:
    return set(RELEVANT_RE.findall(output))


def parse_objdump(output: str) -> dict[str, set[str]]:
    """Return named direct AArch64 b/bl edges from llvm-objdump output.

    LLVM has emitted all of these operand forms across the versions used by
    the desktop container and Android toolchain::

        bl 0x10100 <FileStream__ctor_mBBBB>
        bl <FileStream__ctor_mBBBB>
        bl FileStream__ctor_mBBBB
        bl 0x10100

    The previous parser accepted only the first form.  In particular, the
    last form silently erased a stale FileStream edge even though the target
    function was present later in the same disassembly.  Indexing function
    header addresses first lets the audit resolve that form as well.
    """
    graph: dict[str, set[str]] = {}
    current: str | None = None
    header = re.compile(r"^\s*([0-9A-Fa-f]+)\s+<([^>]+)>:\s*$")

    address_symbols: dict[int, str] = {}
    for line in output.splitlines():
        match = header.match(line)
        if match:
            raw = match.group(2).split("+", 1)[0].split("@", 1)[0]
            if RELEVANT_RE.fullmatch(raw):
                address_symbols[int(match.group(1), 16)] = raw

    for line in output.splitlines():
        match = header.match(line)
        if match:
            raw = match.group(2).split("+", 1)[0].split("@", 1)[0]
            current = raw if RELEVANT_RE.fullmatch(raw) else None
            if current is not None:
                graph.setdefault(current, set())
            continue
        if current is None:
            continue
        instruction = line.split(":", 1)
        if len(instruction) != 2:
            continue
        called = BRANCH_RE.search(instruction[1])
        if called is None:
            continue
        operands = called.group(1)
        named = EDGE_TARGET_RE.search(operands)
        target = named.group(0) if named else None
        if target is None:
            # With symbolic operands disabled, llvm-objdump prints only the
            # branch address.  Resolve it against the relevant headers that
            # were collected in the first pass.
            numeric = re.search(r"(?:^|\s)#?(?:0x)?([0-9A-Fa-f]+)\b", operands)
            if numeric:
                target = address_symbols.get(int(numeric.group(1), 16))
        # llvm-objdump labels an intra-function basic-block branch as
        # <FunctionName+0x...>.  Once normalized, that looks like a call from
        # a method to itself.  Self-branches cannot reach another constructor.
        if target is not None and target != current:
            graph[current].add(target)
    return graph


def verify_binary_graph(
    analysis: dict[str, object],
    defined: set[str],
    binary_graph: dict[str, set[str]],
) -> None:
    source_paths: set[str] = analysis["paths"]  # type: ignore[assignment]
    source_ctors: set[str] = analysis["ctors"]  # type: ignore[assignment]
    source_graph: dict[str, set[str]] = analysis["graph"]  # type: ignore[assignment]
    unsafe: set[str] = analysis["unsafe"]  # type: ignore[assignment]
    patched_paths: set[str] = analysis["patched_paths"]  # type: ignore[assignment]

    binary_paths = {symbol for symbol in defined if symbol.startswith("PathInternal_GetIsCaseSensitive_m")}
    binary_ctors = {symbol for symbol in defined if symbol.startswith("FileStream__ctor_m")}
    if binary_paths != source_paths:
        raise SystemExit(
            "IL2CPP binary audit: PathInternal symbols differ from generated source "
            f"(only-source={sorted(source_paths - binary_paths)}, only-binary={sorted(binary_paths - source_paths)})"
        )
    if binary_ctors != source_ctors:
        raise SystemExit(
            "IL2CPP binary audit: FileStream constructor symbols differ from generated source "
            f"(only-source={sorted(source_ctors - binary_ctors)}, only-binary={sorted(binary_ctors - source_ctors)})"
        )

    for root in sorted(source_paths):
        if root not in binary_graph:
            raise SystemExit(f"IL2CPP binary audit: objdump did not disassemble {root}")
        allowed = _transitive(source_graph, root)
        actual = {called for called in binary_graph[root] if called in source_ctors}
        if root in patched_paths and actual:
            raise SystemExit(
                f"IL2CPP binary audit: patched {root} still calls FileStream in the linked ELF: "
                + ", ".join(sorted(actual))
            )
        if root not in patched_paths and not actual:
            raise SystemExit(f"IL2CPP binary audit: {root} has no visible FileStream call in the linked ELF")
        unexpected = actual - allowed
        if unexpected:
            raise SystemExit(
                f"IL2CPP binary audit: {root} calls constructor(s) absent from its verified source path: "
                + ", ".join(sorted(unexpected))
            )

    pending = list(source_paths)
    reached: set[str] = set()
    while pending:
        symbol = pending.pop()
        if symbol in reached:
            continue
        reached.add(symbol)
        calls = binary_graph.get(symbol, set())
        if any(UNSUPPORTED in called for called in calls):
            raise SystemExit(f"IL2CPP binary audit: {symbol} calls {UNSUPPORTED} in the linked ELF")
        actual_ctors = calls.intersection(source_ctors)
        if symbol in source_ctors:
            allowed = _transitive(source_graph, symbol)
            if allowed and not actual_ctors:
                raise SystemExit(
                    f"IL2CPP binary audit: {symbol} has no visible constructor edge in the linked ELF "
                    f"although its verified source reaches {', '.join(sorted(allowed))}; "
                    "refusing to package an unverifiable native object"
                )
            unexpected = actual_ctors - allowed
            if unexpected:
                raise SystemExit(
                    f"IL2CPP binary audit: {symbol} has stale constructor edge(s): "
                    + ", ".join(sorted(unexpected))
                )
        bad = actual_ctors.intersection(unsafe)
        if bad:
            raise SystemExit(
                f"IL2CPP binary audit: {symbol} reaches source-audited unsupported constructor(s): "
                + ", ".join(sorted(bad))
            )
        pending.extend(actual_ctors)


def verify_binary(
    binary: Path,
    analysis: dict[str, object],
    nm: Path,
    objdump: Path,
) -> None:
    nm_run = subprocess.run(
        [str(nm), "--defined-only", "--format=just-symbols", str(binary)],
        text=True, capture_output=True,
    )
    if nm_run.returncode != 0:
        raise SystemExit(f"IL2CPP binary audit: llvm-nm failed: {nm_run.stderr.strip()}")
    defined = parse_nm(nm_run.stdout)
    wanted = sorted(defined)
    if not wanted:
        raise SystemExit("IL2CPP binary audit: linked ELF exposes no System.IO symbols")

    objdump_run = subprocess.run(
        [
            str(objdump), "--disassemble", "--no-show-raw-insn",
            "--disassemble-symbols=" + ",".join(wanted), str(binary),
        ],
        text=True, capture_output=True,
    )
    if objdump_run.returncode != 0:
        raise SystemExit(f"IL2CPP binary audit: llvm-objdump failed: {objdump_run.stderr.strip()}")
    graph = parse_objdump(objdump_run.stdout)
    verify_binary_graph(analysis, defined, graph)
    print(
        f"[docker] verified linked ARM64 System.IO graph: "
        f"{len(analysis['paths'])} PathInternal symbol(s), {len(analysis['reachable'])} reachable constructor(s)"
    )
    for symbol in sorted(set(analysis["paths"]) | set(analysis["reachable"])):
        called = sorted(
            target for target in graph.get(symbol, set())
            if RELEVANT_RE.fullmatch(target) or target == UNSUPPORTED
        )
        print(f"[docker]   linked {symbol} -> {', '.join(called) or '<implemented terminal>'}")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("generated_cpp_dir", type=Path)
    parser.add_argument("--binary", type=Path)
    parser.add_argument("--nm", type=Path)
    parser.add_argument("--objdump", type=Path)
    parser.add_argument(
        "--require-case-insensitive-fallback",
        action="store_true",
        help="require every PathInternal.GetIsCaseSensitive body to return false directly",
    )
    args = parser.parse_args()
    if args.binary is not None and (args.nm is None or args.objdump is None):
        parser.error("--binary requires --nm and --objdump")

    analysis = analyze_sources(
        args.generated_cpp_dir,
        require_case_insensitive_fallback=args.require_case_insensitive_fallback,
    )
    print(
        f"[docker] verified generated System.IO graph: "
        f"{analysis['path_definition_count']} PathInternal definition(s), "
        f"{analysis['reachable_definition_count']} reachable constructor definition(s)"
    )
    locations: dict[str, list[str]] = analysis["locations"]  # type: ignore[assignment]
    graph: dict[str, set[str]] = analysis["graph"]  # type: ignore[assignment]
    for symbol in sorted(analysis["paths"]):
        target = "<constant false fallback>" if symbol in analysis["patched_paths"] else \
            ", ".join(sorted(graph[symbol]))
        print(f"[docker]   {symbol} [{', '.join(locations[symbol])}] -> {target}")
    for symbol in sorted(analysis["reachable"]):
        called = ", ".join(sorted(graph[symbol])) or "<implemented terminal>"
        print(f"[docker]   {symbol} [{', '.join(locations[symbol])}] -> {called}")
    if args.binary is not None:
        verify_binary(args.binary, analysis, args.nm, args.objdump)


if __name__ == "__main__":
    main()
