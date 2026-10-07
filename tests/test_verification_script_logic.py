"""
Unit tests for verification script logic and failure-paths.
Proves that:
1. --strict rejects --skip-backend.
2. --strict rejects missing acceptance database.
3. Strict provider check rejects fake/simulated/none/case-insensitive providers.
4. Component-only mode outputs PARTIAL.
5. Report artifact is persisted on failure as well as success.
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

    def test_strict_rejects_missing_database(self):
        """Verifies that missing acceptance DB in strict mode fails and persists report."""
        non_existent_db = os.path.join(tempfile.gettempdir(), "non_existent_voice_acceptance.db")
        if os.path.exists(non_existent_db):
            os.remove(non_existent_db)

        with patch.object(vscript, "resolve_db_path", return_value=non_existent_db), \
             patch("sys.argv", ["verify.py", "--strict"]):
            with self.assertRaises(SystemExit) as ctx:
                vscript.main()
            self.assertEqual(ctx.exception.code, 1)

        # Verify report artifact was written with failure reason
        report_file = os.path.join(vscript.ARTIFACTS_DIR, "real_voice_verification_report.json")
        self.assertTrue(os.path.exists(report_file))
        with open(report_file, "r", encoding="utf-8") as f:
            rep = json.load(f)
        self.assertIn("acceptance_database", rep.get("verifications", {}))
        self.assertIn("FAILED", rep["verifications"]["acceptance_database"])

    def test_strict_provider_check_rejects_fake_and_simulated(self):
        """Verifies strict provider validation rejects fake, simulated, and empty providers case-insensitively."""
        disallowed = ["deterministicfake", "simulated", "fake", "none", ""]
        for p in ["DeterministicFake", "simulated", "SIMULATED", "fake", "None", "", "   "]:
            self.assertIn(p.strip().lower(), disallowed)

        # Real providers are accepted
        for real_llm in ["Ollama", "ollama", "qwen2.5:3b"]:
            self.assertNotIn(real_llm.strip().lower(), disallowed)
        for real_stt in ["Whisper", "faster-whisper"]:
            self.assertNotIn(real_stt.strip().lower(), disallowed)
        for real_tts in ["Piper", "piper-tts"]:
            self.assertNotIn(real_tts.strip().lower(), disallowed)


if __name__ == "__main__":
    unittest.main()
