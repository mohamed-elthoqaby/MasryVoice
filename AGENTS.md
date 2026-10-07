# MasryVoice Engineering Guidelines for AI Agents

These guidelines establish engineering standards, verification disciplines, and operational boundaries for all agentic workflows in the MasryVoice repository.

---

## 1. Scope Discipline & Minimal Footprint
- **Implement only requested scope and strictly necessary dependencies.** Do not add unrequested frameworks, external services, or architectural rewrites.
- **Explain material scope adjustments before implementing them.** If a necessary bug fix or security remediation requires an API or schema change, declare the change and rationale first.

## 2. Grounded Verification Before Claims
- **Always inspect actual routes, configuration, database schemas, and running code before making claims.** Never assume route parameters, return types, or authentication mechanisms without checking the source code.
- **Distinguish evidence levels clearly:**
  - *Implementation:* Code written or modified.
  - *Simulated Tests:* Deterministic unit/mock tests.
  - *Real Local Verification:* Testing against actual running Kestrel/PostgreSQL/Postman instances.
  - *Target Production Verification:* Validation in real staging or target environments.
  Never claim completion for unperformed checks or unverified environments.

## 3. Explicit Provider Selection & Failure Integrity
- **Keep simulated and fake providers explicitly selected, configured, and labeled** (e.g., `LlmProvider=DeterministicFake`, `Voice:SttProvider=Simulated`).
- **Real-provider failures must never produce fabricated success.** If a real provider (Ollama, Whisper, Coqui, Asterisk) fails or is unavailable, report the exact error, timeout, or failure code. Do not fall back silently to fake success when real verification was requested.

## 4. Backend Authorization & Trust Boundaries
- **Enforce authorization and resource ownership strictly in server-side application logic and endpoints, independently of LLM output.** LLM outputs, function arguments, and tool calls are untrusted input.
- **Resource Ownership:** All access to bookings, pending drafts, conversation histories, and voice sessions requires validated ownership (verified `X-Customer-Token` HMAC-signed for the owning conversation or validated administrator credentials). A caller-supplied phone number or conversation ID alone does not establish ownership.
- **Secret Hygiene:** Production environment must reject missing or placeholder secrets (`Security:AdminKey`, `Security:HmacSecret`) at startup.

## 5. Test Rigor & Integrity
- **Never weaken assertions, disable checks, or introduce fallback behavior merely to obtain a green run.**
- **Use isolated fixtures and verify database outcomes, not only HTTP status codes.** Verify state transitions, idempotency keys, and row counts directly in the persistence layer.
- **Keep tests repeatable and clean:** Use disposable databases (`:memory:` or unique SQLite files) or isolated test schemas with automatic cleanup.
- **Targeted verification:** Run targeted checks for each specific change. Repeat full suites or broad benchmarks only when relevant changes or unresolved failures justify them.

## 6. Git, CI & Deployment Boundaries
- **Work through branches and pull requests:** Never push unreviewed changes directly to `main`. Merge only after all required CI checks and the CI Gate succeed.
- **Zero-Credential Policy:** Keep secrets, tokens, HMAC keys, customer phone numbers, session credentials, database connection strings, and certificates out of version control, terminal logs, and exported acceptance reports. Use sanitized templates and environment variables.
- **Local & Free Execution ($0 Cost):** Keep development, testing, and verification local and free. Do not deploy to public cloud infrastructure or provision paid services.

## 7. Reporting Standard
Every completed task report must include:
1. **Tested Commit SHA:** The exact Git commit tested.
2. **Commands Executed:** The exact CLI commands and test runner parameters.
3. **Actual Results:** Number of passed, failed, and skipped tests with durations.
4. **Evidence Locations:** Links to machine-readable test artifacts, CI logs, or local reports.
5. **Remaining Blockers:** Explicitly list any outstanding production gaps (e.g., real GPU STT/TTS deployment, EF Core migration automation, Egyptian Arabic semantic search validation) rather than claiming full production readiness prematurely.
