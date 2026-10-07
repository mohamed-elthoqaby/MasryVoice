import os
import sys
import wave
from piper.voice import PiperVoice
from faster_whisper import WhisperModel

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")

fixtures_dir = "tests/fixtures/audio"
os.makedirs(fixtures_dir, exist_ok=True)
voice = PiperVoice.load("models/piper/ar_JO-kareem-low/ar_JO-kareem-low.onnx", config_path="models/piper/ar_JO-kareem-low/ar_JO-kareem-low.onnx.json")

prompts = {
    "turn1_inquiry.wav": "عايز أعرف المواعيد المتاحة بكرة لكشف الباطنة",
    "turn2_booking.wav": "تمام، احجزلي كشف باطنة الساعة 10 الصبح باسم محمد عاطف وتليفوني 01012345678"
}

for fname, text in prompts.items():
    fpath = os.path.join(fixtures_dir, fname)
    with wave.open(fpath, "wb") as wav_file:
        wav_file.setnchannels(1)
        wav_file.setsampwidth(2)
        wav_file.setframerate(16000)
        for chunk in voice.synthesize(text):
            wav_file.writeframes(chunk.audio_int16_bytes)
    print(f"Generated {fpath} ({os.path.getsize(fpath)} bytes) for: '{text}'")

tiny_path = os.path.abspath("models/whisper/models--Systran--faster-whisper-tiny/snapshots/d90ca5fe260221311c53c58e660288d3deb8d356")
base_path = os.path.abspath("models/whisper/models--Systran--faster-whisper-base/snapshots/ebe41f70d5b6dfa9166e2c581c45c9c0cfc57b66")

tiny = WhisperModel(tiny_path, device="cpu", compute_type="int8", local_files_only=True)
base = WhisperModel(base_path, device="cpu", compute_type="int8", local_files_only=True)

print("\n--- Focused Comparison on Fixtures ---")
for fname in ["turn1_inquiry.wav", "turn2_booking.wav"]:
    fpath = os.path.join(fixtures_dir, fname)
    segs_t, _ = tiny.transcribe(fpath, language="ar", beam_size=5)
    t_text = " ".join(s.text.strip() for s in segs_t)
    segs_b, _ = base.transcribe(fpath, language="ar", beam_size=5)
    b_text = " ".join(s.text.strip() for s in segs_b)
    print(f"\nFixture: {fname}")
    print(f"  TINY: '{t_text}'")
    print(f"  BASE: '{b_text}'")
