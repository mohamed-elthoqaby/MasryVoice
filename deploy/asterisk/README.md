# MasryVoice Asterisk PBX & Telephony Integration

This directory contains containerized production deployment templates for connecting Asterisk PBX to MasryVoice via the **AudioSocket** protocol.

## Architecture

```
PSTN / Mobile Callers
       │
       ▼ (SIP / RTP)
   [Asterisk 20 PBX]
       │
       ▼ (TCP AudioSocket Port 9092)
[MasryVoice AsteriskAudioSocketService]
       │
       ├─► Whisper STT (FastAPI Port 8000)
       ├─► Agent Orchestrator & Booking Tools
       └─► Piper Egyptian Arabic TTS (Port 8000)
```

## Configuration Files

- `pjsip.conf`: SIP trunk registration and inbound endpoint configuration.
- `extensions.conf`: Dialplan routing inbound calls directly to `AudioSocket(${AUDIOSOCKET_UUID},backend:9092)`.
- `audiosocket.conf`: AudioSocket linear PCM (16-bit 8kHz) audio parameters.
- `Dockerfile`: Debian Bookworm Asterisk 20 image.

## Production Credentials & Blocker

To receive actual telephone calls in Egypt:
- **Required**: An active SIP Trunk account with an Egyptian DID number (e.g., from Telecom Egypt, Zadarma, or Twilio) or a local VoIP GSM Gateway (e.g., Dinstar, Yeastar) connected on the clinic local network.
- **Local Verification**: The internal AudioSocket protocol listener is fully implemented and tested locally on port 9092 (`AsteriskAudioSocketService.cs`, `ProductionFeatureTests.cs`).
