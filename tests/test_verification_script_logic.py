"""
Unit tests for verification script logic and failure-paths.
Proves that:
1. --strict rejects --skip-backend.
2. --strict rejects missing acceptance database and persists failure report into isolated temporary directory.
3. validate_backend_providers whitelist rejects fake, simulated, missing, unknown typo configurations, and simulated concrete types.
4. Component-only mode outputs PARTIAL.
"""

import os
import sys
import json
import unittest
import tempfile
from unittest.mock import patch, MagicMock

BASE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if BASE_DIR not in sys.path:
    sys.path.insert(0, BASE_DIR)

import importlib

vscript = importlib.import_module("scripts.verify-real-local-voice-pipeline")


class TestVerificationScriptLogic(unittest.TestCase):

    def test_strict_rejects_skip_backend(self):
        """Verifies that running --strict with --skip-backend immediately exits nonzero."""
        with patch("sys.argv", ["verify.py", "--strict", "--skip-backend"]):
            with self.assertRaises(SystemExit) as ctx:
                vscript.main()
            self.assertEqual(ctx.exception.code, 1)

    def test_strict_rejects_missing_database_and_uses_isolated_dir(self):
        """Verifies that missing acceptance DB in strict mode fails and writes to isolated directory without overwriting real report."""
        with tempfile.TemporaryDirectory() as tmp_dir:
            non_existent_db = os.path.join(tmp_dir, "non_existent_voice_acceptance.db")
            with patch.object(vscript, "resolve_db_path", return_value=non_existent_db), \
                 patch.object(vscript, "ARTIFACTS_DIR", tmp_dir), \
                 patch("sys.argv", ["verify.py", "--strict"]):
                with self.assertRaises(SystemExit) as ctx:
                    vscript.main()
                self.assertEqual(ctx.exception.code, 1)

            # Verify report artifact was written to temporary directory
            report_file = os.path.join(tmp_dir, "real_voice_verification_report.json")
            self.assertTrue(os.path.exists(report_file))
            with open(report_file, "r", encoding="utf-8") as f:
                rep = json.load(f)
            self.assertIn("acceptance_database", rep.get("verifications", {}))
            self.assertIn("FAILED", rep["verifications"]["acceptance_database"])

    def test_validate_backend_providers_with_whitelist_and_concrete_types(self):
        """
        Directly exercises vscript.validate_backend_providers, proving that:
        - Real authorized providers pass.
        - Unknown typo configurations fail.
        - Simulated / fake providers fail.
        - Simulated resolved concrete DI types fail.
        """
        # 1. Valid real configurations
        vscript.validate_backend_providers("Ollama", "Whisper", "Piper")
        vscript.validate_backend_providers("ollama", "LocalEgyptian", "LocalEgyptian")
        vscript.validate_backend_providers("OLLAMA", "real", "real",
                                          resolved_llm="OllamaLlmProvider",
                                          resolved_stt="WhisperSttProvider",
                                          resolved_tts="LocalEgyptianTtsProvider")

        # 2. Typos and unknown configurations must fail
        with self.assertRaises(ValueError) as ctx1:
            vscript.validate_backend_providers("UnknownLlm", "Whisper", "Piper")
        self.assertIn("LLM", str(ctx1.exception))

        with self.assertRaises(ValueError) as ctx2:
            vscript.validate_backend_providers("Ollama", "WhisprTypo", "Piper")
        self.assertIn("STT", str(ctx2.exception))

        with self.assertRaises(ValueError) as ctx3:
            vscript.validate_backend_providers("Ollama", "Whisper", "PiprTypo")
        self.assertIn("TTS", str(ctx3.exception))

        # 3. Simulated and fake configurations must fail
        for fake in ["DeterministicFake", "Simulated", "fake", ""]:
            with self.assertRaises(ValueError):
                vscript.validate_backend_providers(fake, "Whisper", "Piper")
            with self.assertRaises(ValueError):
                vscript.validate_backend_providers("Ollama", fake, "Piper")
            with self.assertRaises(ValueError):
                vscript.validate_backend_providers("Ollama", "Whisper", fake)

        # 4. Resolved concrete types mismatch must fail
        with self.assertRaises(ValueError):
            vscript.validate_backend_providers("Ollama", "Whisper", "Piper",
                                              resolved_stt="SimulatedSttProvider")

        with self.assertRaises(ValueError):
            vscript.validate_backend_providers("Ollama", "Whisper", "Piper",
                                              resolved_tts="SimulatedTtsProvider")

        with self.assertRaises(ValueError):
            vscript.validate_backend_providers("Ollama", "Whisper", "Piper",
                                              resolved_llm="DeterministicFakeLlmProvider")

    def test_final_gate_succeeds_when_all_criteria_passed(self):
        """Verifies that when all REQUIRED_CRITERIA_FULL pass, main() succeeds cleanly."""
        def mock_execute(args, report):
            for crit in vscript.REQUIRED_CRITERIA_FULL:
                report["verifications"][crit] = "PASSED"

        with tempfile.TemporaryDirectory() as tmp_dir:
            with patch.object(vscript, "execute_verification", side_effect=mock_execute), \
                 patch.object(vscript, "ARTIFACTS_DIR", tmp_dir), \
                 patch("sys.argv", ["verify.py"]):
                # Should not raise SystemExit
                vscript.main()

    def test_final_gate_fails_when_criterion_is_missing_or_failed(self):
        """Verifies that if any required criterion (e.g. tts_benchmark) is missing or failed, main() exits 1."""
        def mock_execute_missing_tts(args, report):
            for crit in vscript.REQUIRED_CRITERIA_FULL:
                if crit != "tts_benchmark":
                    report["verifications"][crit] = "PASSED"

        with tempfile.TemporaryDirectory() as tmp_dir:
            with patch.object(vscript, "execute_verification", side_effect=mock_execute_missing_tts), \
                 patch.object(vscript, "ARTIFACTS_DIR", tmp_dir), \
                 patch("sys.argv", ["verify.py"]):
                with self.assertRaises(SystemExit) as ctx:
                    vscript.main()
                self.assertEqual(ctx.exception.code, 1)

    def test_component_only_mode_behavior(self):
        """Verifies component-only mode exits nonzero on failure, and succeeds (exits 0) on pass."""
        # 1. Failure case
        def mock_execute_fail(args, report):
            for crit in vscript.REQUIRED_CRITERIA_COMPONENT:
                if crit != "tts_benchmark":
                    report["verifications"][crit] = "PASSED"

        with tempfile.TemporaryDirectory() as tmp_dir:
            with patch.object(vscript, "execute_verification", side_effect=mock_execute_fail), \
                 patch.object(vscript, "ARTIFACTS_DIR", tmp_dir), \
                 patch("sys.argv", ["verify.py", "--skip-backend"]):
                with self.assertRaises(SystemExit) as ctx:
                    vscript.main()
                self.assertEqual(ctx.exception.code, 1)

        # 2. Success case (PARTIAL)
        def mock_execute_pass(args, report):
            for crit in vscript.REQUIRED_CRITERIA_COMPONENT:
                report["verifications"][crit] = "PASSED"

        with tempfile.TemporaryDirectory() as tmp_dir:
            with patch.object(vscript, "execute_verification", side_effect=mock_execute_pass), \
                 patch.object(vscript, "ARTIFACTS_DIR", tmp_dir), \
                 patch("sys.argv", ["verify.py", "--skip-backend"]):
                # Must exit cleanly without exception
                vscript.main()


if __name__ == "__main__":
    unittest.main()
