"""Shared capture launch options remain compatible with older capture drivers."""
import importlib.util
from pathlib import Path
from types import SimpleNamespace
import tempfile
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("jumpshot_capture_options", ROOT / "tools/run_jumpshot_capture.py")
capture = importlib.util.module_from_spec(spec)
spec.loader.exec_module(capture)


class JumpshotCaptureOptionsTests(unittest.TestCase):
    # Patch only process creation; command construction and log IO remain real.
    # https://docs.python.org/3.14/library/unittest.mock.html#unittest.mock.patch
    def legacy_args(self):
        # The transition capture driver builds this namespace without heading
        # options; only the ordinary capture CLI declares those optional fields.
        return SimpleNamespace(
            godot="godot", remote=False, paced=False, resolution="960x540",
            headless=True, compatibility=False, view="gameplay", commit="test",
            port=24450, pacing_ms=80, entry="dribble", entry_offset=0,
            subject_role="host", before440=False, legacy_shot=False,
            fadeaway_substitute=None)

    def test_legacy_namespace_launches_without_heading_flags(self):
        with tempfile.TemporaryDirectory() as directory:
            with patch.object(capture.subprocess, "Popen") as launch:
                process, log = capture.start(self.legacy_args(), Path(directory) / "capture", "local", "Right", "normal")
                try:
                    self.assertIs(process, launch.return_value)
                    command = launch.call_args.args[0]
                    self.assertFalse(any(arg.startswith("--capture-heading-") for arg in command))
                    self.assertTrue(Path(log.name).exists())
                    self.assertIs(launch.call_args.kwargs["stdout"], log)
                finally:
                    log.close()

    def test_explicit_zero_angle_forwards_all_heading_options(self):
        args = self.legacy_args()
        args.heading_angle = 0.0
        args.heading_rate = 90.0
        args.heading_entry = "moving"
        with tempfile.TemporaryDirectory() as directory:
            with patch.object(capture.subprocess, "Popen") as launch:
                _, log = capture.start(args, Path(directory) / "capture", "local", "Right", "normal")
                try:
                    command = launch.call_args.args[0]
                    self.assertEqual(
                        ["--capture-heading-angle=0.0", "--capture-heading-rate=90.0", "--capture-heading-entry=moving"],
                        [arg for arg in command if arg.startswith("--capture-heading-")])
                finally:
                    log.close()


if __name__ == "__main__":
    unittest.main()
