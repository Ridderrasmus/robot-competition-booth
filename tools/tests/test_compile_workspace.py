import importlib.util
import sys
import unittest
from pathlib import Path


SCRIPT = Path(__file__).parents[1] / "compile_workspace.py"
SPEC = importlib.util.spec_from_file_location("compile_workspace", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC and SPEC.loader
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)


class CompileWorkspaceTests(unittest.TestCase):
    def test_compiles_event_stack_and_nested_reporter(self):
        workspace = {
            "blocks": {
                "languageVersion": 0,
                "blocks": [{
                    "type": "prg_on_start",
                    "id": "start",
                    "x": 40,
                    "y": 40,
                    "next": {"block": {
                        "type": "mot_run",
                        "id": "motor",
                        "fields": {"MOTOR": "left"},
                        "inputs": {"SPEED": {"shadow": {
                            "type": "math_number", "id": "speed", "fields": {"NUM": 50}
                        }}}
                    }}
                }]
            }
        }

        package = MODULE.compile_workspace(workspace, "Race program")

        self.assertEqual(1, package["contractVersion"])
        self.assertEqual("onStart", package["entrypoints"][0]["kind"])
        motor = package["entrypoints"][0]["root"]["next"]
        self.assertEqual("mot_run", motor["opcode"])
        self.assertEqual(50, motor["inputs"]["SPEED"]["fields"]["NUM"])
        self.assertNotIn("x", package["entrypoints"][0]["root"])

    def test_rejects_multiple_on_start_stacks(self):
        workspace = {"blocks": {"blocks": [
            {"type": "prg_on_start", "id": "one"},
            {"type": "prg_on_start", "id": "two"},
        ]}}

        with self.assertRaisesRegex(MODULE.CompileError, "only one"):
            MODULE.compile_workspace(workspace, "Bad program")

    def test_warns_for_loose_top_level_stack(self):
        workspace = {"blocks": {"blocks": [{"type": "mot_stop_all", "id": "loose"}]}}

        package = MODULE.compile_workspace(workspace, "Loose")

        self.assertEqual("looseStack", package["entrypoints"][0]["kind"])
        self.assertTrue(package["warnings"])


if __name__ == "__main__":
    unittest.main()
