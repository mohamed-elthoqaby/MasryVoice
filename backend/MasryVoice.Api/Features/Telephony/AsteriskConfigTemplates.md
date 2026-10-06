# Asterisk & SIP Trunk Integration Configuration for MasryVoice

This document specifies the exact Asterisk 20+ configuration files to connect external telephone calls (PSTN / Mobile numbers) to MasryVoice via the **AudioSocket** protocol.

---

### 1. `pjsip.conf` (SIP Trunk Provider Configuration)

```ini
; ====================================================================
; MasryVoice Inbound SIP Trunk Configuration (e.g., Zadarma, Twilio, or Local PBX)
; ====================================================================

[transport-udp]
type=transport
protocol=udp
bind=0.0.0.0:5060

; Registration with SIP Provider
[masryvoice-reg]
type=registration
outbound_auth=masryvoice-auth
server_uri=sip:sip.provider.com
client_uri=sip:USERNAME@sip.provider.com
retry_interval=60

[masryvoice-auth]
type=auth
auth_type=userpass
username=USERNAME
password=SECRET_PASSWORD

[masryvoice-endpoint]
type=endpoint
context=masryvoice-inbound
disallow=all
allow=alaw,ulaw,g722
aors=masryvoice-aor
auth=masryvoice-auth

[masryvoice-aor]
type=aor
contact=sip:sip.provider.com
```

---

### 2. `extensions.conf` (Dialplan Routing to AudioSocket)

```ini
; ====================================================================
; Inbound Dialplan - Routes callers directly to MasryVoice Backend
; ====================================================================

[masryvoice-inbound]
; Route incoming call on DID 20233445566 (Cairo Landline / Clinic Number)
exten => _.,1,NoOp(Incoming MasryVoice Call from ${CALLERID(num)})
 same => n,Answer()
 same => n,Wait(1)
 ; AudioSocket connects to MasryVoice Backend on port 9092
 ; UUID can be generated dynamically using ${CHANNEL(uniqueid)}
 same => n,AudioSocket(${CHANNEL(uniqueid)},127.0.0.1:9092)
 same => n,Hangup()
```

---

### 3. Verification & Blocker Status

| Element | Status | Notes |
| :--- | :--- | :--- |
| **AudioSocket TCP Listener** | **Implemented & Verified Locally** | Listens on port 9092; handles frame parsing, audio decoding, and lifecycle. |
| **Local Mock Telephony Test** | **Verified Locally** | Verified via synthetic socket connection test. |
| **Live SIP Trunk / Real Telephone Call** | **Explicitly Blocked** | **Blocker:** Requires active SIP trunk credentials or a physical GSM/VoIP gateway with a registered Egyptian DID phone number. |
