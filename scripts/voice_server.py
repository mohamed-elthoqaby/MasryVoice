"""
MasryVoice 100% Local Free Speech Server
Provides OpenAI-compatible endpoints for:
1. Whisper Speech-To-Text: POST /v1/audio/transcriptions
2. Local Arabic Speech Synthesis (Piper ONNX): POST /v1/audio/speech

Runs 100% locally on CPU without requiring GPU infrastructure, external networks, or paid APIs ($0 Cost).
Licenses:
- faster-whisper: MIT License (SYSTRAN)
- piper-tts 1.8.0 engine: GPL-3.0-or-later (Rhasspy / Michael Hansen)
- ar_JO-kareem-low voice weights: Open Data / CC-BY (Ali Mokhammad / arabicttstrain)
"""

import os
import sys
import io
import time
import glob
import wave
import asyncio
import threading
import tempfile
import logging
from typing import Optional
from contextlib import asynccontextmanager

import av
from fastapi import FastAPI, UploadFile, File, Form, HTTPException, Request, Response
from fastapi.responses import JSONResponse, Response
from pydantic import BaseModel
from faster_whisper import WhisperModel
from piper.voice import PiperVoice

logging.basicConfig(level=logging.INFO, format="%(asctime)s [%(levelname)s] %(message)s")
logger = logging.getLogger("VoiceServer")

# Base directory configuration
BASE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MODELS_DIR = os.path.join(BASE_DIR, "models")

# Whisper STT config
WHISPER_DEVICE = os.environ.get("WHISPER_DEVICE", "cpu")
WHISPER_COMPUTE = os.environ.get("WHISPER_COMPUTE_TYPE", "int8")
WHISPER_MODEL_NAME = os.environ.get("WHISPER_MODEL", "tiny")

# Piper TTS config (GPL-3.0-or-later engine, ar_JO-kareem-low voice)
DEFAULT_PIPER_ONNX = os.path.join(MODELS_DIR, "piper", "ar_JO-kareem-low", "ar_JO-kareem-low.onnx")
DEFAULT_PIPER_JSON = os.path.join(MODELS_DIR, "piper", "ar_JO-kareem-low", "ar_JO-kareem-low.onnx.json")

TTS_MODEL_PATH = os.environ.get("TTS_MODEL_PATH", DEFAULT_PIPER_ONNX)
TTS_CONFIG_PATH = os.environ.get("TTS_CONFIG_PATH", DEFAULT_PIPER_JSON)
DEFAULT_VOICE = os.environ.get("TTS_VOICE", "ar_JO-kareem-low")

# Hard bounds
MAX_AUDIO_BYTES = 10 * 1024 * 1024  # 10 MB strict limit
MAX_AUDIO_DURATION_SEC = 30.5       # 30s limit (+0.5s tolerance)
MAX_TEXT_CHARS = 2000

# Concurrency limits for laptop CPU budget
STT_CONCURRENCY = int(os.environ.get("STT_CONCURRENCY", "1"))
TTS_CONCURRENCY = int(os.environ.get("TTS_CONCURRENCY", "2"))
QUEUE_TIMEOUT_SEC = float(os.environ.get("VOICE_QUEUE_TIMEOUT", "10.0"))

stt_semaphore = asyncio.Semaphore(STT_CONCURRENCY)
tts_semaphore = asyncio.Semaphore(TTS_CONCURRENCY)

whisper_model: Optional[WhisperModel] = None
piper_voice: Optional[PiperVoice] = None


def find_local_whisper_snapshot() -> Optional[str]:
    """Find strictly local Whisper model directory without remote fallback."""
    explicit_path = os.environ.get("WHISPER_MODEL_PATH")
    if explicit_path and os.path.exists(explicit_path):
        return explicit_path

    # Check for specific model snapshot first
    pattern = os.path.join(MODELS_DIR, "whisper", f"models--Systran--faster-whisper-{WHISPER_MODEL_NAME}", "snapshots", "*")
    matches = glob.glob(pattern)
    if matches and os.path.exists(matches[0]):
        return matches[0]

    # Check for any faster-whisper snapshot
    any_pattern = os.path.join(MODELS_DIR, "whisper", "models--Systran--faster-whisper-*", "snapshots", "*")
    any_matches = glob.glob(any_pattern)
    if any_matches and os.path.exists(any_matches[0]):
        return any_matches[0]

    return None


@asynccontextmanager
async def lifespan(app: FastAPI):
    global whisper_model, piper_voice

    # 1. Load Whisper STT (strictly local, local_files_only=True)
    local_whisper_path = find_local_whisper_snapshot()
    if local_whisper_path and os.path.exists(local_whisper_path):
        logger.info(f"Loading faster-whisper from local snapshot '{local_whisper_path}' on {WHISPER_DEVICE} ({WHISPER_COMPUTE})...")
        stt_start = time.perf_counter()
        try:
            whisper_model = WhisperModel(
                local_whisper_path,
                device=WHISPER_DEVICE,
                compute_type=WHISPER_COMPUTE,
                local_files_only=True
            )
            logger.info(f"faster-whisper loaded in {time.perf_counter() - stt_start:.2f}s")
        except Exception as e:
            logger.error(f"Failed to load local Whisper model: {e}", exc_info=True)
            whisper_model = None
    else:
        logger.warning("No local faster-whisper model found. STT disabled (offline local_files_only=True enforced).")
        whisper_model = None

    # 2. Load Piper TTS (strictly local ONNX, GPL-3.0-or-later)
    if os.path.exists(TTS_MODEL_PATH) and os.path.exists(TTS_CONFIG_PATH):
        logger.info(f"Loading Piper TTS from '{TTS_MODEL_PATH}'...")
        tts_start = time.perf_counter()
        try:
            piper_voice = PiperVoice.load(TTS_MODEL_PATH, config_path=TTS_CONFIG_PATH)
            logger.info(f"Piper TTS loaded in {time.perf_counter() - tts_start:.2f}s (voice={DEFAULT_VOICE})")
        except Exception as e:
            logger.error(f"Failed to load Piper TTS model: {e}", exc_info=True)
            piper_voice = None
    else:
        logger.warning(f"Piper weights not found at {TTS_MODEL_PATH}. TTS disabled.")
        piper_voice = None

    yield
    logger.info("Shutting down MasryVoice Speech Server...")


app = FastAPI(title="MasryVoice Speech Server (100% Local)", lifespan=lifespan)


class SpeechRequest(BaseModel):
    input: str
    voice: Optional[str] = DEFAULT_VOICE
    model: Optional[str] = "piper-tts"
    response_format: Optional[str] = "wav"
    speed: Optional[float] = 1.0


@app.get("/health")
@app.get("/")
async def health(response: Response):
    stt_ok = whisper_model is not None
    tts_ok = piper_voice is not None
    is_healthy = stt_ok and tts_ok
    if not is_healthy:
        response.status_code = 503

    return {
        "status": "healthy" if is_healthy else "degraded",
        "stt_engine": "faster-whisper",
        "stt_license": "MIT",
        "stt_ready": stt_ok,
        "stt_local_weights": stt_ok,
        "device": WHISPER_DEVICE,
        "tts_engine": "piper-tts",
        "tts_engine_license": "GPL-3.0-or-later",
        "tts_voice_dataset_license": "CC-BY (AliMokhammad/arabicttstrain)",
        "tts_ready": tts_ok,
        "tts_model": DEFAULT_VOICE,
        "tts_local_weights": tts_ok,
        "free_and_local": True
    }


@app.post("/v1/audio/transcriptions")
async def transcribe_audio(
    request: Request,
    file: UploadFile = File(...),
    model: str = Form("whisper-1"),
    language: Optional[str] = Form("ar"),
    prompt: Optional[str] = Form(None)
):
    if not whisper_model:
        raise HTTPException(status_code=503, detail={"code": "stt_unavailable", "message": "Whisper STT model not loaded or offline"})

    # Check client disconnect before doing any work
    if await request.is_disconnected():
        raise HTTPException(status_code=499, detail="Client Closed Request")

    # 1. Bounded upload reading: read in 64KB chunks up to MAX_AUDIO_BYTES
    content = bytearray()
    while chunk := await file.read(64 * 1024):
        content.extend(chunk)
        if len(content) > MAX_AUDIO_BYTES:
            raise HTTPException(
                status_code=413,
                detail={"code": "audio_size_exceeded", "message": f"Audio file exceeds maximum size of {MAX_AUDIO_BYTES} bytes"}
            )

    if len(content) < 12:
        raise HTTPException(status_code=400, detail={"code": "audio_truncated", "message": "Audio file is truncated or corrupted."})

    # Acquire STT admission permit with timeout
    try:
        await asyncio.wait_for(stt_semaphore.acquire(), timeout=QUEUE_TIMEOUT_SEC)
    except asyncio.TimeoutError:
        raise HTTPException(
            status_code=429,
            headers={"Retry-After": "5"},
            detail={"code": "stt_overload", "message": "STT capacity limit exceeded. Please retry shortly."}
        )

    tmp_path = None
    try:
        if await request.is_disconnected():
            raise HTTPException(status_code=499, detail="Client Closed Request")

        # Determine extension based on filename or content-type
        ext = ".wav"
        if file.filename:
            _, file_ext = os.path.splitext(file.filename)
            if file_ext:
                ext = file_ext.lower()
        elif file.content_type:
            if "webm" in file.content_type:
                ext = ".webm"
            elif "ogg" in file.content_type:
                ext = ".ogg"

        with tempfile.NamedTemporaryFile(suffix=ext, delete=False) as tmp:
            tmp.write(content)
            tmp_path = tmp.name

        # 2. Strict audio container decoding and bounded sample validation via PyAV
        try:
            with av.open(tmp_path) as container:
                if not container.streams.audio:
                    raise HTTPException(status_code=400, detail={"code": "audio_invalid_container", "message": "No audio stream found in container."})
                audio_stream = container.streams.audio[0]
                rate = audio_stream.rate or 16000
                max_allowed_samples = int(rate * MAX_AUDIO_DURATION_SEC)
                total_samples = 0

                for frame in container.decode(audio_stream):
                    total_samples += frame.samples
                    if total_samples > max_allowed_samples:
                        duration_sec = total_samples / rate
                        raise HTTPException(
                            status_code=400,
                            detail={
                                "code": "audio_duration_exceeded",
                                "message": f"Audio duration ({duration_sec:.1f}s) exceeds maximum allowed limit of 30 seconds."
                            }
                        )
                if total_samples == 0:
                    raise HTTPException(status_code=400, detail={"code": "audio_empty_frames", "message": "Audio stream contains 0 decodable frames."})
        except HTTPException:
            raise
        except Exception as e:
            raise HTTPException(status_code=400, detail={"code": "audio_corrupted", "message": f"Failed to decode audio frames: {e}"})

        start_time = time.perf_counter()

        # 3. Completely offload model forward pass and segment iteration to a worker thread
        # This prevents lazy CTranslate2 generator iteration from blocking the asyncio event loop!
        stop_event = threading.Event()

        def run_stt_worker():
            segments_iter, info = whisper_model.transcribe(
                tmp_path,
                language=language if language and language != "auto" else "ar",
                initial_prompt=prompt,
                beam_size=5,
                vad_filter=True,
                vad_parameters=dict(min_silence_duration_ms=500)
            )
            text_chunks = []
            for segment in segments_iter:
                if stop_event.is_set():
                    return None, info, True
                text_chunks.append(segment.text.strip())
            return " ".join(text_chunks).strip(), info, False

        worker_task = asyncio.create_task(asyncio.to_thread(run_stt_worker))

        # Monitor client disconnect and polling while worker thread executes
        while not worker_task.done():
            if await request.is_disconnected():
                stop_event.set()
                logger.warning("[STT] Client disconnected during inference. Stop flag signaled to worker.")
                raise HTTPException(status_code=499, detail="Client Closed Request")
            await asyncio.sleep(0.05)

        transcribed_text, info, was_cancelled = await worker_task
        if was_cancelled:
            raise HTTPException(status_code=499, detail="Client Closed Request")

        latency = time.perf_counter() - start_time
        logger.info(f"[STT] Transcribed {len(content)} bytes in {latency:.2f}s (lang={info.language}): '{transcribed_text}'")
        return {"text": transcribed_text, "language": info.language, "duration": info.duration}

    except HTTPException:
        raise
    except Exception as e:
        logger.error(f"[STT] Transcription failed: {e}", exc_info=True)
        raise HTTPException(status_code=500, detail={"code": "stt_failed", "message": str(e)})
    finally:
        stt_semaphore.release()
        if tmp_path and os.path.exists(tmp_path):
            try:
                os.remove(tmp_path)
            except OSError:
                pass


@app.post("/v1/audio/speech")
async def synthesize_speech(request: Request, req: SpeechRequest):
    if not piper_voice:
        raise HTTPException(status_code=503, detail={"code": "tts_unavailable", "message": "Piper TTS voice not loaded or offline"})

    text = req.input.strip()
    if not text:
        raise HTTPException(status_code=400, detail={"code": "text_empty", "message": "Text cannot be empty"})
    if len(text) > MAX_TEXT_CHARS:
        raise HTTPException(
            status_code=400,
            detail={"code": "text_too_long", "message": f"Text length ({len(text)}) exceeds maximum limit of {MAX_TEXT_CHARS} characters."}
        )

    if await request.is_disconnected():
        raise HTTPException(status_code=499, detail="Client Closed Request")

    # Acquire TTS admission permit with timeout
    try:
        await asyncio.wait_for(tts_semaphore.acquire(), timeout=QUEUE_TIMEOUT_SEC)
    except asyncio.TimeoutError:
        raise HTTPException(
            status_code=429,
            headers={"Retry-After": "5"},
            detail={"code": "tts_overload", "message": "TTS capacity limit exceeded. Please retry shortly."}
        )

    start_time = time.perf_counter()
    try:
        if await request.is_disconnected():
            raise HTTPException(status_code=499, detail="Client Closed Request")

        # 4. Offload Piper ONNX synthesis and chunk iteration to worker thread
        # This prevents ONNX generator iteration from blocking the asyncio event loop!
        stop_event = threading.Event()

        def run_tts_worker():
            buffer = io.BytesIO()
            with wave.open(buffer, "wb") as wav_file:
                wav_file.setnchannels(1)
                wav_file.setsampwidth(2)
                wav_file.setframerate(16000)
                for chunk in piper_voice.synthesize(text):
                    if stop_event.is_set():
                        return None, True
                    wav_file.writeframes(chunk.audio_int16_bytes)
            return buffer.getvalue(), False

        worker_task = asyncio.create_task(asyncio.to_thread(run_tts_worker))

        while not worker_task.done():
            if await request.is_disconnected():
                stop_event.set()
                logger.warning("[TTS] Client disconnected during synthesis. Stop flag signaled to worker.")
                raise HTTPException(status_code=499, detail="Client Closed Request")
            await asyncio.sleep(0.05)

        wav_bytes, was_cancelled = await worker_task
        if was_cancelled:
            raise HTTPException(status_code=499, detail="Client Closed Request")

        if not wav_bytes or len(wav_bytes) <= 44:
            raise HTTPException(status_code=500, detail={"code": "tts_empty", "message": "TTS generated empty audio"})

        latency = time.perf_counter() - start_time
        logger.info(f"[TTS] Synthesized {len(text)} chars to 16kHz WAV in {latency:.2f}s (size={len(wav_bytes)} bytes)")
        return Response(content=wav_bytes, media_type="audio/wav")

    except HTTPException:
        raise
    except Exception as e:
        logger.error(f"[TTS] Synthesis failed: {e}", exc_info=True)
        raise HTTPException(status_code=500, detail={"code": "tts_failed", "message": str(e)})
    finally:
        tts_semaphore.release()


if __name__ == "__main__":
    import uvicorn
    port = int(os.environ.get("VOICE_PORT", "8000"))
    uvicorn.run("scripts.voice_server:app", host="127.0.0.1", port=port, log_level="info")
