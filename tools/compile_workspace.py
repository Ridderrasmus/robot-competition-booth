#!/usr/bin/env python3
"""Compile a saved Blockly workspace into the versioned Robobooth instruction IR."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


CONTRACT_VERSION = 1
COMPILER_VERSION = "0.1.0"
MAX_BLOCKS = 1_000
MAX_DEPTH = 128
BLOCK_TYPE = re.compile(r"^[a-z][a-z0-9_]{0,79}$")
INPUT_NAME = re.compile(r"^[A-Za-z][A-Za-z0-9_]{0,79}$")
PROGRAM_ID_CHARACTER = re.compile(r"[^A-Za-z0-9._-]+")

EVENT_PREFIXES = ("dst_on_", "clr_on_", "lin_on_", "com_on_")


class CompileError(ValueError):
    """Raised when a workspace cannot be represented safely."""


class WorkspaceCompiler:
    def __init__(self) -> None:
        self._seen_ids: set[str] = set()
        self._block_count = 0
        self._generated_id = 0

    def compile(self, workspace: dict[str, Any], program_name: str) -> dict[str, Any]:
        roots = self._workspace_roots(workspace)
        normalized_program_name = program_name.strip() or "Program"
        if len(normalized_program_name) > 80:
            raise CompileError("The program name cannot be longer than 80 characters.")
        if len(roots) > 128:
            raise CompileError("The workspace cannot contain more than 128 top-level stacks.")
        on_start_count = sum(root.get("type") in {"prg_on_start", "robot_start"} for root in roots)
        if on_start_count > 1:
            raise CompileError("A program can contain only one top-level on-start stack.")

        warnings: list[str] = []
        entrypoints = []
        for root in sorted(roots, key=lambda value: (value.get("y", 0), value.get("x", 0), value.get("id", ""))):
            kind = self._entrypoint_kind(root.get("type"))
            if kind == "looseStack":
                warnings.append(
                    f"Top-level block {root.get('type', '<unknown>')} is not an event and will not run automatically."
                )
            entrypoints.append({"kind": kind, "root": self._compile_block(root, 0)})

        canonical_workspace = json.dumps(workspace, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")
        workspace_sha256 = hashlib.sha256(canonical_workspace).hexdigest()
        safe_name = PROGRAM_ID_CHARACTER.sub("-", normalized_program_name).strip("-._") or "program"
        program_id = f"{safe_name[:48]}-{workspace_sha256[:12]}"

        package: dict[str, Any] = {
            "contractVersion": CONTRACT_VERSION,
            "programId": program_id,
            "programName": normalized_program_name,
            "workspaceSha256": workspace_sha256,
            "compiledAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
            "compiler": {"name": "robobooth-python", "version": COMPILER_VERSION},
            "target": {"runtime": "robobooth", "minimumRuntimeVersion": "1.0.0"},
            "safety": {"stopAllOutputsOnEnd": True, "stopAllOutputsOnFault": True},
            "variables": self._normalise_variables(workspace.get("variables", [])),
            "entrypoints": entrypoints,
        }
        if warnings:
            package["warnings"] = warnings
        return package

    @staticmethod
    def _workspace_roots(workspace: dict[str, Any]) -> list[dict[str, Any]]:
        if not isinstance(workspace, dict):
            raise CompileError("The Blockly workspace must be a JSON object.")
        blocks = workspace.get("blocks")
        if not isinstance(blocks, dict) or not isinstance(blocks.get("blocks"), list):
            raise CompileError("The Blockly workspace does not contain blocks.blocks.")
        if not all(isinstance(block, dict) for block in blocks["blocks"]):
            raise CompileError("Every top-level Blockly block must be an object.")
        return blocks["blocks"]

    @staticmethod
    def _entrypoint_kind(block_type: Any) -> str:
        if block_type in {"prg_on_start", "robot_start"}:
            return "onStart"
        if block_type == "prg_forever":
            return "forever"
        if isinstance(block_type, str) and block_type.startswith(EVENT_PREFIXES):
            return "event"
        if isinstance(block_type, str) and block_type.startswith("procedures_def"):
            return "function"
        return "looseStack"

    def _compile_block(self, block: dict[str, Any], depth: int) -> dict[str, Any]:
        if depth > MAX_DEPTH:
            raise CompileError(f"The block graph is deeper than {MAX_DEPTH} levels.")
        self._block_count += 1
        if self._block_count > MAX_BLOCKS:
            raise CompileError(f"The workspace contains more than {MAX_BLOCKS} blocks.")

        block_type = block.get("type")
        if not isinstance(block_type, str) or not BLOCK_TYPE.fullmatch(block_type):
            raise CompileError(f"Invalid Blockly block type: {block_type!r}.")

        block_id = block.get("id")
        if not isinstance(block_id, str) or not block_id:
            self._generated_id += 1
            block_id = f"generated-{self._generated_id}"
        if len(block_id) > 128:
            raise CompileError("Blockly block ids cannot be longer than 128 characters.")
        if block_id in self._seen_ids:
            raise CompileError(f"Duplicate Blockly block id: {block_id}.")
        self._seen_ids.add(block_id)

        node: dict[str, Any] = {"id": block_id, "opcode": block_type}
        fields = block.get("fields")
        if isinstance(fields, dict) and fields:
            node["fields"] = fields

        inputs: dict[str, Any] = {}
        for name, connection in sorted((block.get("inputs") or {}).items()):
            if not isinstance(name, str) or not INPUT_NAME.fullmatch(name) or not isinstance(connection, dict):
                raise CompileError(f"Invalid input on block {block_id}.")
            child = connection.get("block") or connection.get("shadow")
            if child is not None:
                if not isinstance(child, dict):
                    raise CompileError(f"Input {name} on block {block_id} is not a block.")
                inputs[name] = self._compile_block(child, depth + 1)
        if inputs:
            node["inputs"] = inputs

        state = {key: block[key] for key in ("extraState", "mutation") if key in block}
        if state:
            node["state"] = state
        if block.get("enabled") is False:
            node["disabled"] = True

        next_connection = block.get("next")
        if isinstance(next_connection, dict) and isinstance(next_connection.get("block"), dict):
            node["next"] = self._compile_block(next_connection["block"], depth + 1)
        return node

    @staticmethod
    def _normalise_variables(variables: Any) -> list[dict[str, Any]]:
        if variables is None:
            return []
        if not isinstance(variables, list):
            raise CompileError("Workspace variables must be an array.")
        result = []
        for variable in variables:
            if not isinstance(variable, dict) or not isinstance(variable.get("id"), str):
                raise CompileError("Every workspace variable must have an id.")
            variable_id = variable["id"]
            variable_name = str(variable.get("name", "variable"))
            variable_type = str(variable.get("type", ""))
            if not variable_id or len(variable_id) > 128 or not variable_name or len(variable_name) > 80 or len(variable_type) > 80:
                raise CompileError("A workspace variable exceeds the v1 id, name, or type limit.")
            result.append({
                "id": variable_id,
                "name": variable_name,
                "type": variable_type,
            })
        return sorted(result, key=lambda value: value["id"])


def compile_workspace(workspace: dict[str, Any], program_name: str) -> dict[str, Any]:
    return WorkspaceCompiler().compile(workspace, program_name)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("workspace", type=Path, help="Blockly workspace JSON file")
    parser.add_argument("--output", "-o", type=Path, help="Output instruction package; defaults to stdout")
    parser.add_argument("--name", help="Program display name; defaults to the workspace file name")
    args = parser.parse_args()

    try:
        workspace = json.loads(args.workspace.read_text(encoding="utf-8"))
        package = compile_workspace(workspace, args.name or args.workspace.stem)
    except (OSError, json.JSONDecodeError, CompileError) as error:
        parser.error(str(error))

    output = json.dumps(package, ensure_ascii=False, indent=2) + "\n"
    if args.output:
        args.output.write_text(output, encoding="utf-8", newline="\n")
    else:
        print(output, end="")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
