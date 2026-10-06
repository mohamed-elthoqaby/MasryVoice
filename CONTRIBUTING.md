# MasryVoice Contributor & CI Merge Gate Instructions

All code changes to MasryVoice must be verified by automated continuous integration before merging into `main`. The `main` branch is protected by enforceable branch protection rules and cannot be updated directly or with failing checks.

---

## 1. Core Workflow Overview

```
Feature / Bugfix Branch ───► Pull Request ───► GitHub Actions CI ───► Merge into main
```

1. **Branch**: Create a descriptive branch from `main`. Never push directly to `main`.
   ```bash
   git checkout main
   git pull origin main
   git checkout -b feature/your-feature-name
   ```

2. **Develop & Verify Locally**:
   - Backend tests:
     ```bash
     dotnet test tests/MasryVoice.Tests/MasryVoice.Tests.csproj -c Release
     ```
   - Frontend verification:
     ```bash
     cd frontend/dashboard
     npm run build
     ```
   - Acceptance collection (API running):
     ```bash
     npm run test:acceptance # or run tests/postman/run-all-tests.ps1
     ```

3. **Commit & Push**:
   ```bash
   git add .
   git commit -m "feat(scope): concise description"
   git push -u origin feature/your-feature-name
   ```

4. **Open a Pull Request**:
   - Target branch: `main`.
   - Provide a clear title and description explaining what was changed and why.

5. **Pass Required CI Checks**:
   The pull request triggers GitHub Actions CI. Merging is automatically blocked until all required checks succeed:
   - `Backend Build & Deterministic Tests`
   - `PostgreSQL & pgvector Integration Tests`
   - `Postman CLI API Acceptance Tests`
   - `Frontend TypeScript & Production Build`
   - `CI Gate` (final aggregate status gate)

6. **Branch Up-To-Date Requirement**:
   Your branch must be up to date with the latest `main`. If `main` changes while your PR is open, update your branch:
   ```bash
   git fetch origin
   git merge origin/main
   git push origin feature/your-feature-name
   ```

7. **Merge**:
   Once all required checks pass and the branch is up to date, merge via squash or rebase:
   ```bash
   gh pr merge <PR_NUMBER> --squash --delete-branch
   ```

---

## 2. Enforced Protection Policies on `main`

The `main` branch strictly enforces:
- **Pull Requests Required**: Direct pushes to `main` are blocked.
- **Status Checks Required**: All 5 CI checks (including `CI Gate`) must pass on the latest revision.
- **Strict Up-To-Date Policy**: Branches must be synchronized with `main` before merging.
- **Administrator Enforcement (`enforce_admins: true`)**: Administrators cannot bypass required checks.
- **Force Push Protection**: Force pushes (`git push --force`) are blocked.
- **Deletion Protection**: Deleting the `main` branch is blocked.

---

## 3. Workflows Separation

- **`MasryVoice CI` (`ci.yml`)**: Automated, fast (<2 min), deterministic Linux runner workflow using disposable PostgreSQL + pgvector services and a labeled `DeterministicFake` LLM provider for zero-cost repeatable CI.
- **`Ollama & Egyptian Arabic Speech Benchmarks` (`ollama-voice-benchmarks.yml`)**: Manual `workflow_dispatch` workflow for local GPU/CPU hardware verification with real Ollama models and Whisper/Sherpa ONNX models. Real-model tests are never counted as fake tests or run on standard headless CI runners.
