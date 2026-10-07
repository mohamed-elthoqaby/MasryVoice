"""
Unit tests for voice_server.py lifecycle, capacity retention on cancellation,
timeout deadline ownership, PyAV cancellation, ASGI cancellation,
and recovery under repeated interruption.
"""

import sys
import os
import io
import time
import wave
import asyncio
import threading
import unittest
from unittest.mock import MagicMock, patch
from fastapi import HTTPException, UploadFile
from starlette.requests import Request

# Ensure root directory is in sys.path
BASE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if BASE_DIR not in sys.path:
    sys.path.insert(0, BASE_DIR)

from scripts import voice_server


class TestVoiceServerLifecycle(unittest.TestCase):

    def setUp(self):
        # Reset semaphores and active task trackers to clean baseline
        voice_server.tts_semaphore = asyncio.Semaphore(voice_server.TTS_CONCURRENCY)
        voice_server.stt_semaphore = asyncio.Semaphore(voice_server.STT_CONCURRENCY)
        voice_server._active_stt_tasks.clear()
        voice_server._active_tts_tasks.clear()

    def test_tts_worker_exceeding_5s_deadline_retains_capacity_until_actual_finish(self):
        """
        Proves that when a cancelled request exceeds the 5.0s wait deadline,
        the handler returns HTTP 499 but capacity and active worker count
        remain HELD by the worker task until the OS thread genuinely terminates.
        """
        async def run_test():
            worker_started = threading.Event()
            worker_can_finish = threading.Event()
            worker_finished = threading.Event()

            mock_voice = MagicMock()

            def very_slow_synthesize(text):
                worker_started.set()
                try:
                    chunk = MagicMock()
                    chunk.audio_int16_bytes = b"\x00" * 3200
                    yield chunk
                    # Worker is stuck/slow and will exceed the 5.0s route deadline
                    worker_can_finish.wait(timeout=10.0)
                    yield chunk
                finally:
                    worker_finished.set()

            mock_voice.synthesize = very_slow_synthesize
            voice_server.piper_voice = mock_voice

            async def mock_is_disconnected():
                return worker_started.is_set()

            mock_request = MagicMock(spec=Request)
            mock_request.is_disconnected = mock_is_disconnected

            req_payload = voice_server.SpeechRequest(input="نص اختباري لتجاوز مهلة الخمس ثواني")

            real_wait_for = asyncio.wait_for
            with patch("scripts.voice_server.asyncio.wait_for") as mock_wait_for:
                async def patched_wait_for(fut, timeout):
                    if timeout == 5.0:
                        # Emulate the 5s deadline expiring while worker is still running
                        await asyncio.sleep(0.05)
                        raise asyncio.TimeoutError()
                    return await real_wait_for(fut, timeout=timeout)
                mock_wait_for.side_effect = patched_wait_for

                task = asyncio.create_task(
                    voice_server.synthesize_speech(mock_request, req_payload)
                )

                # The handler should exit with 499 because deadline expired
                with self.assertRaises(HTTPException) as ctx:
                    await task

                self.assertEqual(ctx.exception.status_code, 499)

            # AT THIS POINT: Handler has exited with 499, BUT worker thread is STILL RUNNING!
            self.assertFalse(worker_finished.is_set(), "Worker thread should still be running!")
            self.assertEqual(len(voice_server._active_tts_tasks), 1, "Worker task must still be tracked as active!")
            self.assertLess(
                voice_server.tts_semaphore._value,
                voice_server.TTS_CONCURRENCY,
                "Semaphore permit MUST NOT be released while worker thread is still computing!"
            )

            # Now allow the worker thread to finish
            worker_can_finish.set()
            # Give background task event loop cycle to complete its finally block
            for _ in range(50):
                if worker_finished.is_set() and len(voice_server._active_tts_tasks) == 0:
                    break
                await asyncio.sleep(0.02)

            self.assertTrue(worker_finished.is_set(), "Worker thread should have terminated")
            self.assertEqual(len(voice_server._active_tts_tasks), 0, "Active worker tracking must be cleared!")
            self.assertEqual(
                voice_server.tts_semaphore._value,
                voice_server.TTS_CONCURRENCY,
                "Capacity must be restored ONLY after worker actually terminates!"
            )

        asyncio.run(run_test())

    def test_stt_cancellation_during_pyav_decode(self):
        """
        Verifies that when client disconnects during PyAV container decoding,
        the stop_event halts decoding early, the temporary file is deleted,
        and capacity is safely released.
        """
        async def run_test():
            decode_started = threading.Event()
            stop_event = threading.Event()

            # Create minimal valid audio file
            buf = io.BytesIO()
            with wave.open(buf, "wb") as w:
                w.setnchannels(1); w.setsampwidth(2); w.setframerate(16000)
                w.writeframes(b"\x00" * 3200)
            wav_bytes = buf.getvalue()

            upload_file = UploadFile(file=io.BytesIO(wav_bytes), filename="test.wav")

            # Mock whisper model so we don't load weights
            mock_model = MagicMock()
            voice_server.whisper_model = mock_model

            # Mock validate_audio_container_and_duration_sync to verify stop_event
            def mock_validate(path, ev):
                decode_started.set()
                # Wait until client disconnect sets the event
                for _ in range(50):
                    if ev and ev.is_set():
                        return -1.0
                    time.sleep(0.01)
                return 1.0

            with patch("scripts.voice_server.validate_audio_container_and_duration_sync", side_effect=mock_validate):
                async def mock_is_disconnected():
                    return decode_started.is_set()

                mock_request = MagicMock(spec=Request)
                mock_request.is_disconnected = mock_is_disconnected

                task = asyncio.create_task(
                    voice_server.transcribe_audio(mock_request, file=upload_file)
                )

                with self.assertRaises(HTTPException) as ctx:
                    await task

                self.assertEqual(ctx.exception.status_code, 499)

            # Wait for cleanup
            await asyncio.sleep(0.1)
            self.assertEqual(len(voice_server._active_stt_tasks), 0)
            self.assertEqual(voice_server.stt_semaphore._value, voice_server.STT_CONCURRENCY)

        asyncio.run(run_test())

    def test_asgi_task_cancellation_preserves_capacity_until_thread_completion(self):
        """
        Tests ASGI cancellation (asyncio.CancelledError injected into request handler)
        and verifies capacity and active worker tracking are held until worker completes.
        """
        async def run_test():
            worker_started = threading.Event()
            worker_can_finish = threading.Event()
            worker_finished = threading.Event()

            mock_voice = MagicMock()

            def slow_synthesize(text):
                worker_started.set()
                try:
                    chunk = MagicMock()
                    chunk.audio_int16_bytes = b"\x00" * 1600
                    yield chunk
                    worker_can_finish.wait(timeout=5.0)
                    yield chunk
                finally:
                    worker_finished.set()

            mock_voice.synthesize = slow_synthesize
            voice_server.piper_voice = mock_voice

            async def not_disconnected():
                return False

            req = MagicMock(spec=Request)
            req.is_disconnected = not_disconnected
            payload = voice_server.SpeechRequest(input="اختبار إلغاء ASGI")

            # Run synthesize_speech in a task
            task = asyncio.create_task(voice_server.synthesize_speech(req, payload))

            # Wait for worker to enter thread
            for _ in range(50):
                if worker_started.is_set():
                    break
                await asyncio.sleep(0.01)

            self.assertTrue(worker_started.is_set())

            # Cancel the ASGI task
            task.cancel()

            # Now allow worker to finish
            worker_can_finish.set()

            with self.assertRaises(asyncio.CancelledError):
                await task

            # Wait for worker cleanup
            for _ in range(50):
                if worker_finished.is_set() and len(voice_server._active_tts_tasks) == 0:
                    break
                await asyncio.sleep(0.01)

            self.assertTrue(worker_finished.is_set())
            self.assertEqual(len(voice_server._active_tts_tasks), 0)
            self.assertEqual(voice_server.tts_semaphore._value, voice_server.TTS_CONCURRENCY)

        asyncio.run(run_test())

    def test_repeated_in_flight_cancellations_with_clean_recovery(self):
        """
        Simulates 5 back-to-back in-flight cancellations, asserting active worker
        tracking and semaphore permits at each step, followed by a valid 200 OK request.
        """
        async def run_test():
            mock_voice = MagicMock()

            def cancellable_synthesize(text):
                for _ in range(3):
                    chunk = MagicMock()
                    chunk.audio_int16_bytes = b"\x00" * 800
                    time.sleep(0.01)
                    yield chunk

            mock_voice.synthesize = cancellable_synthesize
            voice_server.piper_voice = mock_voice

            for i in range(5):
                async def mock_disc():
                    return True

                req = MagicMock(spec=Request)
                req.is_disconnected = mock_disc
                payload = voice_server.SpeechRequest(input=f"طلب ملغى متكرر {i}")

                with self.assertRaises(HTTPException) as exc:
                    await voice_server.synthesize_speech(req, payload)
                self.assertEqual(exc.exception.status_code, 499)

                # Wait for worker to exit
                for _ in range(50):
                    if len(voice_server._active_tts_tasks) == 0:
                        break
                    await asyncio.sleep(0.01)

                self.assertEqual(len(voice_server._active_tts_tasks), 0)
                self.assertEqual(voice_server.tts_semaphore._value, voice_server.TTS_CONCURRENCY)

            # Subsequent normal request succeeds completely
            async def normal_disc():
                return False

            req = MagicMock(spec=Request)
            req.is_disconnected = normal_disc
            payload = voice_server.SpeechRequest(input="طلب سليم بعد سلسلة الإلغاءات")

            resp = await voice_server.synthesize_speech(req, payload)
            self.assertEqual(resp.status_code, 200)
            self.assertGreater(len(resp.body), 44)
            self.assertTrue(resp.body.startswith(b"RIFF"))
            self.assertEqual(len(voice_server._active_tts_tasks), 0)
            self.assertEqual(voice_server.tts_semaphore._value, voice_server.TTS_CONCURRENCY)

        asyncio.run(run_test())


if __name__ == "__main__":
    unittest.main()
