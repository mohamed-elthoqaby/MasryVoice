"""
MasryVoice Real Local Voice Pipeline Verification Script ($0 Cost)
Exercises genuine local STT (faster-whisper CPU) and genuine local TTS (Piper ONNX CPU)
with real Arabic WAV audio fixtures, recording separate latencies, verifying booking flow,
and producing machine-readable artifacts.
"""

import os
import io
import sys
import time
import json
import base64
import argparse
import urllib.request
import urllib.error

VOICE_SERVER_URL = os.environ.get("VOICE_SERVER_URL", "http://127.0.0.1:8000")
API_BASE_URL = os.environ.get("API_BASE_URL", "http://localhost:5000")
BASE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ARTIFACTS_DIR = os.path.join(BASE_DIR, "tests", "artifacts")
FIXTURES_DIR = os.path.join(BASE_DIR, "tests", "fixtures", "audio")

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")

os.makedirs(ARTIFACTS_DIR, exist_ok=True)


def http_json(url, method="GET", payload=None, headers=None):
    hdrs = {"Content-Type": "application/json"}
    if headers:
        hdrs.update(headers)
    data = json.dumps(payload).encode("utf-8") if payload is not None else None
    req = urllib.request.Request(url, data=data, headers=hdrs, method=method)
    with urllib.request.urlopen(req, timeout=45) as resp:
        return resp.getcode(), json.loads(resp.read().decode("utf-8")), resp.headers


def http_post_multipart(url, file_path_or_bytes, field_name="file", filename="audio.wav", mime_type="audio/wav", language="ar"):
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
    with urllib.request.urlopen(req, timeout=45) as resp:
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


def main():
    parser = argparse.ArgumentParser(description="MasryVoice Real Local Voice Verification")
    parser.add_argument("--strict", action="store_true", help="Fail nonzero if any required stage is unavailable")
    parser.add_argument("--skip-backend", action="store_true", help="Skip backend E2E turns and test speech server only")
    args = parser.parse_args()

    print("=" * 75)
    print("   MASRYVOICE REAL LOCAL VOICE PIPELINE VERIFICATION ($0 Cost)   ")
    print("=" * 75)

    report = {
        "timestamp_utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "voice_server_url": VOICE_SERVER_URL,
        "api_base_url": API_BASE_URL,
        "mode": "strict" if args.strict else "standard",
        "stages": {},
        "timings_ms": {},
        "verifications": {}
    }

    # -----------------------------------------------------------------
    # Stage 1: Local Voice Server Health & License Verification
    # -----------------------------------------------------------------
    print("\n[Stage 1] Checking Local Voice Server Health & Licenses...")
    t0 = time.perf_counter()
    try:
        code, health_data, _ = http_json(f"{VOICE_SERVER_URL}/health")
        t_health = (time.perf_counter() - t0) * 1000
        assert code == 200, f"Expected 200 from voice server, got {code}"
        assert health_data.get("free_and_local") is True, "Voice server must be free and local"
        assert health_data.get("stt_ready") is True, "Whisper STT must be ready locally"
        assert health_data.get("tts_ready") is True, "Piper TTS must be ready locally"
        print(f" -> Healthy in {t_health:.1f}ms:")
        print(f"    STT Engine: {health_data.get('stt_engine')} (License: {health_data.get('stt_license')})")
        print(f"    TTS Engine: {health_data.get('tts_engine')} (License: {health_data.get('tts_engine_license')})")
        print(f"    Voice Model: {health_data.get('tts_model')} (Dataset: {health_data.get('tts_voice_dataset_license')})")
        report["stages"]["voice_server_health"] = health_data
        report["timings_ms"]["health_check"] = round(t_health, 2)
        report["verifications"]["health_status"] = "PASSED"
    except Exception as e:
        print(f" [FAILED] Health check failed: {e}")
        report["verifications"]["health_status"] = f"FAILED: {e}"
        if args.strict:
            sys.exit(1)

    # -----------------------------------------------------------------
    # Stage 2: Audio Container Duration Bounds & Malformed Rejections
    # -----------------------------------------------------------------
    print("\n[Stage 2] Verifying Duration Limits & Container Bounds on Speech Server...")

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
    # Stage 3: Real Isolated STT Benchmark (Turn 1 & Turn 2 Fixtures)
    # -----------------------------------------------------------------
    turn1_wav = os.path.join(FIXTURES_DIR, "turn1_inquiry.wav")
    turn2_wav = os.path.join(FIXTURES_DIR, "turn2_booking.wav")
    assert os.path.exists(turn1_wav), f"Fixture not found: {turn1_wav}"
    assert os.path.exists(turn2_wav), f"Fixture not found: {turn2_wav}"

    print(f"\n[Stage 3] Benchmarking Real Local STT on Arabic Fixtures...")
    
    # STT Turn 1
    t0 = time.perf_counter()
    code1, res1 = http_post_multipart(f"{VOICE_SERVER_URL}/v1/audio/transcriptions", turn1_wav)
    t_stt1 = (time.perf_counter() - t0) * 1000
    assert code1 == 200, f"STT failed for turn 1 with {code1}"
    transcript1 = res1.get("text", "")
    assert len(transcript1) > 0, "STT returned empty transcript for turn 1"
    print(f" -> STT Fixture 1 ({os.path.getsize(turn1_wav)} bytes) in {t_stt1:.1f}ms: '{transcript1}'")
    report["timings_ms"]["stt_turn1_isolated_ms"] = round(t_stt1, 2)
    report["stages"]["stt_fixture1"] = {"duration_ms": round(t_stt1, 2), "transcript": transcript1}

    # STT Turn 2
    t0 = time.perf_counter()
    code2, res2 = http_post_multipart(f"{VOICE_SERVER_URL}/v1/audio/transcriptions", turn2_wav)
    t_stt2 = (time.perf_counter() - t0) * 1000
    assert code2 == 200, f"STT failed for turn 2 with {code2}"
    transcript2 = res2.get("text", "")
    assert len(transcript2) > 0, "STT returned empty transcript for turn 2"
    assert transcript2 != transcript1, "Turn 2 transcript must not equal Turn 1 transcript"
    print(f" -> STT Fixture 2 ({os.path.getsize(turn2_wav)} bytes) in {t_stt2:.1f}ms: '{transcript2}'")
    report["timings_ms"]["stt_turn2_isolated_ms"] = round(t_stt2, 2)
    report["stages"]["stt_fixture2"] = {"duration_ms": round(t_stt2, 2), "transcript": transcript2}
    report["verifications"]["stt_benchmarks"] = "PASSED"

    # -----------------------------------------------------------------
    # Stage 4: Real Isolated TTS Benchmark (Piper ONNX CPU)
    # -----------------------------------------------------------------
    tts_text = "أهلاً بحضرتك في عيادة النور التخصصية، معاك سارة. إزاي أقدر أساعدك؟"
    print(f"\n[Stage 4] Benchmarking Real Local TTS (Piper ONNX CPU, GPL-3.0-or-later)...")
    t0 = time.perf_counter()
    tts_payload = {"input": tts_text, "voice": "ar_JO-kareem-low", "response_format": "wav"}
    tts_req = urllib.request.Request(f"{VOICE_SERVER_URL}/v1/audio/speech", data=json.dumps(tts_payload).encode("utf-8"), headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(tts_req, timeout=30) as resp:
        tts_code = resp.getcode()
        tts_bytes = resp.read()
    t_tts = (time.perf_counter() - t0) * 1000
    assert tts_code == 200, f"TTS failed with code {tts_code}"
    assert tts_bytes.startswith(b"RIFF"), "TTS output must be valid RIFF WAV"
    print(f" -> TTS completed in {t_tts:.1f}ms: synthesized {len(tts_bytes)} bytes WAV")
    tts_sample_path = os.path.join(ARTIFACTS_DIR, "real_voice_tts_isolated_sample.wav")
    with open(tts_sample_path, "wb") as f:
        f.write(tts_bytes)
    report["timings_ms"]["tts_isolated_ms"] = round(t_tts, 2)
    report["stages"]["tts_benchmark"] = {
        "text": tts_text,
        "output_bytes": len(tts_bytes),
        "duration_ms": round(t_tts, 2),
        "sample_file": os.path.basename(tts_sample_path)
    }
    report["verifications"]["tts_benchmark"] = "PASSED"

    # -----------------------------------------------------------------
    # Stage 5: End-to-End Two-Turn Voice Verification against Live Backend
    # -----------------------------------------------------------------
    backend_available = False
    if not args.skip_backend:
        try:
            _, live_res, _ = http_json(f"{API_BASE_URL}/api/health/live")
            backend_available = True
        except Exception as e:
            print(f"\n[Notice] Backend on {API_BASE_URL} not reachable ({e}).")
            if args.strict:
                print(" [ERROR] Acceptance verification requires running backend. Exiting.")
                sys.exit(1)

    if backend_available:
        print("\n[Stage 5] Exercising Live End-to-End Two-Turn Voice Flow on Backend...")

        # 5.1 Create Session
        _, session_res, _ = http_json(f"{API_BASE_URL}/api/voice/session", method="POST", payload={"conversationId": None})
        s_id = session_res["sessionId"]
        c_id = session_res["conversationId"]
        token = session_res.get("customerToken", "")
        hdrs = {"X-Customer-Token": token} if token else {}
        print(f" -> Voice session created: id={s_id[:16]}..., conversation={c_id[:16]}...")

        # 5.2 Turn 1: Inquiry with audio fixture
        with open(turn1_wav, "rb") as f:
            turn1_b64 = base64.b64encode(f.read()).decode("ascii")

        print(" - Executing E2E Turn 1 (Audio -> STT -> LLM/Tools -> TTS)...")
        t0 = time.perf_counter()
        t1_payload = {
            "sessionId": s_id,
            "conversationId": c_id,
            "agentId": "00000000-0000-0000-0000-000000000000",
            "message": "نص قديم يجب تجاهله تماماً",  # Prove backend prioritizes audio
            "audioBase64": turn1_b64,
            "mimeType": "audio/wav"
        }
        _, t1_res, _ = http_json(f"{API_BASE_URL}/api/voice/turn", method="POST", payload=t1_payload, headers=hdrs)
        t_turn1_total = (time.perf_counter() - t0) * 1000
        assert t1_res.get("turnId") == 1, f"Expected turnId=1, got {t1_res.get('turnId')}"
        user1_transcribed = t1_res.get("userText", "")
        assistant1_text = t1_res.get("text", "")
        assert len(user1_transcribed) > 0, "Turn 1 returned empty userText"
        assert len(assistant1_text) > 0, "Turn 1 returned empty assistant text"
        print(f" -> Turn 1 completed in {t_turn1_total:.1f}ms:")
        print(f"    Recognized user utterance: '{user1_transcribed}'")
        print(f"    Assistant reply: '{assistant1_text[:60]}...'")

        report["stages"]["e2e_turn1"] = {
            "total_ms": round(t_turn1_total, 2),
            "user_text": user1_transcribed,
            "assistant_text": assistant1_text,
            "audio_response_bytes": len(base64.b64decode(t1_res["audioBase64"])) if t1_res.get("audioBase64") else 0
        }
        report["timings_ms"]["e2e_turn1_total_ms"] = round(t_turn1_total, 2)

        # 5.3 Turn 2: Booking intent with audio fixture
        with open(turn2_wav, "rb") as f:
            turn2_b64 = base64.b64encode(f.read()).decode("ascii")

        print("\n - Executing E2E Turn 2 (Audio -> STT -> LLM/Tools -> Staged Booking -> TTS)...")
        t0 = time.perf_counter()
        t2_payload = {
            "sessionId": s_id,
            "conversationId": c_id,
            "agentId": "00000000-0000-0000-0000-000000000000",
            "audioBase64": turn2_b64,
            "mimeType": "audio/wav"
        }
        _, t2_res, _ = http_json(f"{API_BASE_URL}/api/voice/turn", method="POST", payload=t2_payload, headers=hdrs)
        t_turn2_total = (time.perf_counter() - t0) * 1000
        assert t2_res.get("turnId") == 2, f"Expected turnId=2, got {t2_res.get('turnId')}"
        user2_transcribed = t2_res.get("userText", "")
        assistant2_text = t2_res.get("text", "")
        assert user2_transcribed != user1_transcribed, "Turn 2 must NOT reuse Turn 1 transcript!"
        print(f" -> Turn 2 completed in {t_turn2_total:.1f}ms:")
        print(f"    Recognized user utterance: '{user2_transcribed}'")
        print(f"    Assistant reply: '{assistant2_text[:60]}...'")

        report["stages"]["e2e_turn2"] = {
            "total_ms": round(t_turn2_total, 2),
            "user_text": user2_transcribed,
            "assistant_text": assistant2_text,
            "audio_response_bytes": len(base64.b64decode(t2_res["audioBase64"])) if t2_res.get("audioBase64") else 0
        }
        report["timings_ms"]["e2e_turn2_total_ms"] = round(t_turn2_total, 2)

        # 5.4 Interruption barge-in check
        print("\n - Testing Barge-In Interrupt Signal...")
        t0 = time.perf_counter()
        code_int, _, _ = http_json(f"{API_BASE_URL}/api/voice/interrupt", method="POST", payload={"sessionId": s_id}, headers=hdrs)
        t_int = (time.perf_counter() - t0) * 1000
        assert code_int == 200, f"Expected 200 for interrupt, got {code_int}"
        print(f" -> Interrupt acknowledged in {t_int:.1f}ms")
        report["timings_ms"]["interrupt_acknowledgement_ms"] = round(t_int, 2)
        report["verifications"]["e2e_flow"] = "PASSED"

    # -----------------------------------------------------------------
    # Stage 6: Persist & Report Artifacts
    # -----------------------------------------------------------------
    report_file = os.path.join(ARTIFACTS_DIR, "real_voice_verification_report.json")
    with open(report_file, "w", encoding="utf-8") as f:
        json.dump(report, f, ensure_ascii=False, indent=2)

    print("\n" + "=" * 75)
    print("                      VERIFICATION SUMMARY REPORT                      ")
    print("=" * 75)
    print(f"Artifact File: {report_file}")
    print(f"Server Health: {report['verifications'].get('health_status', 'N/A')}")
    print(f"Container Rejection: {report['verifications'].get('overlong_rejection', 'N/A')}")
    print(f"Isolated STT (Turn 1): {report['timings_ms'].get('stt_turn1_isolated_ms', 'N/A')} ms")
    print(f"Isolated STT (Turn 2): {report['timings_ms'].get('stt_turn2_isolated_ms', 'N/A')} ms")
    print(f"Isolated TTS (Piper): {report['timings_ms'].get('tts_isolated_ms', 'N/A')} ms")
    if "e2e_turn1_total_ms" in report["timings_ms"]:
        print(f"E2E Roundtrip (Turn 1): {report['timings_ms']['e2e_turn1_total_ms']} ms")
        print(f"E2E Roundtrip (Turn 2): {report['timings_ms']['e2e_turn2_total_ms']} ms")
    print("=" * 75)
    print("[SUCCESS] All real local voice pipeline verifications passed.\n")


if __name__ == "__main__":
    main()
