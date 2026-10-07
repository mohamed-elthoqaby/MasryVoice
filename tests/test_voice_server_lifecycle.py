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
        the stop_event halts decoding early, the temporary file is retained during execution
        and deleted upon completion, and capacity is safely released.
        """
        async def run_test():
            decode_started = threading.Event()
            stop_event = threading.Event()
            captured_tmp_path = None

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

            # Mock validate_audio_container_and_duration_sync to verify stop_event and temp file
            def mock_validate(path, ev):
                nonlocal captured_tmp_path
                captured_tmp_path = path
                decode_started.set()
                # Assert temp file exists while decoding is active
                self.assertTrue(os.path.exists(path), "Temporary file must exist during active decoding!")
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

            # Wait for background worker cleanup
            for _ in range(50):
                if len(voice_server._active_stt_tasks) == 0:
                    break
                await asyncio.sleep(0.01)

            self.assertEqual(len(voice_server._active_stt_tasks), 0)
            self.assertEqual(voice_server.stt_semaphore._value, voice_server.STT_CONCURRENCY)
            self.assertIsNotNone(captured_tmp_path)
            self.assertFalse(os.path.exists(captured_tmp_path), "Temporary file must be deleted after worker cleanup!")

        asyncio.run(run_test())

    def test_asgi_task_cancellation_preserves_capacity_until_thread_completion(self):
        """
        Tests ASGI cancellation (asyncio.CancelledError injected into request handler)
        and verifies capacity and active worker tracking are held while worker is blocked,
        and released cleanly only after worker completion.
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

            # Verification while worker is blocked during cancellation
            async def verify_and_release():
                await asyncio.sleep(0.05)
                # Worker thread is STILL BLOCKED because worker_can_finish is not yet set!
                self.assertFalse(worker_finished.is_set(), "Worker should still be running while blocked!")
                self.assertEqual(len(voice_server._active_tts_tasks), 1, "Active worker must remain tracked!")
                self.assertLess(voice_server.tts_semaphore._value, voice_server.TTS_CONCURRENCY, "Capacity permit must be held!")
                worker_can_finish.set()

            verifier = asyncio.create_task(verify_and_release())
            task.cancel()

            with self.assertRaises(asyncio.CancelledError):
                await task

            await verifier

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
        Simulates 5 back-to-back in-flight cancellations where the worker has genuinely started
        before disconnect is observed, asserting active worker tracking and permits at each step,
        followed by a valid 200 OK request.
        """
        async def run_test():
            for i in range(5):
                worker_started = threading.Event()
                worker_finished = threading.Event()

                mock_voice = MagicMock()

                def in_flight_synthesize(text):
                    worker_started.set()
                    try:
                        for _ in range(5):
                            chunk = MagicMock()
                            chunk.audio_int16_bytes = b"\x00" * 800
                            time.sleep(0.02)
                            yield chunk
                    finally:
                        worker_finished.set()

                mock_voice.synthesize = in_flight_synthesize
                voice_server.piper_voice = mock_voice

                async def mock_disc():
                    # Return True only after worker has demonstrably started
                    return worker_started.is_set()

                req = MagicMock(spec=Request)
                req.is_disconnected = mock_disc
                payload = voice_server.SpeechRequest(input=f"طلب ملغى متكرر أثناء العمل {i}")

                with self.assertRaises(HTTPException) as exc:
                    await voice_server.synthesize_speech(req, payload)
                self.assertEqual(exc.exception.status_code, 499)

                # Worker was started and running in flight
                self.assertTrue(worker_started.is_set(), f"Worker must have started for cancellation {i}")

                # Wait for worker to exit cleanly
                for _ in range(50):
                    if worker_finished.is_set() and len(voice_server._active_tts_tasks) == 0:
                        break
                    await asyncio.sleep(0.01)

                self.assertTrue(worker_finished.is_set())
                self.assertEqual(len(voice_server._active_tts_tasks), 0)
                self.assertEqual(voice_server.tts_semaphore._value, voice_server.TTS_CONCURRENCY)

            # Subsequent normal request succeeds completely
            normal_mock_voice = MagicMock()
            def fast_synthesize(text):
                chunk = MagicMock()
                chunk.audio_int16_bytes = b"\x00" * 1600
                yield chunk

            normal_mock_voice.synthesize = fast_synthesize
            voice_server.piper_voice = normal_mock_voice

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

    def test_worker_exception_after_http_deadline_is_observed_and_cleans_up(self):
        """
        Verifies that when a worker task raises an exception after the HTTP 5.0s wait deadline
        has already expired (and 499 was returned), the exception is observed by the task's
        done callback without unhandled errors, active worker count clears, and capacity restores.
        """
        async def run_test():
            worker_started = threading.Event()
            worker_can_fail = threading.Event()
            worker_finished = threading.Event()

            mock_voice = MagicMock()

            def failing_synthesize(text):
                worker_started.set()
                try:
                    chunk = MagicMock()
                    chunk.audio_int16_bytes = b"\x00" * 1600
                    yield chunk
                    # Block until deadline in HTTP handler expires
                    worker_can_fail.wait(timeout=10.0)
                    raise RuntimeError("Simulated worker error after HTTP deadline expiration")
                finally:
                    worker_finished.set()

            mock_voice.synthesize = failing_synthesize
            voice_server.piper_voice = mock_voice

            async def mock_disc():
                return worker_started.is_set()

            req = MagicMock(spec=Request)
            req.is_disconnected = mock_disc
            payload = voice_server.SpeechRequest(input="اختبار فشل العامل بعد مهلة HTTP")

            real_wait_for = asyncio.wait_for
            with patch("scripts.voice_server.asyncio.wait_for") as mock_wait_for:
                async def patched_wait_for(fut, timeout):
                    if timeout == 5.0:
                        await asyncio.sleep(0.05)
                        raise asyncio.TimeoutError()
                    return await real_wait_for(fut, timeout=timeout)
                mock_wait_for.side_effect = patched_wait_for

                task = asyncio.create_task(
                    voice_server.synthesize_speech(req, payload)
                )

                with self.assertRaises(HTTPException) as ctx:
                    await task

                self.assertEqual(ctx.exception.status_code, 499)

            # Handler exited, worker is still running
            self.assertFalse(worker_finished.is_set())
            self.assertEqual(len(voice_server._active_tts_tasks), 1)

            # Now let worker fail with exception
            worker_can_fail.set()

            for _ in range(50):
                if worker_finished.is_set() and len(voice_server._active_tts_tasks) == 0:
                    break
                await asyncio.sleep(0.02)

            self.assertTrue(worker_finished.is_set())
            self.assertEqual(len(voice_server._active_tts_tasks), 0)
            self.assertEqual(voice_server.tts_semaphore._value, voice_server.TTS_CONCURRENCY)

        asyncio.run(run_test())


    def test_uncancelled_tts_provider_failure_returns_500_not_499(self):
        """
        Verifies that when a TTS provider fails while the client is still connected,
        the route returns HTTP 500 (not 499 client disconnect), releases the semaphore permit,
        and unregisters the worker task.
        """
        async def run_test():
            mock_voice = MagicMock()
            def failing_synthesize(text):
                raise RuntimeError("Synthesizer model runtime failure")

            mock_voice.synthesize = failing_synthesize
            voice_server.piper_voice = mock_voice

            async def mock_disc():
                return False  # Client remains connected!

            req = MagicMock(spec=Request)
            req.is_disconnected = mock_disc
            payload = voice_server.SpeechRequest(input="اختبار فشل المزود والعميل متصل")

            with self.assertRaises(HTTPException) as ctx:
                await voice_server.synthesize_speech(req, payload)

            self.assertEqual(ctx.exception.status_code, 500)
            self.assertEqual(len(voice_server._active_tts_tasks), 0)
            self.assertEqual(voice_server.tts_semaphore._value, voice_server.TTS_CONCURRENCY)

        asyncio.run(run_test())

    def test_invalid_stt_container_returns_400_not_499(self):
        """
        Verifies that when an invalid/corrupt audio container is submitted and the client is connected,
        the route returns HTTP 400 (not 499), releases the permit, and cleans up temp files.
        """
        async def run_test():
            voice_server.whisper_model = MagicMock()

            corrupt_bytes = b"RIFF\x20\x00\x00\x00WAVE" + b"\xff" * 50
            upload_file = UploadFile(file=io.BytesIO(corrupt_bytes), filename="corrupt.wav")

            async def mock_disc():
                return False  # Client remains connected

            req = MagicMock(spec=Request)
            req.is_disconnected = mock_disc

            with self.assertRaises(HTTPException) as ctx:
                await voice_server.transcribe_audio(req, file=upload_file)

            self.assertEqual(ctx.exception.status_code, 400)
            self.assertEqual(len(voice_server._active_stt_tasks), 0)
            self.assertEqual(voice_server.stt_semaphore._value, voice_server.STT_CONCURRENCY)

        asyncio.run(run_test())

    def test_overlong_stt_audio_returns_400_not_499(self):
        """
        Verifies that audio exceeding 30s limit returns HTTP 400 with audio_duration_exceeded, not 499.
        """
        async def run_test():
            voice_server.whisper_model = MagicMock()

            # Create a 35s audio header
            sample_rate = 8000
            duration_sec = 35
            byte_rate = sample_rate * 2
            data_size = int(duration_sec * byte_rate)
            buf = bytearray()
            buf.extend(b"RIFF")
            buf.extend((36 + data_size).to_bytes(4, "little"))
            buf.extend(b"WAVEfmt ")
            buf.extend((16).to_bytes(4, "little"))
            buf.extend((1).to_bytes(2, "little"))
            buf.extend((1).to_bytes(2, "little"))
            buf.extend(sample_rate.to_bytes(4, "little"))
            buf.extend(byte_rate.to_bytes(4, "little"))
            buf.extend((2).to_bytes(2, "little"))
            buf.extend((16).to_bytes(2, "little"))
            buf.extend(b"data")
            buf.extend(data_size.to_bytes(4, "little"))
            buf.extend(bytes(data_size))
            overlong_bytes = bytes(buf)

            upload_file = UploadFile(file=io.BytesIO(overlong_bytes), filename="long.wav")

            async def mock_disc():
                return False

            req = MagicMock(spec=Request)
            req.is_disconnected = mock_disc

            with self.assertRaises(HTTPException) as ctx:
                await voice_server.transcribe_audio(req, file=upload_file)

            self.assertEqual(ctx.exception.status_code, 400)
            self.assertEqual(ctx.exception.detail.get("code"), "audio_duration_exceeded")
            self.assertEqual(len(voice_server._active_stt_tasks), 0)
            self.assertEqual(voice_server.stt_semaphore._value, voice_server.STT_CONCURRENCY)

        asyncio.run(run_test())

    def test_uncancelled_stt_provider_failure_returns_500_not_499(self):
        """
        Verifies that when STT model inference raises an exception while client is connected,
        the route returns HTTP 500 (not 499) and restores capacity.
        """
        async def run_test():
            buf = io.BytesIO()
            with wave.open(buf, "wb") as w:
                w.setnchannels(1); w.setsampwidth(2); w.setframerate(16000)
                w.writeframes(b"\x00" * 3200)
            wav_bytes = buf.getvalue()

            upload_file = UploadFile(file=io.BytesIO(wav_bytes), filename="valid.wav")

            mock_model = MagicMock()
            mock_model.transcribe.side_effect = RuntimeError("Whisper CTranslate2 memory fault")
            voice_server.whisper_model = mock_model

            async def mock_disc():
                return False

            req = MagicMock(spec=Request)
            req.is_disconnected = mock_disc

            with self.assertRaises(HTTPException) as ctx:
                await voice_server.transcribe_audio(req, file=upload_file)

            self.assertEqual(ctx.exception.status_code, 500)
            self.assertEqual(len(voice_server._active_stt_tasks), 0)
            self.assertEqual(voice_server.stt_semaphore._value, voice_server.STT_CONCURRENCY)

        asyncio.run(run_test())


if __name__ == "__main__":
    unittest.main()
