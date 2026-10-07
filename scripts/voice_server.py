"""
MasryVoice Real Local Speech Server ($0 Cost)
Provides local OpenAI-compatible endpoints for Whisper STT and Piper TTS.
100% Offline: Enforces local_files_only=True and pre-cached model paths.
Engine licenses:
- STT (faster-whisper): MIT
- TTS (piper-tts 1.8.0): GPL-3.0-or-later
- Voice dataset (ar_JO-kareem-low): Upstream dataset https://github.com/AliMokhammad/arabicttstrain/ (Model Card: License: See URL)
"""

import os
import sys
import io
import re
import av
import wave
import time
import glob
import asyncio
import logging
import tempfile
import threading
import hashlib
from typing import Optional
from contextlib import asynccontextmanager

from fastapi import FastAPI, UploadFile, File, Form, HTTPException, Response, Request
from pydantic import BaseModel

try:
    from faster_whisper import WhisperModel
except ImportError:
    WhisperModel = None

try:
    from piper.voice import PiperVoice
except ImportError:
    PiperVoice = None

logging.basicConfig(level=logging.INFO, format="%(asctime)s [%(levelname)s] %(message)s")
logger = logging.getLogger("VoiceServer")

# Base directory configuration
BASE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MODELS_DIR = os.path.join(BASE_DIR, "models")

# Whisper STT config
WHISPER_DEVICE = os.environ.get("WHISPER_DEVICE", "cpu")
WHISPER_COMPUTE = os.environ.get("WHISPER_COMPUTE_TYPE", "int8")
WHISPER_MODEL_NAME = os.environ.get("WHISPER_MODEL", "base")

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
_active_stt_tasks: set[asyncio.Task] = set()
_active_tts_tasks: set[asyncio.Task] = set()

whisper_model: Optional[WhisperModel] = None
piper_voice: Optional[PiperVoice] = None

# Model metadata and asset tracking
resolved_whisper_path: Optional[str] = None
whisper_snapshot_id: Optional[str] = None
whisper_bin_sha256: Optional[str] = None
piper_onnx_sha256: Optional[str] = None
piper_json_sha256: Optional[str] = None


def get_file_sha256(path: str) -> Optional[str]:
    """Computes SHA256 hash of a file for grounded asset provenance."""
    if not path or not os.path.exists(path):
        return None
    h = hashlib.sha256()
    with open(path, "rb") as f:
        while chunk := f.read(65536):
            h.update(chunk)
    return h.hexdigest()


def find_local_whisper_snapshot() -> Optional[str]:
    """
    Find strictly local Whisper model directory without remote fallback
    or silent wildcard substitution. Requires explicitly configured model snapshot.
    """
    explicit_path = os.environ.get("WHISPER_MODEL_PATH")
    if explicit_path and os.path.exists(explicit_path):
        return explicit_path

    # Check for explicitly configured model snapshot
    pattern = os.path.join(MODELS_DIR, "whisper", f"models--Systran--faster-whisper-{WHISPER_MODEL_NAME}", "snapshots", "*")
    matches = glob.glob(pattern)
    if matches and os.path.exists(matches[0]):
        return matches[0]

    return None


def normalize_spoken_arabic_digits(text: str) -> str:
    """Normalizes sequences of spoken Arabic number words into digits for downstream NLU/LLM."""
    if not text:
        return text
    word_to_digit = {
        'صفر': '0', 'سفر': '0', 'صف': '0', 'الصفر': '0', 'السفر': '0',
        'واحد': '1', 'واح': '1', 'الواحد': '1', 'صفواح': '01',
        'اثنان': '2', 'اثنين': '2', 'تنين': '2', 'يثنان': '2', 'دثنان': '2', 'ثنان': '2', 'الاثنين': '2', 'الاتنين': '2',
        'ثلاثة': '3', 'تلاتة': '3', 'ثلاث': '3', 'تلات': '3', 'الثلاثة': '3',
        'أربعة': '4', 'اربعة': '4', 'أربع': '4', 'اربع': '4', 'اربعا': '4', 'أربعا': '4', 'الاربعة': '4', 'الأربعة': '4',
        'خمسة': '5', 'كمسة': '5', 'كمس': '5', 'خمس': '5', 'كامس': '5', 'الخمسة': '5',
        'ستة': '6', 'ست': '6', 'سست': '6', 'الستة': '6',
        'سبعة': '7', 'سبع': '7', 'تسبع': '7', 'السبعة': '7',
        'ثمانية': '8', 'تمانية': '8', 'ثماني': '8', 'تماني': '8', 'الثمانية': '8',
        'تسعة': '9', 'تسع': '9', 'التسعة': '9'
    }
    tokens = re.split(r'(\s+)', text)
    result = []
    i = 0
    while i < len(tokens):
        tok = tokens[i].strip()
        clean_tok = re.sub(r'[^\w]', '', tok)
        if clean_tok in word_to_digit:
            digit_seq = []
            j = i
            while j < len(tokens):
                sub_tok = tokens[j].strip()
                if not sub_tok:
                    j += 1
                    continue
                clean_sub = re.sub(r'[^\w]', '', sub_tok)
                if clean_sub in word_to_digit:
                    digit_seq.append(word_to_digit[clean_sub])
                    j += 1
                else:
                    break
            if len(digit_seq) >= 3:
                result.append(''.join(digit_seq))
                i = j
                continue
        result.append(tokens[i])
        i += 1
    res_str = ''.join(result)
    res_str = re.sub(r'\b(بسم|بسمي)\b(?=\s+[أ-ي])', 'باسم', res_str)
    return res_str


@asynccontextmanager
async def lifespan(app: FastAPI):
    global whisper_model, piper_voice
    global resolved_whisper_path, whisper_snapshot_id, whisper_bin_sha256
    global piper_onnx_sha256, piper_json_sha256

    # 1. Load Whisper STT (strictly local, local_files_only=True)
    if WhisperModel is None:
        logger.warning("faster-whisper is not installed. STT disabled.")
        whisper_model = None
    else:
        resolved_whisper_path = find_local_whisper_snapshot()
        if resolved_whisper_path and os.path.exists(resolved_whisper_path):
            whisper_snapshot_id = os.path.basename(resolved_whisper_path)
            model_bin_file = os.path.join(resolved_whisper_path, "model.bin")
            whisper_bin_sha256 = get_file_sha256(model_bin_file)

            logger.info(f"Loading faster-whisper from explicit snapshot '{resolved_whisper_path}' (model={WHISPER_MODEL_NAME}, snapshot={whisper_snapshot_id}) on {WHISPER_DEVICE} ({WHISPER_COMPUTE})...")
            stt_start = time.perf_counter()
            try:
                whisper_model = WhisperModel(
                    resolved_whisper_path,
                    device=WHISPER_DEVICE,
                    compute_type=WHISPER_COMPUTE,
                    local_files_only=True
                )
                logger.info(f"faster-whisper loaded in {time.perf_counter() - stt_start:.2f}s")
            except Exception as e:
                logger.error(f"Failed to load local Whisper model: {e}", exc_info=True)
                whisper_model = None
        else:
            logger.warning(f"No local faster-whisper model found for '{WHISPER_MODEL_NAME}'. STT disabled (offline local_files_only=True enforced).")
            whisper_model = None

    # 2. Load Piper TTS (strictly local ONNX, GPL-3.0-or-later)
    if PiperVoice is None:
        logger.warning("piper-voice is not installed. TTS disabled.")
        piper_voice = None
    elif os.path.exists(TTS_MODEL_PATH) and os.path.exists(TTS_CONFIG_PATH):
        piper_onnx_sha256 = get_file_sha256(TTS_MODEL_PATH)
        piper_json_sha256 = get_file_sha256(TTS_CONFIG_PATH)

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

    active_stt = len(_active_stt_tasks)
    active_tts = len(_active_tts_tasks)

    return {
        "status": "healthy" if is_healthy else "degraded",
        "stt_engine": "faster-whisper",
        "stt_license": "MIT",
        "stt_ready": stt_ok,
        "stt_local_weights": stt_ok,
        "stt_model_configured": WHISPER_MODEL_NAME,
        "stt_snapshot_path": resolved_whisper_path,
        "stt_snapshot_hash": whisper_snapshot_id,
        "stt_model_bin_sha256": whisper_bin_sha256,
        "stt_active_workers": active_stt,
        "device": WHISPER_DEVICE,
        "tts_engine": "piper-tts",
        "tts_engine_license": "GPL-3.0-or-later",
        "tts_ready": tts_ok,
        "tts_model": DEFAULT_VOICE,
        "tts_model_path": TTS_MODEL_PATH,
        "tts_model_sha256": piper_onnx_sha256,
        "tts_config_sha256": piper_json_sha256,
        "tts_dataset_reference": "https://github.com/AliMokhammad/arabicttstrain/ (Model Card: License: See URL)",
        "tts_local_weights": tts_ok,
        "tts_active_workers": active_tts,
        "free_and_local": True
    }


def validate_audio_container_and_duration_sync(path: str, stop_event: Optional[threading.Event] = None) -> float:
    """Decodes bounded frames and samples via PyAV in worker thread to prevent blocking asyncio loop."""
    try:
        with av.open(path) as container:
            if not container.streams.audio:
                raise HTTPException(status_code=400, detail={"code": "audio_invalid_container", "message": "No audio stream found in container."})
            audio_stream = container.streams.audio[0]
            rate = audio_stream.rate or 16000
            max_allowed_samples = int(rate * MAX_AUDIO_DURATION_SEC)
            total_samples = 0

            for frame in container.decode(audio_stream):
                if stop_event and stop_event.is_set():
                    return -1.0
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
            return total_samples / rate
    except HTTPException:
        raise
    except Exception as e:
        raise HTTPException(status_code=400, detail={"code": "audio_corrupted", "message": f"Failed to decode audio frames: {e}"})


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
    worker_task = None
    stop_event = threading.Event()

    try:
        if await request.is_disconnected():
            raise HTTPException(status_code=499, detail="Client Closed Request")

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

        start_time = time.perf_counter()

        # Managed STT worker task owns permit release, temp file deletion, and active worker registration
        async def _run_stt_worker():
            cur_task = asyncio.current_task()
            if cur_task:
                _active_stt_tasks.add(cur_task)
            try:
                def _sync_work():
                    duration = validate_audio_container_and_duration_sync(tmp_path, stop_event)
                    if stop_event.is_set() or duration < 0:
                        return None, None, True

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

                try:
                    return await asyncio.to_thread(_sync_work)
                except Exception as ex:
                    logger.warning(f"[STT Worker] Thread error observed: {ex}")
                    return None, None, True
            finally:
                if cur_task:
                    _active_stt_tasks.discard(cur_task)
                stt_semaphore.release()
                if tmp_path and os.path.exists(tmp_path):
                    try:
                        os.remove(tmp_path)
                    except OSError:
                        pass

        def _observe_stt_task_exception(t: asyncio.Task):
            if not t.cancelled():
                exc = t.exception()
                if exc:
                    logger.warning(f"[STT] Background worker completed with exception: {exc}")

        worker_task = asyncio.create_task(_run_stt_worker())
        worker_task.add_done_callback(_observe_stt_task_exception)

        while not worker_task.done():
            if await request.is_disconnected():
                stop_event.set()
                logger.warning("[STT] Client disconnected during inference. Holding capacity until worker completion...")
                try:
                    await asyncio.wait_for(asyncio.shield(worker_task), timeout=5.0)
                except asyncio.TimeoutError:
                    logger.warning("[STT] Worker did not stop within bounded deadline after client disconnect; capacity remains held until thread completes.")
                except Exception as e:
                    logger.warning(f"[STT] Worker finished with exception: {e}")
                raise HTTPException(status_code=499, detail="Client Closed Request")
            await asyncio.sleep(0.02)

        transcribed_text, info, was_cancelled = await worker_task
        if was_cancelled:
            raise HTTPException(status_code=499, detail="Client Closed Request")

        latency = time.perf_counter() - start_time
        normalized_text = normalize_spoken_arabic_digits(transcribed_text)
        logger.info(f"[STT] Transcribed {len(content)} bytes in {latency:.2f}s (lang={info.language if info else 'ar'}): '{transcribed_text}' -> normalized: '{normalized_text}'")
        return {"text": normalized_text, "raw_text": transcribed_text, "language": info.language if info else "ar", "duration": info.duration if info else 0.0}

    except HTTPException:
        raise
    except asyncio.CancelledError:
        stop_event.set()
        logger.warning("[STT] Task cancelled via ASGI. Holding capacity until worker terminates...")
        if worker_task and not worker_task.done():
            try:
                await asyncio.wait_for(asyncio.shield(worker_task), timeout=5.0)
            except Exception:
                pass
        raise
    except Exception as e:
        logger.error(f"[STT] Transcription failed: {e}", exc_info=True)
        raise HTTPException(status_code=500, detail={"code": "stt_failed", "message": str(e)})
    finally:
        if worker_task is None:
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
    worker_task = None
    stop_event = threading.Event()

    try:
        if await request.is_disconnected():
            raise HTTPException(status_code=499, detail="Client Closed Request")

        # Managed TTS worker task owns permit release and active worker registration
        async def _run_tts_worker():
            cur_task = asyncio.current_task()
            if cur_task:
                _active_tts_tasks.add(cur_task)
            try:
                def _sync_work():
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

                try:
                    return await asyncio.to_thread(_sync_work)
                except Exception as ex:
                    logger.warning(f"[TTS Worker] Thread error observed: {ex}")
                    return None, True
            finally:
                if cur_task:
                    _active_tts_tasks.discard(cur_task)
                tts_semaphore.release()

        def _observe_tts_task_exception(t: asyncio.Task):
            if not t.cancelled():
                exc = t.exception()
                if exc:
                    logger.warning(f"[TTS] Background worker completed with exception: {exc}")

        worker_task = asyncio.create_task(_run_tts_worker())
        worker_task.add_done_callback(_observe_tts_task_exception)

        while not worker_task.done():
            if await request.is_disconnected():
                stop_event.set()
                logger.warning("[TTS] Client disconnected during synthesis. Holding capacity until worker completion...")
                try:
                    await asyncio.wait_for(asyncio.shield(worker_task), timeout=5.0)
                except asyncio.TimeoutError:
                    logger.warning("[TTS] Worker did not stop within bounded deadline after client disconnect; capacity remains held until thread completes.")
                except Exception as e:
                    logger.warning(f"[TTS] Worker finished with exception: {e}")
                raise HTTPException(status_code=499, detail="Client Closed Request")
            await asyncio.sleep(0.02)

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
    except asyncio.CancelledError:
        stop_event.set()
        logger.warning("[TTS] Task cancelled via ASGI. Holding capacity until worker terminates...")
        if worker_task and not worker_task.done():
            try:
                await asyncio.wait_for(asyncio.shield(worker_task), timeout=5.0)
            except Exception:
                pass
        raise
    except Exception as e:
        logger.error(f"[TTS] Synthesis failed: {e}", exc_info=True)
        raise HTTPException(status_code=500, detail={"code": "tts_failed", "message": str(e)})
    finally:
        if worker_task is None:
            tts_semaphore.release()


if __name__ == "__main__":
    import uvicorn
    if BASE_DIR not in sys.path:
        sys.path.insert(0, BASE_DIR)
    port = int(os.environ.get("VOICE_PORT", "8000"))
    uvicorn.run(app, host="127.0.0.1", port=port, log_level="info")
