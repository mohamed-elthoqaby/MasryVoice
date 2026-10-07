"""
MasryVoice 100% Local Free Speech Server
Provides OpenAI-compatible endpoints for:
1. Whisper Speech-To-Text: POST /v1/audio/transcriptions
2. Local Arabic Speech Synthesis (Piper ONNX): POST /v1/audio/speech

Runs 100% locally on CPU without requiring GPU infrastructure, external networks, or paid APIs.
Uses faster-whisper (CTranslate2 int8) and Piper TTS (ONNX / 16kHz PCM WAV).
"""

import os
import sys
import io
import time
import glob
import wave
import asyncio
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

# Configuration
BASE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MODELS_DIR = os.path.join(BASE_DIR, "models")

# Whisper STT config
WHISPER_DEVICE = os.environ.get("WHISPER_DEVICE", "cpu")
WHISPER_COMPUTE = os.environ.get("WHISPER_COMPUTE_TYPE", "int8")

# Piper TTS config
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
    pattern = os.path.join(MODELS_DIR, "whisper", "models--Systran--faster-whisper-*", "snapshots", "*")
    matches = glob.glob(pattern)
    return matches[0] if matches else None


@asynccontextmanager
async def lifespan(app: FastAPI):
    global whisper_model, piper_voice
    
    # 1. Load Whisper STT (strictly local)
    local_whisper_path = find_local_whisper_snapshot()
    whisper_source = local_whisper_path if local_whisper_path and os.path.exists(local_whisper_path) else "tiny"
    logger.info(f"Loading faster-whisper from '{whisper_source}' on {WHISPER_DEVICE} ({WHISPER_COMPUTE})...")
    stt_start = time.perf_counter()
    whisper_model = WhisperModel(whisper_source, device=WHISPER_DEVICE, compute_type=WHISPER_COMPUTE, local_files_only=bool(local_whisper_path))
    logger.info(f"faster-whisper loaded in {time.perf_counter() - stt_start:.2f}s")

    # 2. Load Piper TTS (strictly local ONNX)
    logger.info(f"Loading Piper TTS from '{TTS_MODEL_PATH}'...")
    tts_start = time.perf_counter()
    if os.path.exists(TTS_MODEL_PATH) and os.path.exists(TTS_CONFIG_PATH):
        piper_voice = PiperVoice.load(TTS_MODEL_PATH, config_path=TTS_CONFIG_PATH)
        logger.info(f"Piper TTS loaded in {time.perf_counter() - tts_start:.2f}s (voice={DEFAULT_VOICE})")
    else:
        logger.warning(f"Piper weights not found at {TTS_MODEL_PATH}. TTS will be unavailable until downloaded.")

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
async def health():
    return {
        "status": "healthy",
        "stt_engine": "faster-whisper",
        "stt_local_weights": bool(find_local_whisper_snapshot()),
        "device": WHISPER_DEVICE,
        "tts_engine": "piper-tts",
        "tts_model": DEFAULT_VOICE,
        "tts_local_weights": bool(piper_voice is not None),
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
        raise HTTPException(status_code=503, detail={"code": "stt_unavailable", "message": "Whisper model not initialized"})

    content = await file.read()
    if not content or len(content) == 0:
        raise HTTPException(status_code=400, detail={"code": "audio_empty", "message": "Audio file cannot be empty"})
    if len(content) > MAX_AUDIO_BYTES:
        raise HTTPException(
            status_code=413,
            detail={"code": "audio_size_exceeded", "message": f"Audio file ({len(content)} bytes) exceeds maximum size of {MAX_AUDIO_BYTES} bytes"}
        )

    # Check for client disconnect before waiting for permit
    if await request.is_disconnected():
        raise HTTPException(status_code=499, detail="Client Closed Request")

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
        # Check disconnect again after permit acquisition
        if await request.is_disconnected():
            raise HTTPException(status_code=499, detail="Client Closed Request")

        # Determine extension based on content-type or filename
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

        # Inspect duration server-side with PyAV
        try:
            with av.open(tmp_path) as container:
                if container.duration is not None:
                    duration_sec = float(container.duration) / av.time_base
                    if duration_sec > MAX_AUDIO_DURATION_SEC:
                        raise HTTPException(
                            status_code=400,
                            detail={
                                "code": "audio_duration_exceeded",
                                "message": f"Audio duration ({duration_sec:.1f}s) exceeds maximum allowed limit of 30 seconds."
                            }
                        )
        except HTTPException:
            raise
        except Exception as e:
            # If PyAV cannot inspect container, verify minimal headers
            if len(content) < 12:
                raise HTTPException(status_code=400, detail={"code": "audio_truncated", "message": "Audio file is truncated or corrupted."})

        start_time = time.perf_counter()

        def transcribe_generator():
            return whisper_model.transcribe(
                tmp_path,
                language=language if language and language != "auto" else "ar",
                initial_prompt=prompt,
                beam_size=5,
                vad_filter=True,
                vad_parameters=dict(min_silence_duration_ms=500)
            )

        segments_iter, info = await asyncio.to_thread(transcribe_generator)

        # Iterate segments with cooperative disconnect check
        text_chunks = []
        for segment in segments_iter:
            if await request.is_disconnected():
                logger.warning("[STT] Client disconnected during segment transcription. Aborting processing.")
                raise HTTPException(status_code=499, detail="Client Closed Request")
            text_chunks.append(segment.text.strip())

        transcribed_text = " ".join(text_chunks).strip()
        latency = time.perf_counter() - start_time
        logger.info(f"[STT] Transcribed {len(content)} bytes in {latency:.2f}s (lang={info.language}, prob={info.language_probability:.2f}): '{transcribed_text}'")
        return {"text": transcribed_text, "language": info.language, "duration": info.duration}

    except HTTPException:
        raise
    except Exception as e:
        logger.error(f"[STT] Transcription error: {e}", exc_info=True)
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
        raise HTTPException(status_code=503, detail={"code": "tts_unavailable", "message": "Piper TTS voice not loaded"})

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

        # Synthesize via local Piper ONNX model into 16kHz Mono 16-bit PCM WAV
        buffer = io.BytesIO()
        with wave.open(buffer, "wb") as wav_file:
            wav_file.setnchannels(1)
            wav_file.setsampwidth(2)
            wav_file.setframerate(16000)

            # Generate chunks with cooperative cancellation check
            for chunk in piper_voice.synthesize(text):
                if await request.is_disconnected():
                    logger.warning("[TTS] Client disconnected during chunk synthesis. Aborting processing.")
                    raise HTTPException(status_code=499, detail="Client Closed Request")
                wav_file.writeframes(chunk.audio_int16_bytes)

        wav_bytes = buffer.getvalue()
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
