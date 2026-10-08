"""Browser-shaped pipeline verification through the Next.js proxy (MV-ARCH-002/003).

Evidence level: REAL LOCAL VERIFICATION of transport + services using a PRE-RECORDED
fixture re-encoded to WebM/Opus (the container/codec Chrome MediaRecorder produces).
This is NOT a physical-microphone test and NOT an intelligibility acceptance.

Strict rules: error events fail the run, terminal `done` is required, HTTP errors are
reported with stage/correlation data, nothing is swallowed.
"""
import argparse
import json
import subprocess
import sys
import time
import urllib.error
import urllib.request
import uuid
from pathlib import Path

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")

ROOT = Path(__file__).resolve().parent.parent
FIXTURE_WAV = ROOT / "tests" / "fixtures" / "audio" / "turn1_inquiry.wav"
OUT_DIR = ROOT / "tests" / "artifacts"


def make_webm(dst: Path) -> Path:
    subprocess.run(
        ["ffmpeg", "-y", "-loglevel", "error", "-i", str(FIXTURE_WAV),
         "-c:a", "libopus", "-b:a", "32k", "-ar", "48000", "-ac", "1", str(dst)],
        check=True,
    )
    return dst


def http(method, url, headers=None, body=None, timeout=120):
    req = urllib.request.Request(url, data=body, headers=headers or {}, method=method)
    t0 = time.perf_counter()
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return r.status, dict(r.headers), r.read(), (time.perf_counter() - t0) * 1000
    except urllib.error.HTTPError as e:
        return e.code, dict(e.headers), e.read(), (time.perf_counter() - t0) * 1000


def multipart(file_bytes, filename, content_type):
    b = f"----verify{uuid.uuid4().hex}"
    head = (f"--{b}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"{filename}\"\r\n"
            f"Content-Type: {content_type}\r\n\r\n").encode()
    return f"multipart/form-data; boundary={b}", head + file_bytes + f"\r\n--{b}--\r\n".encode()


def sse(base, headers, payload):
    status, hdrs, raw, ms = http("POST", f"{base}/api/chat/stream",
                                 {**headers, "Content-Type": "application/json"},
                                 json.dumps(payload).encode())
    if status != 200:
        return status, "", False, [], ms
    text, done, errors = "", False, []
    for line in raw.decode("utf-8").splitlines():
        if line.startswith("data: "):
            ev = json.loads(line[6:])
            if ev["type"] == "token":
                text += ev.get("content") or ""
            elif ev["type"] == "done":
                done = True
            elif ev["type"] == "error":
                errors.append(ev)
    return status, text, done, errors, ms


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--base", default="http://localhost:3000", help="Next proxy (browser path)")
    ap.add_argument("--mime", default="audio/webm;codecs=opus")
    ap.add_argument("--free-text", action="append", default=[
        "السلام عليكم، حابب أعرف إيه التخصصات اللي عندكم؟",
        "لو سمحت عايز أغيّر ميعاد كشفي من بكرة لبعده",
    ])
    args = ap.parse_args()

    OUT_DIR.mkdir(parents=True, exist_ok=True)
    webm = make_webm(OUT_DIR / "verify_prerecorded.webm")
    results, failed = [], False

    def rec(name, ok, **info):
        nonlocal failed
        failed |= not ok
        results.append({"check": name, "ok": ok, **info})
        print(f"[{'PASS' if ok else 'FAIL'}] {name} {json.dumps(info, ensure_ascii=False)}")

    # fresh session via proxy
    st, _, raw, ms = http("POST", f"{args.base}/api/voice/session",
                          {"Content-Type": "application/json"}, b"{}")
    sess = json.loads(raw) if st == 200 else {}
    rec("session.fresh", st == 200 and bool(sess.get("customerToken")), status=st, ms=round(ms))
    if st != 200:
        sys.exit(1)
    auth = {"X-Customer-Token": sess["customerToken"], "X-Conversation-Id": sess["conversationId"]}

    # STT with the exact content-type shape browsers send
    ctype, body = multipart(webm.read_bytes(), "mic_recording.webm", args.mime)
    st, _, raw, ms = http("POST", f"{args.base}/api/voice/stt", {**auth, "Content-Type": ctype}, body)
    stt_text = json.loads(raw).get("text", "") if st == 200 else ""
    rec("stt.webm_opus_via_proxy", st == 200 and bool(stt_text.strip()), status=st, ms=round(ms),
        mime=args.mime, body=(raw[:200].decode("utf-8", "replace") if st != 200 else None),
        transcript_chars=len(stt_text))

    # chat with STT output + new free-form inputs; strict: no error event, terminal done
    for label, msg in [("stt_text", stt_text)] + [("free", t) for t in args.free_text]:
        if not msg.strip():
            continue
        st, text, done, errs, ms = sse(args.base, auth, {"conversationId": sess["conversationId"], "message": msg})
        rec(f"chat.{label}", st == 200 and done and not errs and bool(text.strip()),
            status=st, ms=round(ms), done=done, errors=errs, reply_chars=len(text))

    # voice turn: text path, then audio path (server-side STT of webm)
    turn_headers = {**auth, "Content-Type": "application/json"}
    st, _, raw, ms = http("POST", f"{args.base}/api/voice/turn", turn_headers, json.dumps({
        "sessionId": sess["sessionId"], "conversationId": sess["conversationId"],
        "message": "عايز أعرف مواعيد الكشف المتاحة"}).encode(), timeout=180)
    d = json.loads(raw) if st == 200 else {}
    rec("turn.text", st == 200 and bool(d.get("audioBase64")) and bool(d.get("text")),
        status=st, ms=round(ms), body=(raw[:300].decode("utf-8", "replace") if st != 200 else None))

    import base64
    st, _, raw, ms = http("POST", f"{args.base}/api/voice/turn", turn_headers, json.dumps({
        "sessionId": sess["sessionId"], "conversationId": sess["conversationId"],
        "audioBase64": base64.b64encode(webm.read_bytes()).decode(), "mimeType": args.mime}).encode(), timeout=180)
    d = json.loads(raw) if st == 200 else {}
    rec("turn.audio_webm", st == 200 and bool(d.get("audioBase64")),
        status=st, ms=round(ms), body=(raw[:300].decode("utf-8", "replace") if st != 200 else None))

    report = OUT_DIR / "verify_webm_pipeline_report.json"
    report.write_text(json.dumps({"evidence": "prerecorded-webm-via-proxy (not physical mic)",
                                  "base": args.base, "results": results}, ensure_ascii=False, indent=2),
                      encoding="utf-8")
    print(f"report: {report}")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
