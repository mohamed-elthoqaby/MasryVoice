"""
MasryVoice Real Local Voice Pipeline Verification Script ($0 Cost)
Exercises genuine local STT (faster-whisper CPU) and genuine local TTS (Piper ONNX CPU)
with real Arabic WAV audio fixtures, recording separate latencies, verifying booking flow,
staging and confirmation lifecycle, capacity deltas, barge-in interruption, and producing machine-readable artifacts.
"""

import os
import io
import sys
import time
import json
import base64
import sqlite3
import argparse
import urllib.request
import urllib.error
import threading
from typing import Optional

VOICE_SERVER_URL = os.environ.get("VOICE_SERVER_URL", "http://127.0.0.1:8000")
API_BASE_URL = os.environ.get("API_BASE_URL", "http://localhost:5000")
BASE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ARTIFACTS_DIR = os.path.join(BASE_DIR, "tests", "artifacts")
FIXTURES_DIR = os.path.join(BASE_DIR, "tests", "fixtures", "audio")
def resolve_db_path():
    explicit = os.environ.get("ACCEPTANCE_DB_PATH")
    if explicit and os.path.exists(explicit):
        return explicit
    backend_path = os.path.join(BASE_DIR, "backend", "MasryVoice.Api", "masryvoice_real_voice_acceptance.db")
    if os.path.exists(backend_path):
        return backend_path
    return os.path.join(BASE_DIR, "masryvoice_real_voice_acceptance.db")

DB_PATH = resolve_db_path()

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")

os.makedirs(ARTIFACTS_DIR, exist_ok=True)
os.makedirs(FIXTURES_DIR, exist_ok=True)


def http_json(url, method="GET", payload=None, headers=None, timeout=60):
    hdrs = {"Content-Type": "application/json"}
    if headers:
        hdrs.update(headers)
    data = json.dumps(payload).encode("utf-8") if payload is not None else None
    req = urllib.request.Request(url, data=data, headers=hdrs, method=method)
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        return resp.getcode(), json.loads(resp.read().decode("utf-8")), resp.headers


def http_post_multipart(url, file_path_or_bytes, field_name="file", filename="audio.wav", mime_type="audio/wav", language="ar", timeout=60):
    boundary = "----MasryVoiceBoundary"
    if isinstance(file_path_or_bytes, str):
        with open(file_path_or_bytes, "rb") as f:
            file_bytes = f.read()
        filename = os.path.basename(file_path_or_bytes)
    else:
        file_bytes = file_path_or_bytes

    body = bytearray()
    body.extend(f"--{boundary}\r\n".encode())
    body.extend(f'Content-Disposition: form-data; name="{field_name}"; filename="{filename}"\r\n'.encode())
    body.extend(f"Content-Type: {mime_type}\r\n\r\n".encode())
    body.extend(file_bytes)
    body.extend(f"\r\n--{boundary}\r\n".encode())
    body.extend(b'Content-Disposition: form-data; name="language"\r\n\r\n')
    body.extend(language.encode())
    body.extend(f"\r\n--{boundary}--\r\n".encode())

    req = urllib.request.Request(url, data=bytes(body), headers={"Content-Type": f"multipart/form-data; boundary={boundary}"})
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        return resp.getcode(), json.loads(resp.read().decode("utf-8"))


def build_overlong_wav_bytes(duration_sec=35):
    """Builds a real PCM WAV containing duration_sec seconds of valid 16kHz 16-bit mono PCM."""
    sample_rate = 16000
    byte_rate = sample_rate * 2
    data_size = int(duration_sec * byte_rate)
    buf = bytearray()
    buf.extend(b"RIFF")
    buf.extend((36 + data_size).to_bytes(4, "little"))
    buf.extend(b"WAVE")
    buf.extend(b"fmt ")
    buf.extend((16).to_bytes(4, "little"))
    buf.extend((1).to_bytes(2, "little"))   # PCM
    buf.extend((1).to_bytes(2, "little"))   # mono
    buf.extend(sample_rate.to_bytes(4, "little"))
    buf.extend(byte_rate.to_bytes(4, "little"))
    buf.extend((2).to_bytes(2, "little"))
    buf.extend((16).to_bytes(2, "little"))
    buf.extend(b"data")
    buf.extend(data_size.to_bytes(4, "little"))
    buf.extend(bytes(data_size))  # silence samples
    return bytes(buf)


REAL_LLM_PROVIDERS = {"ollama"}
REAL_STT_PROVIDERS = {"whisper", "localegyptian", "localegyptianvoice", "real"}
REAL_TTS_PROVIDERS = {"piper", "localegyptian", "localegyptianvoice", "real"}

REQUIRED_CRITERIA_COMPONENT = [
    "health_status",
    "active_supervision",
    "overlong_rejection",
    "corrupt_rejection",
    "stt_benchmarks",
    "tts_benchmark"
]

REQUIRED_CRITERIA_FULL = REQUIRED_CRITERIA_COMPONENT + [
    "booking_lifecycle",
    "active_barge_in",
    "e2e_flow"
]


def validate_backend_providers(cfg_llm: str, cfg_stt: str, cfg_tts: str,
                               resolved_llm: Optional[str] = None,
                               resolved_stt: Optional[str] = None,
                               resolved_tts: Optional[str] = None):
    """
    Strict whitelist validation of backend LLM, STT, and TTS providers.
    Rejects fake, simulated, missing, unknown, and typo configurations.
    Also validates concrete resolved DI types if provided.
    """
    clean_llm = (cfg_llm or "").strip().lower()
    clean_stt = (cfg_stt or "").strip().lower()
    clean_tts = (cfg_tts or "").strip().lower()

    if clean_llm not in REAL_LLM_PROVIDERS:
        raise ValueError(f"Strict mode requires real LLM provider (whitelisted: {REAL_LLM_PROVIDERS}), got '{cfg_llm}'")
    if clean_stt not in REAL_STT_PROVIDERS:
        raise ValueError(f"Strict mode requires real STT provider (whitelisted: {REAL_STT_PROVIDERS}), got '{cfg_stt}'")
    if clean_tts not in REAL_TTS_PROVIDERS:
        raise ValueError(f"Strict mode requires real TTS provider (whitelisted: {REAL_TTS_PROVIDERS}), got '{cfg_tts}'")

    if resolved_llm:
        if resolved_llm in {"DeterministicFakeLlmProvider"} or resolved_llm != "OllamaLlmProvider":
            raise ValueError(f"Resolved LLM type '{resolved_llm}' is not an authorized real local LLM provider")
    if resolved_stt:
        if resolved_stt in {"SimulatedSttProvider"} or resolved_stt != "WhisperSttProvider":
            raise ValueError(f"Resolved STT type '{resolved_stt}' is not an authorized real local STT provider")
    if resolved_tts:
        if resolved_tts in {"SimulatedTtsProvider"} or resolved_tts != "LocalEgyptianTtsProvider":
            raise ValueError(f"Resolved TTS type '{resolved_tts}' is not an authorized real local TTS provider")


def persist_report(report):
    report_file = os.path.join(ARTIFACTS_DIR, "real_voice_verification_report.json")
    try:
        with open(report_file, "w", encoding="utf-8") as f:
            json.dump(report, f, ensure_ascii=False, indent=2)
    except Exception as e:
        print(f" [WARNING] Failed to write report file: {e}")


def execute_verification(args, report):
    if args.strict and not os.path.exists(DB_PATH):
        print(f" [ERROR] Acceptance database not found at {DB_PATH} in strict mode.")
        report["verifications"]["acceptance_database"] = f"FAILED: Missing DB {DB_PATH}"
        sys.exit(1)

    # -----------------------------------------------------------------
    # Stage 1: Local Voice Server Health, Engine Provenance & Hashes
    # -----------------------------------------------------------------
    print("\n[Stage 1] Checking Local Voice Server Health & Engine Provenance...")
    t0 = time.perf_counter()
    try:
        code, health_data, _ = http_json(f"{VOICE_SERVER_URL}/health")
        t_health = (time.perf_counter() - t0) * 1000
        assert code == 200, f"Expected 200 from voice server, got {code}"
        assert health_data.get("free_and_local") is True, "Voice server must be free and local"
        assert health_data.get("stt_ready") is True, "Whisper STT must be ready locally"
        assert health_data.get("tts_ready") is True, "Piper TTS must be ready locally"
        assert health_data.get("tts_engine_license") == "GPL-3.0-or-later", "Piper TTS license must be GPL-3.0-or-later"
        print(f" -> Healthy in {t_health:.1f}ms:")
        print(f"    STT Engine: {health_data.get('stt_engine')} (License: {health_data.get('stt_license')})")
        print(f"    STT Configured Model: {health_data.get('stt_model_configured')} (Snapshot: {health_data.get('stt_snapshot_hash')})")
        print(f"    STT Model Bin SHA256: {str(health_data.get('stt_model_bin_sha256'))[:16]}...")
        print(f"    TTS Engine: {health_data.get('tts_engine')} (License: {health_data.get('tts_engine_license')})")
        print(f"    TTS Voice Model: {health_data.get('tts_model')} (Weights SHA256: {str(health_data.get('tts_model_sha256'))[:16]}...)")
        print(f"    TTS Dataset Provenance: {health_data.get('tts_dataset_reference')}")
        report["stages"]["voice_server_health"] = health_data
        report["timings_ms"]["health_check_idle_ms"] = round(t_health, 2)
        report["verifications"]["health_status"] = "PASSED"
    except Exception as e:
        print(f" [FAILED] Health check failed: {e}")
        report["verifications"]["health_status"] = f"FAILED: {e}"
        if args.strict:
            persist_report(report)
            sys.exit(1)

    # -----------------------------------------------------------------
    # Stage 2: Audio Container Bounds, Duration Bounds & PyAV Offloading
    # -----------------------------------------------------------------
    print("\n[Stage 2] Verifying Audio Container Bounds & PyAV Offloaded Validation...")

    # 2a: Real 35-second audio (>30s) rejection
    overlong_wav = build_overlong_wav_bytes(35)
    try:
        http_post_multipart(f"{VOICE_SERVER_URL}/v1/audio/transcriptions", overlong_wav, filename="overlong_35s.wav")
        assert False, "Speech server accepted >30s audio without error!"
    except urllib.error.HTTPError as he:
        assert he.code == 400, f"Expected 400 for >30s audio, got {he.code}"
        err_body = json.loads(he.read().decode("utf-8"))
        assert err_body.get("detail", {}).get("code") == "audio_duration_exceeded", f"Expected audio_duration_exceeded, got {err_body}"
        print(f" -> Real 35s audio correctly rejected with HTTP 400 ({err_body['detail']['code']})")
        report["verifications"]["overlong_rejection"] = "PASSED"

    # 2b: Truncated/corrupt payload rejection
    corrupt_bytes = b"RIFF\x20\x00\x00\x00WAVE" + b"\xff" * 50
    try:
        http_post_multipart(f"{VOICE_SERVER_URL}/v1/audio/transcriptions", corrupt_bytes, filename="corrupt.wav")
        assert False, "Speech server accepted corrupt audio without error!"
    except urllib.error.HTTPError as he:
        assert he.code == 400, f"Expected 400 for corrupt audio, got {he.code}"
        print(f" -> Corrupted audio container correctly rejected with HTTP 400")
        report["verifications"]["corrupt_rejection"] = "PASSED"

    # -----------------------------------------------------------------
    # Stage 3: Responsiveness During Active Work & Capacity Retention
    # -----------------------------------------------------------------
    print("\n[Stage 3] Testing Server Responsiveness DURING Active Inference & Capacity Supervision...")
    # 3a: Health check during active synthesis
    long_text = "أهلاً وسهلاً بحضرتك في عيادة النور التخصصية بالقاهرة، نحن سعداء جداً بتقديم الرعاية الطبية الفائقة لحضرتك في تخصصات الباطنة والأطفال والعيون والجراحة العامة طوال أيام الأسبوع."
    synth_started = threading.Event()
    bg_result = {}

    def background_synthesis():
        synth_started.set()
        try:
            req_data = json.dumps({"input": long_text}).encode("utf-8")
            r = urllib.request.Request(f"{VOICE_SERVER_URL}/v1/audio/speech", data=req_data, headers={"Content-Type": "application/json"})
            with urllib.request.urlopen(r, timeout=15) as resp:
                data = resp.read()
                bg_result["status"] = resp.getcode()
                bg_result["bytes"] = len(data)
        except Exception as e:
            bg_result["error"] = str(e)

    t_bg = threading.Thread(target=background_synthesis)
    t_bg.start()
    synth_started.wait()

    # Poll /health until active worker is genuinely observed (> 0)
    observed_active = 0
    dt_active_health = 0.0
    for _ in range(100):
        t_h0 = time.perf_counter()
        code_h, active_health, _ = http_json(f"{VOICE_SERVER_URL}/health")
        dt_health = (time.perf_counter() - t_h0) * 1000
        if active_health.get("tts_active_workers", 0) > 0:
            observed_active = active_health.get("tts_active_workers")
            dt_active_health = dt_health
            break
        time.sleep(0.01)

    t_bg.join()
    assert bg_result.get("status") == 200, f"Background synthesis failed: {bg_result}"
    assert bg_result.get("bytes", 0) > 44, "Background synthesis returned empty audio"

    print(f" -> /health responsiveness DURING active inference: {dt_active_health:.2f}ms (Active TTS workers observed: {observed_active})")
    assert observed_active > 0, f"Active TTS worker must be observed during background synthesis, got {observed_active}"
    assert dt_active_health < 1000.0, f"Health check during active inference exceeded threshold: {dt_active_health}ms"
    report["timings_ms"]["health_check_during_active_work_ms"] = round(dt_active_health, 2)
    report["stages"]["active_concurrency_supervision"] = {
        "health_during_inference_ms": round(dt_active_health, 2),
        "active_tts_observed": observed_active,
        "bg_synthesis_status": bg_result.get("status"),
        "bg_synthesis_bytes": bg_result.get("bytes"),
        "non_blocking_event_loop": True
    }
    report["verifications"]["active_supervision"] = "PASSED"

    # -----------------------------------------------------------------
    # Stage 4: Focused STT Comparison on Arabic Fixtures (tiny vs base)
    # -----------------------------------------------------------------
    turn1_wav = os.path.join(FIXTURES_DIR, "turn1_inquiry.wav")
    turn2_wav = os.path.join(FIXTURES_DIR, "turn2_booking.wav")
    assert os.path.exists(turn1_wav), f"Fixture not found: {turn1_wav}"
    assert os.path.exists(turn2_wav), f"Fixture not found: {turn2_wav}"

    print(f"\n[Stage 4] Benchmarking STT on Arabic Fixtures with Configured Engine...")
    t0 = time.perf_counter()
    code1, res1 = http_post_multipart(f"{VOICE_SERVER_URL}/v1/audio/transcriptions", turn1_wav)
    t_stt1 = (time.perf_counter() - t0) * 1000
    assert code1 == 200, f"STT failed for turn 1 with {code1}"
    transcript1 = res1.get("text", "")
    raw_transcript1 = res1.get("raw_text", transcript1)
    print(f" -> Fixture 1 ({os.path.getsize(turn1_wav)} bytes) in {t_stt1:.1f}ms: '{transcript1}' (raw: '{raw_transcript1}')")

    t0 = time.perf_counter()
    code2, res2 = http_post_multipart(f"{VOICE_SERVER_URL}/v1/audio/transcriptions", turn2_wav)
    t_stt2 = (time.perf_counter() - t0) * 1000
    assert code2 == 200, f"STT failed for turn 2 with {code2}"
    transcript2 = res2.get("text", "")
    raw_transcript2 = res2.get("raw_text", transcript2)
    print(f" -> Fixture 2 ({os.path.getsize(turn2_wav)} bytes) in {t_stt2:.1f}ms: '{transcript2}' (raw: '{raw_transcript2}')")

    report["stages"]["stt_benchmarks"] = {
        "configured_model": health_data.get("stt_model_configured"),
        "turn1": {"duration_ms": round(t_stt1, 2), "transcript": transcript1, "raw_transcript": raw_transcript1},
        "turn2": {"duration_ms": round(t_stt2, 2), "transcript": transcript2, "raw_transcript": raw_transcript2}
    }
    report["timings_ms"]["stt_turn1_ms"] = round(t_stt1, 2)
    report["timings_ms"]["stt_turn2_ms"] = round(t_stt2, 2)
    report["verifications"]["stt_benchmarks"] = "PASSED"

    # Isolated TTS Benchmark
    tts_text = "أهلاً بحضرتك في عيادة النور التخصصية، معاك سارة. إزاي أقدر أساعدك؟"
    print(f"\n[Stage 4b] Benchmarking Isolated TTS (Piper Kareem Low)...")
    t0 = time.perf_counter()
    tts_payload = {"input": tts_text, "voice": "ar_JO-kareem-low", "response_format": "wav"}
    tts_code, tts_bytes = 0, b""
    tts_req = urllib.request.Request(f"{VOICE_SERVER_URL}/v1/audio/speech", data=json.dumps(tts_payload).encode("utf-8"), headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(tts_req, timeout=30) as resp:
        tts_code = resp.getcode()
        tts_bytes = resp.read()
    t_tts = (time.perf_counter() - t0) * 1000
    assert tts_code == 200
    assert tts_bytes.startswith(b"RIFF")
    print(f" -> Isolated TTS completed in {t_tts:.1f}ms ({len(tts_bytes)} bytes WAV)")
    tts_sample_path = os.path.join(ARTIFACTS_DIR, "real_voice_tts_isolated_sample.wav")
    with open(tts_sample_path, "wb") as f:
        f.write(tts_bytes)

    assert len(tts_bytes) > 44, "Isolated TTS generated empty audio payload"
    report["timings_ms"]["tts_isolated_ms"] = round(t_tts, 2)
    report["stages"]["tts_benchmark"] = {
        "text": tts_text,
        "duration_ms": round(t_tts, 2),
        "bytes": len(tts_bytes),
        "sample_artifact": os.path.basename(tts_sample_path)
    }
    report["verifications"]["tts_benchmark"] = "PASSED"

    # -----------------------------------------------------------------
    # Stage 5: Live End-to-End Voice Booking, Staging & Confirmation Lifecycle
    # -----------------------------------------------------------------
    backend_available = False
    if not args.skip_backend:
        try:
            _, live_res, _ = http_json(f"{API_BASE_URL}/api/health/live")
            _, h_res, _ = http_json(f"{API_BASE_URL}/api/health")
            backend_available = True
            cfg_llm = (h_res.get("configuredLlmProvider") or "").strip()
            cfg_stt = (h_res.get("configuredSttProvider") or "").strip()
            cfg_tts = (h_res.get("configuredTtsProvider") or "").strip()
            resolved_llm = h_res.get("resolvedLlmType")
            resolved_stt = h_res.get("resolvedSttType")
            resolved_tts = h_res.get("resolvedTtsType")
            print(f" -> Backend reachable (LLM: {cfg_llm} [{resolved_llm}], STT: {cfg_stt} [{resolved_stt}], TTS: {cfg_tts} [{resolved_tts}])")
            report["stages"]["backend_providers"] = {
                "llm": cfg_llm,
                "stt": cfg_stt,
                "tts": cfg_tts,
                "resolved_llm": resolved_llm,
                "resolved_stt": resolved_stt,
                "resolved_tts": resolved_tts
            }
            if args.strict:
                try:
                    validate_backend_providers(cfg_llm, cfg_stt, cfg_tts, resolved_llm, resolved_stt, resolved_tts)
                except ValueError as ve:
                    report["verifications"]["backend_providers"] = f"FAILED: {ve}"
                    print(f" [ERROR] Provider validation failed: {ve}")
                    sys.exit(1)
            report["verifications"]["backend_providers"] = "PASSED"
        except Exception as e:
            print(f"\n[Notice] Backend on {API_BASE_URL} not reachable ({e}).")
            if args.strict:
                print(" [ERROR] Acceptance verification requires running backend. Exiting.")
                sys.exit(1)

    if backend_available:
        print("\n[Stage 5] Exercising Live End-to-End Spoken Booking Dialogue...")

        # 5.1 Create Session
        _, session_res, _ = http_json(f"{API_BASE_URL}/api/voice/session", method="POST", payload={"conversationId": None})
        s_id = session_res["sessionId"]
        c_id = session_res["conversationId"]
        token = session_res.get("customerToken", "")
        hdrs = {"X-Customer-Token": token} if token else {}
        print(f" -> Session created: sessionId={s_id[:16]}..., conversationId={c_id[:16]}...")

        # 5.2 Turn 1: Availability Inquiry via Audio Fixture
        with open(turn1_wav, "rb") as f:
            turn1_b64 = base64.b64encode(f.read()).decode("ascii")

        print(" - Turn 1: Spoken Inquiry (Audio -> STT -> LLM/CheckAvailability -> TTS)...")
        t0 = time.perf_counter()
        t1_payload = {
            "sessionId": s_id,
            "conversationId": c_id,
            "agentId": "00000000-0000-0000-0000-000000000000",
            "message": None,  # Strictly null to guarantee STT priority
            "audioBase64": turn1_b64,
            "mimeType": "audio/wav"
        }
        _, t1_res, _ = http_json(f"{API_BASE_URL}/api/voice/turn", method="POST", payload=t1_payload, headers=hdrs, timeout=90)
        t_turn1_total = (time.perf_counter() - t0) * 1000

        t1_user_text = t1_res.get("userText", "")
        t1_reply_text = t1_res.get("text", "")
        t1_timings = t1_res.get("timings", {})
        t1_audio_bytes = base64.b64decode(t1_res["audioBase64"]) if t1_res.get("audioBase64") else b""

        t1_audio_file = os.path.join(ARTIFACTS_DIR, "turn1_assistant_response.wav")
        if t1_audio_bytes:
            with open(t1_audio_file, "wb") as f:
                f.write(t1_audio_bytes)

        print(f" -> Turn 1 completed in {t_turn1_total:.1f}ms (STT: {t1_timings.get('sttMs')}ms, LLM: {t1_timings.get('llmMs')}ms, TTS: {t1_timings.get('ttsMs')}ms)")
        print(f"    Recognized user utterance: '{t1_user_text}'")
        print(f"    Assistant reply: '{t1_reply_text[:60]}...'")

        report["stages"]["e2e_turn1"] = {
            "http_total_ms": round(t_turn1_total, 2),
            "backend_timings": t1_timings,
            "user_text": t1_user_text,
            "assistant_text": t1_reply_text,
            "audio_response_bytes": len(t1_audio_bytes),
            "audio_artifact": os.path.basename(t1_audio_file)
        }

        # 5.3 Turn 2: Booking Request via Audio Fixture
        with open(turn2_wav, "rb") as f:
            turn2_b64 = base64.b64encode(f.read()).decode("ascii")

        print("\n - Turn 2: Spoken Booking Request (Audio -> STT -> LLM/StageBooking -> TTS)...")
        t0 = time.perf_counter()
        t2_payload = {
            "sessionId": s_id,
            "conversationId": c_id,
            "agentId": "00000000-0000-0000-0000-000000000000",
            "message": None,
            "audioBase64": turn2_b64,
            "mimeType": "audio/wav"
        }
        _, t2_res, _ = http_json(f"{API_BASE_URL}/api/voice/turn", method="POST", payload=t2_payload, headers=hdrs, timeout=90)
        t_turn2_total = (time.perf_counter() - t0) * 1000

        t2_user_text = t2_res.get("userText", "")
        t2_reply_text = t2_res.get("text", "")
        t2_timings = t2_res.get("timings", {})
        t2_audio_bytes = base64.b64decode(t2_res["audioBase64"]) if t2_res.get("audioBase64") else b""

        t2_audio_file = os.path.join(ARTIFACTS_DIR, "turn2_assistant_response.wav")
        if t2_audio_bytes:
            with open(t2_audio_file, "wb") as f:
                f.write(t2_audio_bytes)

        print(f" -> Turn 2 completed in {t_turn2_total:.1f}ms (STT: {t2_timings.get('sttMs')}ms, LLM: {t2_timings.get('llmMs')}ms, TTS: {t2_timings.get('ttsMs')}ms)")
        print(f"    Recognized user utterance: '{t2_user_text}'")
        print(f"    Assistant reply: '{t2_reply_text[:60]}...'")

        report["stages"]["e2e_turn2"] = {
            "http_total_ms": round(t_turn2_total, 2),
            "backend_timings": t2_timings,
            "user_text": t2_user_text,
            "assistant_text": t2_reply_text,
            "audio_response_bytes": len(t2_audio_bytes),
            "audio_artifact": os.path.basename(t2_audio_file)
        }

        # 5.4 Database Verification of Staged Pending Booking
        print("\n - Inspecting Staged Booking Draft in Acceptance Database...")
        pending_record = None
        slot_before = None
        if not os.path.exists(DB_PATH):
            if args.strict:
                print(f" [ERROR] Acceptance database not found at {DB_PATH} in strict mode.")
                report["verifications"]["booking_lifecycle"] = f"FAILED: Missing DB {DB_PATH}"
                persist_report(report)
                sys.exit(1)
        else:
            conn = sqlite3.connect(DB_PATH)
            c = conn.cursor()
            c.execute("SELECT Id, SlotId, CustomerName, CustomerPhone, ServiceName, RequestHash, Status FROM PendingBookings WHERE ConversationId = ? COLLATE NOCASE AND Status = 'Pending' ORDER BY CreatedAtUtc DESC LIMIT 1", (c_id,))
            row = c.fetchone()
            if row:
                pending_record = {
                    "pendingId": row[0],
                    "slotId": row[1],
                    "customerName": row[2],
                    "customerPhone": row[3],
                    "serviceName": row[4],
                    "requestHash": row[5],
                    "status": row[6]
                }
                c.execute("SELECT TotalCapacity, BookedCapacity FROM AvailabilitySlots WHERE Id = ? COLLATE NOCASE", (row[1],))
                srow = c.fetchone()
                if srow:
                    slot_before = {"total": srow[0], "booked": srow[1]}
            c.execute("SELECT COUNT(*) FROM Bookings WHERE ConversationId = ? COLLATE NOCASE", (c_id,))
            confirmed_before_count = c.fetchone()[0]

            # Verify tool execution records in database
            c.execute("SELECT ToolName, Status FROM ToolExecutions WHERE ConversationId = ? COLLATE NOCASE ORDER BY ExecutedAtUtc ASC", (c_id,))
            tool_rows = c.fetchall()
            tool_names = [tr[0] for tr in tool_rows]
            conn.close()

            print(f"    Pending Booking Found: {pending_record}")
            print(f"    Recorded Tool Executions: {tool_rows}")
            print(f"    Confirmed Bookings in DB prior to confirmation: {confirmed_before_count} (Trust Boundary: PASS)")
            assert confirmed_before_count == 0, "No confirmed booking should exist before explicit server-side confirmation!"
            if args.strict:
                assert pending_record is not None, "Pending booking draft must be staged in database during Turn 2 in strict mode"
                assert pending_record["customerName"] == "محمد عاطف", f"Expected 'محمد عاطف', got '{pending_record['customerName']}'"
                assert pending_record["customerPhone"] == "01012345678", f"Expected '01012345678', got '{pending_record['customerPhone']}'"
                assert pending_record["serviceName"] == "كشف باطنة عامة", f"Expected 'كشف باطنة عامة', got '{pending_record['serviceName']}'"
                assert "StageBooking" in tool_names, f"Expected StageBooking in tool executions, got {tool_names}"

        # 5.5 Explicit Server-Authorized Confirmation & Capacity Delta
        confirmed_booking_id = None
        if pending_record:
            print("\n - Calling Server-Authorized Confirmation Endpoint (POST /api/bookings/confirm)...")
            confirm_payload = {
                "conversationId": c_id,
                "pendingBookingId": pending_record["pendingId"],
                "expectedRequestHash": pending_record["requestHash"]
            }
            code_conf, conf_res, _ = http_json(f"{API_BASE_URL}/api/bookings/confirm", method="POST", payload=confirm_payload, headers=hdrs)
            assert code_conf == 200, f"Confirmation failed with code {code_conf}: {conf_res}"
            confirmed_booking_id = conf_res.get("bookingId")
            print(f" -> Booking Confirmed Successfully: ID={confirmed_booking_id}")

            # Verify Database State and Capacity Delta
            conn = sqlite3.connect(DB_PATH)
            c = conn.cursor()
            c.execute("SELECT Status FROM PendingBookings WHERE Id = ? COLLATE NOCASE", (pending_record["pendingId"],))
            p_status_after = c.fetchone()[0]
            c.execute("SELECT BookedCapacity FROM AvailabilitySlots WHERE Id = ? COLLATE NOCASE", (pending_record["slotId"],))
            booked_after = c.fetchone()[0]
            c.execute("SELECT CustomerName, CustomerPhone, ServiceName, Status FROM Bookings WHERE ConversationId = ? COLLATE NOCASE", (c_id,))
            booking_row = c.fetchone()
            c.execute("SELECT COUNT(*) FROM Bookings WHERE ConversationId = ? COLLATE NOCASE", (c_id,))
            confirmed_after_count = c.fetchone()[0]
            conn.close()

            print(f"    Pending Booking Status After Confirmation: {p_status_after}")
            print(f"    Confirmed Booking Row in DB: {booking_row}")
            print(f"    Capacity Delta: {slot_before['booked']} -> {booked_after} (Delta: +1 PASS)")
            assert p_status_after == "Confirmed", "PendingBooking status must transition to 'Confirmed'"
            assert booked_after == slot_before["booked"] + 1, "Slot booked capacity must increment by exactly 1"
            assert confirmed_after_count == 1, "Exactly one booking must be created"
            assert booking_row is not None, "Confirmed booking must exist in Bookings table"
            assert booking_row[0] == "محمد عاطف", f"Confirmed booking customerName mismatch: expected 'محمد عاطف', got '{booking_row[0]}'"
            assert booking_row[1] == "01012345678", f"Confirmed booking customerPhone mismatch: expected '01012345678', got '{booking_row[1]}'"
            assert booking_row[2] == "كشف باطنة عامة", f"Confirmed booking serviceName mismatch: expected 'كشف باطنة عامة', got '{booking_row[2]}'"
            assert booking_row[3] == "Confirmed", f"Confirmed booking status mismatch: expected 'Confirmed', got '{booking_row[3]}'"

            # 5.6 Replay Idempotency Verification
            print(" - Testing Replay Idempotency (calling confirm second time with same hash)...")
            code_replay, replay_res, _ = http_json(f"{API_BASE_URL}/api/bookings/confirm", method="POST", payload=confirm_payload, headers=hdrs)
            assert code_replay == 200
            assert replay_res.get("bookingId") == confirmed_booking_id, "Replay must return identical bookingId without re-booking"

            conn = sqlite3.connect(DB_PATH)
            c = conn.cursor()
            c.execute("SELECT BookedCapacity FROM AvailabilitySlots WHERE Id = ? COLLATE NOCASE", (pending_record["slotId"],))
            booked_replay = c.fetchone()[0]
            conn.close()
            assert booked_replay == booked_after, "Replay must not increment capacity again!"
            print(f" -> Replay Idempotency Verified (Capacity unchanged at {booked_replay})")

            report["stages"]["booking_lifecycle"] = {
                "pending_record": pending_record,
                "confirmed_booking_id": confirmed_booking_id,
                "capacity_delta": {
                    "before": slot_before["booked"],
                    "after": booked_after,
                    "delta": booked_after - slot_before["booked"]
                },
                "idempotent_replay_passed": True
            }
            report["verifications"]["booking_lifecycle"] = "PASSED"

        # -----------------------------------------------------------------
        # Stage 6: Active Barge-In Interruption of In-Flight Work
        # -----------------------------------------------------------------
        print("\n[Stage 6] Testing Active In-Flight Barge-In Interruption...")
        in_flight_result = {"status_code": None, "error": None, "duration_ms": 0}
        turn_started_event = threading.Event()

        def in_flight_turn_worker():
            t_start = time.perf_counter()
            turn_started_event.set()
            try:
                t_active_payload = {
                    "sessionId": s_id,
                    "conversationId": c_id,
                    "agentId": "00000000-0000-0000-0000-000000000000",
                    "message": None,
                    "audioBase64": turn1_b64,
                    "mimeType": "audio/wav"
                }
                c, r, _ = http_json(f"{API_BASE_URL}/api/voice/turn", method="POST", payload=t_active_payload, headers=hdrs, timeout=30)
                in_flight_result["status_code"] = c
            except urllib.error.HTTPError as he:
                in_flight_result["status_code"] = he.code
            except Exception as ex:
                in_flight_result["error"] = str(ex)
            finally:
                in_flight_result["duration_ms"] = (time.perf_counter() - t_start) * 1000

        t_inflight = threading.Thread(target=in_flight_turn_worker)
        t_inflight.start()
        turn_started_event.wait()

        # Poll until work is demonstrably underway on server
        observable_work_started = False
        for _ in range(50):
            try:
                _, h_check, _ = http_json(f"{VOICE_SERVER_URL}/health", timeout=2)
                if h_check.get("stt_active_workers", 0) > 0 or h_check.get("tts_active_workers", 0) > 0:
                    observable_work_started = True
                    break
            except Exception:
                pass
            time.sleep(0.01)

        if not observable_work_started:
            report["verifications"]["active_barge_in"] = "FAILED: Active worker work was not observed before interrupt"
            assert observable_work_started, "Active worker work must be demonstrably observable in /health before issuing interrupt"

        t0 = time.perf_counter()
        code_int, int_res, _ = http_json(f"{API_BASE_URL}/api/voice/interrupt", method="POST", payload={"sessionId": s_id}, headers=hdrs)
        t_int = (time.perf_counter() - t0) * 1000
        assert code_int == 200, f"Interrupt failed with {code_int}"
        assert int_res.get("interrupted") is True, f"Expected interrupted=True, got {int_res}"

        t_inflight.join(timeout=10)
        assert in_flight_result["status_code"] == 499, f"Expected HTTP 499 (Client Closed Request / Interrupted), got {in_flight_result['status_code']}"
        print(f" -> Active in-flight turn interrupted successfully: status={in_flight_result['status_code']} in {in_flight_result['duration_ms']:.1f}ms (Observable worker active: {observable_work_started})")
        print(f" -> Interrupt endpoint latency: {t_int:.1f}ms (interrupted={int_res.get('interrupted')})")

        settled = False
        health_after_int = {}
        for _ in range(30):
            time.sleep(0.1)
            _, health_after_int, _ = http_json(f"{VOICE_SERVER_URL}/health")
            if health_after_int.get("stt_active_workers", 0) == 0 and health_after_int.get("tts_active_workers", 0) == 0:
                settled = True
                break
        assert settled, f"Active workers did not return to 0 within deadline: {health_after_int}"
        print(f" -> Capacity cleanly released after interruption: STT={health_after_int.get('stt_active_workers')}, TTS={health_after_int.get('tts_active_workers')}")

        report["stages"]["active_barge_in"] = {
            "interrupt_latency_ms": round(t_int, 2),
            "interrupted_turn_status": in_flight_result["status_code"],
            "interrupted_turn_duration_ms": round(in_flight_result["duration_ms"], 2),
            "observable_work_started": observable_work_started,
            "stt_active_workers_after": health_after_int.get("stt_active_workers", 0),
            "tts_active_workers_after": health_after_int.get("tts_active_workers", 0)
        }
        report["timings_ms"]["active_barge_in_interrupt_ms"] = round(t_int, 2)
        report["verifications"]["active_barge_in"] = "PASSED"
        report["verifications"]["e2e_flow"] = "PASSED"
def main():
    parser = argparse.ArgumentParser(description="MasryVoice Real Local Voice Verification")
    parser.add_argument("--strict", action="store_true", help="Fail nonzero if any required stage is unavailable")
    parser.add_argument("--skip-backend", action="store_true", help="Skip backend E2E turns and test speech server only")
    args = parser.parse_args()

    if args.strict and args.skip_backend:
        print(" [ERROR] --strict cannot be used with --skip-backend. Full end-to-end verification is required.")
        sys.exit(1)

    print("=" * 78)
    print("   MASRYVOICE REAL LOCAL VOICE PIPELINE ACCEPTANCE VERIFICATION ($0 Cost)   ")
    print("=" * 78)

    global DB_PATH
    DB_PATH = resolve_db_path()

    report = {
        "timestamp_utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "voice_server_url": VOICE_SERVER_URL,
        "api_base_url": API_BASE_URL,
        "mode": "strict" if args.strict else "standard",
        "acceptance_database": DB_PATH,
        "fixture_provenance": "synthetic spoken fixtures with real inference",
        "hardware_profile": "100% Local $0 Cost CPU Benchmark",
        "stages": {},
        "timings_ms": {},
        "verifications": {}
    }

    try:
        execute_verification(args, report)
    finally:
        persist_report(report)

    print("\n" + "=" * 78)
    print("                      VERIFICATION SUMMARY REPORT                      ")
    print("=" * 78)
    print(f"Artifact File:           {os.path.join(ARTIFACTS_DIR, 'real_voice_verification_report.json')}")
    print(f"Server Health:           {report['verifications'].get('health_status', 'N/A')}")
    print(f"Active Supervision:      {report['verifications'].get('active_supervision', 'N/A')}")
    print(f"Container Rejection:     {report['verifications'].get('overlong_rejection', 'N/A')}")
    print(f"STT Turn 1:              {report['timings_ms'].get('stt_turn1_ms', 'N/A')} ms")
    print(f"STT Turn 2:              {report['timings_ms'].get('stt_turn2_ms', 'N/A')} ms")
    print(f"Isolated TTS (Piper):    {report['timings_ms'].get('tts_isolated_ms', 'N/A')} ms")
    print(f"Active Health Check:     {report['timings_ms'].get('health_check_during_active_work_ms', 'N/A')} ms")
    if "e2e_turn1" in report.get("stages", {}):
        t1_b = report["stages"]["e2e_turn1"].get("backend_timings", {})
        print(f"Turn 1 Breakdown:        STT={t1_b.get('sttMs')}ms | LLM={t1_b.get('llmMs')}ms | TTS={t1_b.get('ttsMs')}ms | Total={report['stages']['e2e_turn1'].get('http_total_ms')}ms")
    if "e2e_turn2" in report.get("stages", {}):
        t2_b = report["stages"]["e2e_turn2"].get("backend_timings", {})
        print(f"Turn 2 Breakdown:        STT={t2_b.get('sttMs')}ms | LLM={t2_b.get('llmMs')}ms | TTS={t2_b.get('ttsMs')}ms | Total={report['stages']['e2e_turn2'].get('http_total_ms')}ms")
    print(f"Booking Lifecycle:       {report['verifications'].get('booking_lifecycle', 'N/A')}")
    print("=" * 78)

    required_criteria = REQUIRED_CRITERIA_COMPONENT if args.skip_backend else REQUIRED_CRITERIA_FULL
    failed_items = [
        crit for crit in required_criteria
        if report.get("verifications", {}).get(crit) != "PASSED"
    ]
    if failed_items:
        print(f"[FAILURE] The following required criteria did not pass: {failed_items}\n")
        sys.exit(1)
    elif args.skip_backend:
        print("[PARTIAL] Component-only mode completed successfully (backend E2E turns skipped).\n")
    else:
        print("[SUCCESS] All required real local voice pipeline criteria passed.\n")


if __name__ == "__main__":
    main()
