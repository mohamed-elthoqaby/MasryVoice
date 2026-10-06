# MasryVoice (صوت مصري)

**MasryVoice** is an enterprise-grade Egyptian Arabic AI Voice Agent platform designed for real-time customer conversations, automated clinic/business appointment bookings, vector-based semantic knowledge retrieval (RAG), and telephony integration via Asterisk AudioSocket.

The platform is architected as a clean modular monolith adhering to a zero-cost local development model ($0 Cloud Spend) with local Ollama LLMs, pgvector in PostgreSQL, WebRTC/WebAudio browser streaming, and strict server-enforced customer trust boundaries.

---

## 🏛️ System Architecture

- **Backend:** .NET 10 Minimal APIs modular monolith (`backend/MasryVoice.Api`).
- **Database & Vectors:** PostgreSQL 16 with `pgvector` extension for 384-dimensional vector embeddings and ACID-safe booking reservation.
- **Frontend Dashboard:** Next.js 14+ React interface (`frontend`).
- **Inference Pipeline:** Local Ollama (`qwen2.5:3b` / `qwen2.5:1.5b`) with bounded concurrency admission control (`InferenceThrottlingManager`).
- **Voice Pipeline:** Egyptian Arabic STT adapter, 16kHz WAV TTS synthesizer, barge-in interruption handling, and Asterisk AudioSocket (TCP port 9092).
- **Automation & Recovery:** Transactional Outbox pattern (`OutboxJobs`) with background processor and dead-letter queue.
- **Acceptance Testing:** Automated Postman CLI v2.1.0 acceptance test suite with machine-readable JSON reports.

---

## 📋 Prerequisites

1. **Docker & Docker Compose** (for PostgreSQL with `pgvector`).
2. **.NET SDK** (.NET 10.0 or .NET 9.0).
3. **Node.js** (v18+ or v20+ with npm).
4. **Ollama** (running locally on port 11434 with models `qwen2.5:3b` and `nomic-embed-text`).
5. **Postman CLI / Newman** (optional, for running API acceptance test suite).

---

## 🚀 Getting Started

### 1. Start Database Container (PostgreSQL + pgvector)

```bash
docker compose up -d
```

### 2. Configure Local Settings

Create `backend/MasryVoice.Api/appsettings.Development.json` (or set environment variables):

```json
{
  "ConnectionStrings": {
    "PostgreSql": "Host=localhost;Port=5432;Database=masryvoice_db;Username=masryvoice;Password=masryvoice_secret_pass"
  },
  "Security": {
    "AdminKey": "your_secure_admin_key_here",
    "HmacSecret": "your_customer_hmac_secret_here"
  }
}
```

### 3. Run Backend API

```bash
dotnet run --project backend/MasryVoice.Api/MasryVoice.Api.csproj
```
The API will start at `http://localhost:5000` (Swagger / Health check at `http://localhost:5000/api/health`).

### 4. Run Frontend

```bash
cd frontend
npm install
npm run dev
```
Open `http://localhost:3000` in your browser.

---

## 🧪 Testing & Verification

### Running Acceptance Tests via Postman CLI

Run the full acceptance test suite using the Postman CLI runner:

```powershell
powershell -ExecutionPolicy Bypass -File tests/postman/run-all-tests.ps1
```

Or execute Postman CLI directly:

```bash
postman collection run tests/postman/MasryVoice.postman_collection.json \
  -e tests/postman/MasryVoice.postman_environment.json \
  -r cli,json \
  --reporter-json-export tests/postman/reports/postman-acceptance.json
```

### Running .NET Unit & Integration Tests

```bash
dotnet test tests/MasryVoice.Tests/MasryVoice.Tests.csproj
```

### Running Sustained Concurrent Load Benchmark

```bash
node tests/postman/run-concurrent-scenarios.js
```

---

## 🔒 Security & Trust Boundaries

1. **Customer Confirmation Trust Boundary:** LLMs cannot confirm bookings directly. The model only stages a pending booking (`PendingBooking`). The customer must explicitly trigger confirmation from the client, which commits via an atomic PostgreSQL transaction.
2. **Idempotency & Replay Protection:** Confirmations require unique idempotency keys. Repeated confirmation returns the original booking without duplicating or consuming additional slot capacity.
3. **Cross-User Isolation:** Customer tokens are cryptographically signed using HMAC-SHA256, strictly isolating booking and conversation history between users.
4. **Prompt Injection Defense:** Knowledge queries are sanitized and checked against adversarial patterns (e.g., `ignore previous instructions`, `delete bookings`) before performing vector similarity search.

---

## 📄 License

Proprietary / Private — All rights reserved.
