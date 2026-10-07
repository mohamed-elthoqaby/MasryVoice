"""
Unit tests for voice_server.py lifecycle, capacity retention on cancellation,
and recovery under repeated interruption.
"""

import sys
import os
import io
import time
import asyncio
import threading
import unittest
from unittest.mock import MagicMock
from fastapi import HTTPException
from starlette.requests import Request

# Ensure root directory is in sys.path
BASE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if BASE_DIR not in sys.path:
    sys.path.insert(0, BASE_DIR)

from scripts import voice_server


class TestVoiceServerLifecycle(unittest.TestCase):

    def test_tts_capacity_retained_until_worker_actually_terminates(self):
        async def run_test():
            worker_started = threading.Event()
            worker_can_finish = threading.Event()
            worker_finished = threading.Event()

            mock_voice = MagicMock()

            def slow_synthesize(text):
                worker_started.set()
                try:
                    chunk1 = MagicMock()
                    chunk1.audio_int16_bytes = b"\x00" * 3200
                    yield chunk1
                    # Block worker until test signals it can finish
                    worker_can_finish.wait(timeout=5.0)
                    chunk2 = MagicMock()
                    chunk2.audio_int16_bytes = b"\x00" * 3200
                    yield chunk2
                finally:
                    worker_finished.set()

            mock_voice.synthesize = slow_synthesize
            voice_server.piper_voice = mock_voice

            async def mock_is_disconnected():
                return worker_started.is_set()

            mock_request = MagicMock(spec=Request)
            mock_request.is_disconnected = mock_is_disconnected

            req_payload = voice_server.SpeechRequest(input="مرحبا بكم في عيادة النور")

            # Start synthesis task
            synthesis_task = asyncio.create_task(
                voice_server.synthesize_speech(mock_request, req_payload)
            )

            # Wait until worker has started and client disconnect is detected
            await asyncio.sleep(0.1)
            self.assertTrue(worker_started.is_set(), "Worker should have started")

            # While worker is blocked, capacity MUST BE OCCUPIED
            self.assertLess(
                voice_server.tts_semaphore._value,
                voice_server.TTS_CONCURRENCY,
                "Capacity must remain occupied while worker is still running after client disconnect!"
            )

            # Now allow worker to finish
            worker_can_finish.set()

            # The synthesis task should raise HTTPException(499)
            with self.assertRaises(HTTPException) as ctx:
                await synthesis_task

            self.assertEqual(ctx.exception.status_code, 499)
            self.assertTrue(worker_finished.is_set(), "Worker should have finished before handler returned")

            # Now capacity MUST be fully released and available again
            self.assertEqual(
                voice_server.tts_semaphore._value,
                voice_server.TTS_CONCURRENCY,
                "Capacity must be released only after worker termination!"
            )

        asyncio.run(run_test())

    def test_repeated_cancellations_and_subsequent_recovery(self):
        async def run_test():
            mock_voice = MagicMock()

            def quick_cancellable_synthesize(text):
                for _ in range(5):
                    chunk = MagicMock()
                    chunk.audio_int16_bytes = b"\x00" * 1600
                    time.sleep(0.01)
                    yield chunk

            mock_voice.synthesize = quick_cancellable_synthesize
            voice_server.piper_voice = mock_voice

            # Run 3 cancellations in sequence
            for i in range(3):
                async def mock_disc():
                    return True

                req = MagicMock(spec=Request)
                req.is_disconnected = mock_disc
                payload = voice_server.SpeechRequest(input=f"طلب ملغى رقم {i}")

                with self.assertRaises(HTTPException) as exc:
                    await voice_server.synthesize_speech(req, payload)
                self.assertEqual(exc.exception.status_code, 499)

            # Assert all permits were returned
            self.assertEqual(voice_server.tts_semaphore._value, voice_server.TTS_CONCURRENCY)

            # Subsequent normal request succeeds completely
            async def normal_disc():
                return False

            req = MagicMock(spec=Request)
            req.is_disconnected = normal_disc
            payload = voice_server.SpeechRequest(input="طلب طبيعي بعد الاستعادة")

            resp = await voice_server.synthesize_speech(req, payload)
            self.assertEqual(resp.status_code, 200)
            self.assertGreater(len(resp.body), 44)
            self.assertTrue(resp.body.startswith(b"RIFF"))
            self.assertEqual(voice_server.tts_semaphore._value, voice_server.TTS_CONCURRENCY)

        asyncio.run(run_test())


    def test_stt_capacity_retained_and_temp_file_preserved_until_worker_terminates(self):
        async def run_test():
            worker_started = threading.Event()
            worker_can_finish = threading.Event()
            worker_finished = threading.Event()

            mock_model = MagicMock()

            def slow_transcribe(path, **kwargs):
                worker_started.set()
                # Verify temp file exists while worker is executing
                self.assertTrue(os.path.exists(path), "Temp audio file must exist while worker runs")
                try:
                    seg = MagicMock()
                    seg.text = "مرحبا"
                    yield seg
                    worker_can_finish.wait(timeout=5.0)
                    seg2 = MagicMock()
                    seg2.text = "بك"
                    yield seg2
                finally:
                    worker_finished.set()

            info_mock = MagicMock()
            info_mock.language = "ar"
            info_mock.duration = 2.0
            mock_model.transcribe.side_effect = lambda path, **kwargs: (slow_transcribe(path, **kwargs), info_mock)
            voice_server.whisper_model = mock_model

            async def mock_is_disconnected():
                return worker_started.is_set()

            mock_request = MagicMock(spec=Request)
            mock_request.is_disconnected = mock_is_disconnected

            # Real valid minimal WAV bytes
            from fastapi import UploadFile
            import io
            buf = io.BytesIO()
            import wave
            with wave.open(buf, "wb") as w:
                w.setnchannels(1); w.setsampwidth(2); w.setframerate(16000)
                w.writeframes(b"\x00" * 3200)
            wav_bytes = buf.getvalue()

            upload_file = UploadFile(file=io.BytesIO(wav_bytes), filename="test.wav")

            transcribe_task = asyncio.create_task(
                voice_server.transcribe_audio(mock_request, file=upload_file)
            )

            await asyncio.sleep(0.1)
            self.assertTrue(worker_started.is_set(), "STT worker should have started")

            # Capacity MUST BE OCCUPIED while worker runs
            self.assertLess(
                voice_server.stt_semaphore._value,
                voice_server.STT_CONCURRENCY,
                "STT Capacity must remain occupied after client disconnect while worker runs!"
            )

            # Signal worker to finish
            worker_can_finish.set()

            with self.assertRaises(HTTPException) as ctx:
                await transcribe_task

            self.assertEqual(ctx.exception.status_code, 499)
            self.assertTrue(worker_finished.is_set(), "STT worker must finish before handler returns")

            # Capacity MUST BE RELEASED
            self.assertEqual(
                voice_server.stt_semaphore._value,
                voice_server.STT_CONCURRENCY,
                "STT Capacity must be released after worker terminates!"
            )

        asyncio.run(run_test())


if __name__ == "__main__":
    unittest.main()
