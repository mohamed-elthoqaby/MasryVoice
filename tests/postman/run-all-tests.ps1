param(
    [string]$BaseUrl = "http://localhost:5000",
    [string]$AdminKey = $(if ($env:MASRYVOICE_ADMIN_KEY) { $env:MASRYVOICE_ADMIN_KEY } else { "masryvoice_dev_admin_key" }),
    [switch]$SkipPerformance = $false
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$CollectionPath = Join-Path $ScriptDir "MasryVoice.postman_collection.json"
$EnvPath = Join-Path $ScriptDir "MasryVoice.postman_environment.json"
$ReportsDir = Join-Path $ScriptDir "reports"

if (-not (Test-Path $ReportsDir)) {
    New-Item -ItemType Directory -Path $ReportsDir -Force | Out-Null
}

$PostmanReportPath = Join-Path $ReportsDir "postman-acceptance.json"
$NewmanReportPath = Join-Path $ReportsDir "masryvoice-acceptance-report.json"

Write-Host "================================================================"
Write-Host "    MASRYVOICE ACCEPTANCE & CONCURRENT TEST RUNNER"
Write-Host "================================================================"

# 1. Health Check
Write-Host "`n[1/3] Verifying MasryVoice API Health at $BaseUrl..."
try {
    $health = Invoke-RestMethod -Uri "$BaseUrl/api/health" -Method Get -TimeoutSec 5
    Write-Host "API is Healthy. Database Connected: $($health.databaseConnected), Model: $($health.defaultModel)"
}
catch {
    Write-Host "API is not reachable at $BaseUrl. Please ensure the backend is running."
    exit 1
}

# 2. Run Postman CLI Twice Against Same Database (Preserve Both Reports)
$Run1ReportPath = Join-Path $ReportsDir "postman-acceptance-run1.json"
$Run2ReportPath = Join-Path $ReportsDir "postman-acceptance-run2.json"
$SanitizeScript = Join-Path $ScriptDir "sanitize-report.js"

Write-Host "`n[2/4] Executing Postman Acceptance Run 1 via Postman CLI..."
$postmanCmd1 = "collection run `"$CollectionPath`" -e `"$EnvPath`" --env-var `"adminKey=$AdminKey`" -r cli,json --reporter-json-export `"$Run1ReportPath`""
$p1 = Start-Process -FilePath "cmd.exe" -ArgumentList "/c postman $postmanCmd1" -NoNewWindow -Wait -PassThru
if ($p1.ExitCode -ne 0) {
    Write-Host "Postman CLI Run 1 failed with exit code $($p1.ExitCode)."
    exit $p1.ExitCode
}
Write-Host "Postman CLI Run 1: 100% Passed!"

Write-Host "`n[3/4] Executing Postman Acceptance Run 2 (re-verification against same isolated database without global reset)..."
$postmanCmd2 = "collection run `"$CollectionPath`" -e `"$EnvPath`" --env-var `"adminKey=$AdminKey`" -r cli,json --reporter-json-export `"$Run2ReportPath`""
$p2 = Start-Process -FilePath "cmd.exe" -ArgumentList "/c postman $postmanCmd2" -NoNewWindow -Wait -PassThru
if ($p2.ExitCode -ne 0) {
    Write-Host "Postman CLI Run 2 failed with exit code $($p2.ExitCode)."
    exit $p2.ExitCode
}
Write-Host "Postman CLI Run 2: 100% Passed!"

Write-Host "`nSanitizing Postman Acceptance Artifacts..."
node $SanitizeScript $Run1ReportPath
node $SanitizeScript $Run2ReportPath
Copy-Item $Run1ReportPath $PostmanReportPath -Force


# 4. Performance Benchmarks
if (-not $SkipPerformance) {
    Write-Host "`n[3/3] Running Sustained Concurrent Performance & Admission Benchmarks..."
    $benchScript = Join-Path $ScriptDir "run-concurrent-scenarios.js"
    $nodeP = Start-Process -FilePath "node" -ArgumentList "`"$benchScript`"" -NoNewWindow -Wait -PassThru
    if ($nodeP.ExitCode -ne 0) {
        Write-Host "Performance scenario failed with exit code $($nodeP.ExitCode)"
        exit $nodeP.ExitCode
    }
}

Write-Host "`n================================================================"
Write-Host "ALL ACCEPTANCE AND PERFORMANCE VERIFICATIONS COMPLETED SUCCESSFULLY!"
Write-Host "================================================================"
exit 0
