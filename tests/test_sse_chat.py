import urllib.request
import json
import sys

url = "http://localhost:5000/api/chat/stream"
payload = {
    "agentId": "11111111-1111-1111-1111-111111111111",
    "conversationId": "44444444-4444-4444-4444-444444444444",
    "message": "عايز أعرف إيه المواعيد المتاحة بكرة لكشف الباطنة؟"
}

data = json.dumps(payload).encode("utf-8")
req = urllib.request.Request(url, data=data, headers={"Content-Type": "application/json"})

print("Sending Egyptian Arabic prompt to /api/chat/stream ...")
with urllib.request.urlopen(req) as response:
    for line in response:
        line_decoded = line.decode("utf-8").strip()
        if line_decoded.startswith("data: "):
            chunk_json = line_decoded[6:]
            try:
                event = json.loads(chunk_json)
                event_type = event.get("type")
                if event_type == "token":
                    print(event.get("content", ""), end="", flush=True)
                elif event_type == "tool_call":
                    print(f"\n[TOOL CALL]: {event.get('content')}")
                elif event_type == "tool_result":
                    print(f"\n[TOOL RESULT]: {json.dumps(event.get('metadata'), ensure_ascii=False)}")
                elif event_type == "done":
                    print(f"\n[COMPLETED]: {event.get('content')}")
            except Exception as e:
                pass
print("\nStream test completed successfully.")
