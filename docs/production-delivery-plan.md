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
| **Phase C** | Knowledge / RAG, Durable Outbox & Reminders | **COMPLETED & LOCALLY_VERIFIED** | `PhaseCKnowledgeAndOutboxTests.cs` |
| **Phase D** | Integrated Voice Pipeline & Telephony Bridge | **COMPLETED & LOCALLY_VERIFIED** | `verify_webm_pipeline.py`, `ProductionFeatureTests.cs`, `smoke.test.js` |
| **Phase E** | External Integrations (M365, WhatsApp, Telegram) | **COMPLETED & LOCALLY_VERIFIED (Mocks) / EXTERNALLY_BLOCKED** | `PhaseEIntegrationContractTests.cs`, `GET /api/integrations/status` |
| **Phase F** | Production Hardening, Backup & Capacity Benchmarks | **COMPLETED & LOCALLY_VERIFIED** | `SchemaMigrationAcceptanceTests.cs`, `backup_restore.py`, `phaseF-capacity-benchmark.json` |

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
- [x] **C.1 Clinic Knowledge Search**: Semantic vector search with pgvector (cosine distance) and SQLite lexical fallback; prompt injection sanitization; `<verified_clinic_knowledge>` grounding tags in tool results.  
  *Status*: `LOCALLY_VERIFIED` (`PhaseCKnowledgeAndOutboxTests.cs`)
- [x] **C.2 Durable Outbox Processor**: Reliable background queue with exponential backoff and dead-letter handling for notifications (`BookingConfirmed`, `BookingCancelled`, `BookingReminder`).  
  *Status*: `LOCALLY_VERIFIED` (`PhaseCKnowledgeAndOutboxTests.cs`)
- [x] **C.3 Appointment Reminders**: Automated reminder scheduler scanning confirmed bookings in next 24h and enqueuing non-duplicated reminder outbox jobs.  
  *Status*: `LOCALLY_VERIFIED` (`PhaseCKnowledgeAndOutboxTests.cs`)

### Phase D: Integrated Voice Pipeline & Telephony Bridge
- [x] **D.1 Real Local Whisper STT Integration**: FastAPI Whisper on port 8000 handling browser WebM audio.  
  *Status*: `LOCALLY_VERIFIED` (`scripts/voice_server.py`, `scripts/verify_webm_pipeline.py`)
- [x] **D.2 Real Local Piper TTS Integration**: FastAPI Piper synthesizing Egyptian Arabic audio.  
  *Status*: `LOCALLY_VERIFIED` (`scripts/voice_server.py`, `scripts/verify_webm_pipeline.py`)
- [x] **D.3 Browser UI Microphone Recording & PTT**: Chrome MediaRecorder WebM/Opus stream upload with live visualizer and error banner.  
  *Status*: `LOCALLY_VERIFIED` (`frontend/src/app/page.tsx`, `tests/browser/smoke.test.js`)
- [x] **D.4 Voice Interruption & Turn Cancellation**: Abort active synthesis/streaming when user speaks or clicks Interrupt (HTTP 499 handling and `/api/voice/interrupt`).  
  *Status*: `LOCALLY_VERIFIED` (`VoiceSessionManager.cs`, `ProductionFeatureTests.cs`, `tests/browser/smoke.test.js`)
- [x] **D.5 Telephony Asterisk AudioSocket Service**: AudioSocket TCP bridge on port 9092 for Asterisk PBX inbound calls.  
  *Status*: `LOCALLY_VERIFIED` (`AsteriskAudioSocketService.cs`, `ProductionFeatureTests.cs`)

### Phase E: External Integrations ($0 Cost Contracts & Local Mocks)
- [x] **E.1 Microsoft 365 Calendar Provider**: Contract interface `ICalendarIntegrationService` + `MockCalendarIntegrationService` + `Microsoft365CalendarService` with Graph API client.  
  *Status*: `LOCALLY_VERIFIED (Mock Contracts)` / `EXTERNALLY_BLOCKED` (Requires Azure AD tenant & application credentials; verified via mock).
- [x] **E.2 WhatsApp Business Messaging Provider**: Contract interface `IWhatsAppMessagingService` + `MockWhatsAppMessagingService` + `WhatsAppCloudApiService` with HMAC-SHA256 signature verification and webhook handlers.  
  *Status*: `LOCALLY_VERIFIED (Mock Contracts)` / `EXTERNALLY_BLOCKED` (Requires Meta Business Cloud API account; verified via mock).
- [x] **E.3 Telegram Bot Provider**: Contract interface `ITelegramMessagingService` + `MockTelegramMessagingService` + `TelegramBotService` with secret token verification and clinic staff notifications.  
  *Status*: `LOCALLY_VERIFIED (Mock Contracts)` / `EXTERNALLY_BLOCKED` (Requires Telegram Bot Token; verified via mock).
- [x] **E.4 Multi-Channel Dispatch Coordinator & Outbox Dispatch**: `IntegrationDispatchCoordinator` synchronizes calendar appointments, patient WhatsApp notices, and staff Telegram alerts with failure backoff in `DurableOutboxProcessor`.  
  *Status*: `LOCALLY_VERIFIED` (`PhaseEIntegrationContractTests.cs`)
- [x] **E.5 Status & Webhook Endpoints**: `GET /api/integrations/status`, `GET/POST /api/integrations/whatsapp/webhook`, `POST /api/integrations/telegram/webhook`.  
  *Status*: `LOCALLY_VERIFIED` (`PhaseEIntegrationContractTests.cs`)

### Phase F: Production Hardening, Backup & Capacity Benchmarks
- [x] **F.1 PostgreSQL Migration & Schema Parity**: Migration helper supporting SQLite and PostgreSQL with schema integrity validation and baseline recovery.  
  *Status*: `LOCALLY_VERIFIED` (`SchemaMigrationAcceptanceTests.cs`, `DatabaseMigrationHelper.cs`)
- [x] **F.2 Backup & Restore Automation**: `scripts/backup_restore.py` with WAL checkpointing, SHA256 manifests, atomic restore, and integrity checks.  
  *Status*: `LOCALLY_VERIFIED` (`scripts/backup_restore.py --self-test`)
- [x] **F.3 Asterisk PBX Docker / Configuration Templates**: Production templates in `deploy/asterisk/` (`pjsip.conf`, `extensions.conf`, `audiosocket.conf`, `Dockerfile`) and compose profile.  
  *Status*: `LOCALLY_VERIFIED` (`deploy/asterisk/`, `docker-compose.yml`)
- [x] **F.4 Concurrency & Latency Stress Test**: Automated capacity benchmark harness `scripts/capacity_benchmark.py` measuring throughput and latency percentiles (p50, p90, p95, p99).  
  *Status*: `LOCALLY_VERIFIED` (`docs/evidence/phaseF-capacity-benchmark.json`)
