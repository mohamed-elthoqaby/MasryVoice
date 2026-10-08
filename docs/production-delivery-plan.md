# MasryVoice Production Delivery Plan & Verification Matrix

This living document tracks the end-to-end implementation and verification status across all system phases for the MasryVoice AI Clinic Voice Agent.

In compliance with [AGENTS.md](file:///w:/AI%20Voice%20Agent/AGENTS.md):
- **Local & Free Execution ($0 Cost)**: All implementations run locally without paid cloud services.
- **Strict Evidence Levels**:
  - `LOCALLY_VERIFIED`: Verified with automated tests against running local services (Kestrel, Whisper, Piper, Ollama, SQLite).
  - `IMPLEMENTED_UNVERIFIED`: Code implemented and unit tested, awaiting end-to-end verification.
  - `EXTERNALLY_BLOCKED`: External production credentials or cloud tenant required (e.g., Azure AD, Meta Graph API, Telegram Bot Token); covered locally by contracts and mocks.

---

## Phase Status Summary

| Phase | Description | Status | Evidence Reference |
|---|---|---|---|
| **Phase A** | Operational Foundation, Transport & Safety | **COMPLETED & LOCALLY_VERIFIED** | `docs/evidence/phaseA-webm-PASSED.json` |
| **Phase B** | Agent Engine, Grounded Availability & Bookings | **COMPLETED & LOCALLY_VERIFIED** | `PhaseBBookingAndDialectTests.cs`, `AuthorizationAcceptanceTests.cs` |
| **Phase C** | Knowledge / RAG, Durable Outbox & Reminders | **IN PROGRESS** | Outbox workers + SQLite/Postgres schemas |
| **Phase D** | Integrated Voice Pipeline & Telephony Bridge | **LOCALLY_VERIFIED (Transport/Speech)** | `scripts/verify_webm_pipeline.py` |
| **Phase E** | External Integrations (M365, WhatsApp, Telegram) | **IN PROGRESS (Mock Contracts)** | Integration contract tests |
| **Phase F** | Production Hardening, Backup & Capacity Benchmarks | **PENDING** | Benchmark scripts & templates |

---

## Detailed Deliverables Matrix

### Phase A: Operational Foundation & Safety
- [x] **A.1 STT Media Type Normalization**: Strip parameter codecs (`audio/webm;codecs=opus` -> `audio/webm`) preventing Kestrel HTTP 500 (`MediaTypeHeaderValue` format error).  
  *Status*: `LOCALLY_VERIFIED` (`tests/artifacts/verify_webm_pipeline_report.json`)
- [x] **A.2 Classified Failure Envelope**: Standardized `{ code, stage, correlationId, message, retryAfterSeconds }` with proper HTTP status codes (400, 422, 429, 502, 503, 504) and `X-Correlation-Id`.  
  *Status*: `LOCALLY_VERIFIED` (`PhaseAOrchestratorAndVoiceTests.cs`)
- [x] **A.3 Orchestrator Admission Control**: Eliminated admission bypass on conversational retry; permit enforced on every LLM call; structured error emissions.  
  *Status*: `LOCALLY_VERIFIED` (`PhaseAOrchestratorAndVoiceTests.cs`)
- [x] **A.4 Voice Turn Silence Handling**: If LLM produces empty response, route falls back gracefully to clinic greeting or availability speech instead of 502 Bad Gateway.  
  *Status*: `LOCALLY_VERIFIED` (`VoiceRouteAvailabilityFallbackTests.cs`)
- [x] **A.5 Strict Pre-Recorded WebM/Opus Pipeline Verification**: Verified fresh session creation, STT audio upload, chat stream with STT transcript and free text, and voice turn synthesis.  
  *Status*: `LOCALLY_VERIFIED` (`docs/evidence/phaseA-webm-PASSED.json`)

### Phase B: Agent Engine, Grounded Availability & Booking Lifecycle
- [x] **B.1 Grounded Availability Queries**: Egyptian Arabic date/time entity resolution (`بكرة`, `بعد بكرة`, `التلات`, weekdays); specialty filter; enforce DB slot capacity check before proposing times.  
  *Status*: `LOCALLY_VERIFIED` (`PhaseBBookingAndDialectTests.cs`)
- [x] **B.2 Two-Stage Booking Confirmation**: Conversational draft creation (`StageBooking`) -> explicit confirmation card -> HMAC token validated finalization (`IBookingConfirmationService`).  
  *Status*: `LOCALLY_VERIFIED` (`VerticalSliceTests.cs`)
- [x] **B.3 Conflict & Idempotency Enforcement**: Prevent double-booking on concurrent calls using ACID conditional SQL transactions and unique constraints.  
  *Status*: `LOCALLY_VERIFIED` (`PhaseBBookingAndDialectTests.cs`, `BookingConfirmationService.cs`)
- [x] **B.4 Frontend Booking Confirmation UX**: Explicit card in Next.js UI showing patient name, phone, slot time, doctor/specialty with Confirm action and HMAC token header.  
  *Status*: `LOCALLY_VERIFIED` (`frontend/src/app/page.tsx`)
- [x] **B.5 Admin Dashboard Management & Cancellation**: Bookings list, status filter, cancellation endpoint (`POST /api/bookings/{id}/cancel`), and conversational `CancelBookingTool` with capacity decrement and outbox notifications.  
  *Status*: `LOCALLY_VERIFIED` (`AuthorizationAcceptanceTests.cs`, `PhaseBBookingAndDialectTests.cs`)

### Phase C: Knowledge/RAG, Durable Jobs & Reminders
- [ ] **C.1 Clinic Knowledge Search**: Semantic/lexical search for clinic services, operating hours, prices, and doctor profiles.
- [ ] **C.2 Durable Outbox Processor**: Reliable background queue with exponential backoff and dead-letter handling for notifications.
- [ ] **C.3 Appointment Reminders**: Scheduled reminder jobs triggered 24h and 2h prior to booked appointment slots.

### Phase D: Integrated Voice Pipeline & Telephony Bridge
- [x] **D.1 Real Local Whisper STT Integration**: FastAPI Whisper on port 8000 handling browser WebM audio.  
  *Status*: `LOCALLY_VERIFIED`
- [x] **D.2 Real Local Piper TTS Integration**: FastAPI Piper synthesizing Egyptian Arabic audio.  
  *Status*: `LOCALLY_VERIFIED`
- [ ] **D.3 Browser UI Microphone Recording & PTT**: Chrome MediaRecorder WebM/Opus stream upload with live visualizer and error banner.
- [ ] **D.4 Voice Interruption & Turn Cancellation**: Abort active synthesis/streaming when user speaks or clicks Interrupt (HTTP 499 handling).
- [ ] **D.5 Telephony Asterisk AudioSocket Service**: AudioSocket TCP bridge on port 9092 for Asterisk PBX inbound calls.

### Phase E: External Integrations ($0 Cost Contracts & Local Mocks)
- [ ] **E.1 Microsoft 365 Calendar Provider**: Contract interface + Local Mock + OAuth/Graph API client template.  
  *Status*: `EXTERNALLY_BLOCKED` (Requires Azure AD tenant & application credentials; verified via mock).
- [ ] **E.2 WhatsApp Business Messaging Provider**: Contract interface + Local Mock + Cloud API webhook handler.  
  *Status*: `EXTERNALLY_BLOCKED` (Requires Meta Business Cloud API account; verified via mock).
- [ ] **E.3 Telegram Bot Provider**: Contract interface + Local Mock + Bot API webhook handler.  
  *Status*: `EXTERNALLY_BLOCKED` (Requires Telegram Bot Token; verified via mock).

### Phase F: Production Hardening, Backup & Capacity Benchmarks
- [ ] **F.1 PostgreSQL Migration & Schema Parity**: Automated migration scripts for SQLite and PostgreSQL.
- [ ] **F.2 Backup & Restore Automation**: Database snapshot and recovery validation scripts.
- [ ] **F.3 Asterisk PBX Docker / Configuration**: Dialplan extensions and AudioSocket configuration templates.
- [ ] **F.4 Concurrency & Latency Stress Test**: Benchmark harness measuring p50/p95/p99 turn latencies under simulated concurrent calls.
