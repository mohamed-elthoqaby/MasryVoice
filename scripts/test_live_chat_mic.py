import urllib.request
import json
import uuid
import sys

if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8')

def test_flow():
    print("[1] Requesting fresh session credentials from POST /api/voice/session...")
    req = urllib.request.Request(
        "http://127.0.0.1:5000/api/voice/session",
        data=b"{}",
        headers={"Content-Type": "application/json"},
        method="POST"
    )
    with urllib.request.urlopen(req) as resp:
        session_data = json.loads(resp.read().decode("utf-8"))
        print("    Session successfully created:")
        print(f"    - ConversationId: {session_data.get('conversationId')}")
        print(f"    - CustomerToken: {session_data.get('customerToken')[:16]}... (HMAC-signed)")
        conv_id = session_data["conversationId"]
        token = session_data["customerToken"]

    print("\n[2] Testing authenticated STT with fresh session credentials...")
    with open("tests/fixtures/audio/turn1_inquiry.wav", "rb") as f:
        wav_bytes = f.read()

    boundary = f"----WebKitFormBoundary{uuid.uuid4().hex}"
    header_part = (
        f"--{boundary}\r\n"
        f'Content-Disposition: form-data; name="file"; filename="turn1.wav"\r\n'
        f"Content-Type: audio/wav\r\n\r\n"
    ).encode("utf-8")
    footer_part = f"\r\n--{boundary}--\r\n".encode("utf-8")
    body = header_part + wav_bytes + footer_part

    stt_req = urllib.request.Request(
        "http://127.0.0.1:5000/api/voice/stt",
        data=body,
        headers={
            "Content-Type": f"multipart/form-data; boundary={boundary}",
            "X-Customer-Token": token,
            "X-Conversation-Id": conv_id,
        },
        method="POST"
    )
    with urllib.request.urlopen(stt_req) as resp:
        stt_resp = json.loads(resp.read().decode("utf-8"))
        print(f"    STT Result (HTTP {resp.status}): {stt_resp}")
        assert "text" in stt_resp, "Expected text field in STT response"

    print("\n[3] Testing free-form conversational queries against /api/chat/stream...")
    queries = [
        "صباح الخير، إيه مواعيد عمل العيادة وعنوانها؟", # General inquiry (no tools needed)
        "هو كشف العظام بكام؟",                         # General question
        "عايز أحجز كشف باطنة بكرة الصبح"                # Booking inquiry (tools may be needed)
    ]

    for q in queries:
        print(f"\n--- Testing Query: '{q}' ---")
        chat_req = urllib.request.Request(
            "http://127.0.0.1:5000/api/chat/stream",
            data=json.dumps({"message": q, "conversationId": conv_id}).encode("utf-8"),
            headers={
                "Content-Type": "application/json",
                "X-Customer-Token": token,
                "X-Conversation-Id": conv_id
            },
            method="POST"
        )
        full_reply = ""
        done = False
        with urllib.request.urlopen(chat_req) as resp:
            for raw_line in resp:
                line = raw_line.decode("utf-8").strip()
                if not line:
                    continue
                if line.startswith("data: "):
                    payload = json.loads(line[6:])
                    t = payload.get("type")
                    if t == "token":
                        full_reply += payload.get("content") or payload.get("token", "")
                    elif t == "done":
                        done = True
                    elif t == "error":
                        print(f"    [ERROR event]: {payload}")
        print(f"    Agent Response: {full_reply.strip()}")
        print(f"    Stream Completed Successfully: {done}")
        assert len(full_reply.strip()) > 0, f"Expected non-empty response for query: {q}"
    print("\n[4] Testing POST /api/voice/turn with text input...")
    voice_turn_req = urllib.request.Request(
        "http://127.0.0.1:5000/api/voice/turn",
        data=json.dumps({
            "sessionId": session_data["sessionId"],
            "conversationId": conv_id,
            "agentId": "11111111-1111-1111-1111-111111111111",
            "message": "عايز أعرف مواعيد عمل العيادة"
        }).encode("utf-8"),
        headers={
            "Content-Type": "application/json",
            "X-Customer-Token": token,
            "X-Conversation-Id": conv_id
        },
        method="POST"
    )
    with urllib.request.urlopen(voice_turn_req) as resp:
        turn_resp = json.loads(resp.read().decode("utf-8"))
        print(f"    Voice Turn Result (HTTP {resp.status}):")
        print(f"    - ResponseText: {turn_resp.get('text')}")
        print(f"    - AudioBase64 Length: {len(turn_resp.get('audioBase64') or '')} chars")
        assert len(turn_resp.get("text", "")) > 0, "Expected non-empty text"
        assert len(turn_resp.get("audioBase64", "")) > 0, "Expected non-empty audioBase64"

    print("\n[ALL CHECKS PASSED] Fresh session auth, STT, free-form streaming chat, and Voice Turn work perfectly!")

if __name__ == "__main__":
    test_flow()
