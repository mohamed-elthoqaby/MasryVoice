"""
MasryVoice Local Free Speech Server
Provides OpenAI-compatible endpoints for:
1. Whisper Speech-To-Text: POST /v1/audio/transcriptions
2. Spoken Egyptian Arabic Text-To-Speech: POST /v1/audio/speech

Runs 100% locally on CPU without requiring GPU infrastructure or paid APIs.
Uses faster-whisper (CTranslate2 int8) and edge-tts / soundfile.
"""

import os
import sys
import io
import time
import asyncio
import tempfile
import logging
from typing import Optional
from contextlib import asynccontextmanager

import av
import edge_tts
from fastapi import FastAPI, UploadFile, File, Form, HTTPException, Request, Response
from fastapi.responses import JSONResponse, Response
from pydantic import BaseModel
from faster_whisper import WhisperModel

logging.basicConfig(level=logging.INFO, format="%(asctime)s [%(levelname)s] %(message)s")
logger = logging.getLogger("VoiceServer")

# Configuration
WHISPER_MODEL_NAME = os.environ.get("WHISPER_MODEL", "tiny")  # tiny, base, small
DEVICE = os.environ.get("WHISPER_DEVICE", "cpu")
COMPUTE_TYPE = os.environ.get("WHISPER_COMPUTE_TYPE", "int8")
DEFAULT_VOICE = os.environ.get("TTS_VOICE", "ar-EG-SalmaNeural")  # ar-EG-SalmaNeural, ar-EG-ShakirNeural
MAX_AUDIO_BYTES = 25 * 1024 * 1024  # 25 MB max

# Concurrency limits for laptop CPU budget
STT_CONCURRENCY = int(os.environ.get("STT_CONCURRENCY", "1"))
TTS_CONCURRENCY = int(os.environ.get("TTS_CONCURRENCY", "2"))

stt_semaphore = asyncio.Semaphore(STT_CONCURRENCY)
tts_semaphore = asyncio.Semaphore(TTS_CONCURRENCY)

whisper_model: Optional[WhisperModel] = None

@asynccontextmanager
async def lifespan(app: FastAPI):
    global whisper_model
    logger.info(f"Loading faster-whisper model '{WHISPER_MODEL_NAME}' on {DEVICE} ({COMPUTE_TYPE})...")
    start = time.perf_counter()
    whisper_model = WhisperModel(WHISPER_MODEL_NAME, device=DEVICE, compute_type=COMPUTE_TYPE)
    logger.info(f"faster-whisper model loaded in {time.perf_counter() - start:.2f}s")
    yield
    logger.info("Shutting down Voice Server...")

app = FastAPI(title="MasryVoice Speech Server", lifespan=lifespan)

class SpeechRequest(BaseModel):
    input: str
    voice: Optional[str] = DEFAULT_VOICE
    model: Optional[str] = "tts-1"
    response_format: Optional[str] = "wav"  # wav, mp3
    speed: Optional[float] = 1.0


@app.get("/health")
@app.get("/")
async def health():
    return {
        "status": "healthy",
        "stt_engine": "faster-whisper",
        "stt_model": WHISPER_MODEL_NAME,
        "device": DEVICE,
        "tts_engine": "edge-tts",
        "default_voice": DEFAULT_VOICE,
        "free_and_local": True
    }


@app.post("/v1/audio/transcriptions")
async def transcribe_audio(
    file: UploadFile = File(...),
    model: str = Form("whisper-1"),
    language: Optional[str] = Form("ar"),
    prompt: Optional[str] = Form(None)
):
    if not whisper_model:
        raise HTTPException(status_code=503, detail="Whisper model not initialized")

    content = await file.read()
    if not content or len(content) == 0:
        raise HTTPException(status_code=400, detail="Audio file cannot be empty")
    if len(content) > MAX_AUDIO_BYTES:
        raise HTTPException(status_code=413, detail=f"Audio file exceeds maximum size of {MAX_AUDIO_BYTES} bytes")

    start_time = time.perf_counter()
    async with stt_semaphore:
        # Write to temporary file for faster-whisper/av to read
        with tempfile.NamedTemporaryFile(suffix=".wav", delete=False) as tmp:
            tmp.write(content)
            tmp_path = tmp.name

        try:
            # Run CPU transcription in thread pool to avoid blocking the event loop
            def run_whisper():
                segments, info = whisper_model.transcribe(
                    tmp_path,
                    language=language if language and language != "auto" else "ar",
                    initial_prompt=prompt,
                    beam_size=5,
                    vad_filter=True,
                    vad_parameters=dict(min_silence_duration_ms=500)
                )
                text_chunks = [seg.text.strip() for seg in segments]
                return " ".join(text_chunks).strip(), info

            transcribed_text, info = await asyncio.to_thread(run_whisper)
            latency = time.perf_counter() - start_time
            logger.info(f"[STT] Transcribed {len(content)} bytes in {latency:.2f}s (lang={info.language}, prob={info.language_probability:.2f}): '{transcribed_text}'")
            return {"text": transcribed_text}
        except Exception as e:
            logger.error(f"[STT] Transcription failed: {e}", exc_info=True)
            raise HTTPException(status_code=500, detail=f"Transcription failed: {str(e)}")
        finally:
            if os.path.exists(tmp_path):
                try:
                    os.remove(tmp_path)
                except OSError:
                    pass


@app.post("/v1/audio/speech")
async def synthesize_speech(req: SpeechRequest):
    text = req.input.strip()
    if not text:
        raise HTTPException(status_code=400, detail="Text cannot be empty")
    if len(text) > 2000:
        raise HTTPException(status_code=400, detail="Text exceeds maximum limit of 2000 characters")

    voice = req.voice or DEFAULT_VOICE
    fmt = (req.response_format or "wav").lower()

    start_time = time.perf_counter()
    async with tts_semaphore:
        try:
            # Generate speech via edge-tts stream
            communicate = edge_tts.Communicate(text, voice)
            mp3_buffer = bytearray()
            async for chunk in communicate.stream():
                if chunk["type"] == "audio":
                    mp3_buffer.extend(chunk["data"])

            if not mp3_buffer:
                raise HTTPException(status_code=500, detail="TTS service produced empty audio stream")

            if fmt == "mp3":
                latency = time.perf_counter() - start_time
                logger.info(f"[TTS] Synthesized {len(text)} chars to MP3 in {latency:.2f}s")
                return Response(content=bytes(mp3_buffer), media_type="audio/mpeg")

            # Convert to standard 16kHz 16-bit Mono PCM WAV (ideal for browser Web Audio and telephony)
            def convert_to_wav(mp3_bytes: bytes) -> bytes:
                inp = av.open(io.BytesIO(mp3_bytes), format="mp3")
                out_buf = io.BytesIO()
                out = av.open(out_buf, mode="w", format="wav")
                stream = out.add_stream("pcm_s16le", rate=16000, layout="mono")
                resampler = av.AudioResampler(format="s16", layout="mono", rate=16000)
                for frame in inp.decode(audio=0):
                    resampled_frames = resampler.resample(frame)
                    for rf in resampled_frames:
                        for packet in stream.encode(rf):
                            out.mux(packet)
                for packet in stream.encode(None):
                    out.mux(packet)
                out.close()
                return out_buf.getvalue()

            wav_bytes = await asyncio.to_thread(convert_to_wav, bytes(mp3_buffer))
            latency = time.perf_counter() - start_time
            logger.info(f"[TTS] Synthesized {len(text)} chars to 16kHz WAV in {latency:.2f}s (voice={voice}, size={len(wav_bytes)} bytes)")
            return Response(content=wav_bytes, media_type="audio/wav")
        except Exception as e:
            logger.error(f"[TTS] Synthesis failed: {e}", exc_info=True)
            raise HTTPException(status_code=500, detail=f"Speech synthesis failed: {str(e)}")


if __name__ == "__main__":
    import uvicorn
    port = int(os.environ.get("VOICE_PORT", "8000"))
    uvicorn.run("voice_server:app", host="127.0.0.1", port=port, log_level="info")
