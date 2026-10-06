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

# 2. Run Postman CLI
Write-Host "`n[2/3] Executing Postman Acceptance Test Suite via Postman CLI..."
$postmanCmd = "collection run `"$CollectionPath`" -e `"$EnvPath`" -r cli,json --reporter-json-export `"$PostmanReportPath`""
$p = Start-Process -FilePath "cmd.exe" -ArgumentList "/c postman $postmanCmd" -NoNewWindow -Wait -PassThru
$postmanExit = $p.ExitCode

if ($postmanExit -ne 0) {
    Write-Host "Postman CLI acceptance tests failed with exit code $postmanExit."
    exit $postmanExit
}
else {
    Write-Host "Postman CLI Acceptance Tests: 100% Passed!"
    Write-Host "Local JSON report exported to: $PostmanReportPath"
}

# 3. Newman Export
Write-Host "`nGenerating supplementary Newman JSON report to: $NewmanReportPath..."
$newmanCmd = "run `"$CollectionPath`" -e `"$EnvPath`" -r json --reporter-json-export `"$NewmanReportPath`" --silent"
$np = Start-Process -FilePath "cmd.exe" -ArgumentList "/c npx -y newman $newmanCmd" -NoNewWindow -Wait -PassThru
if ($np.ExitCode -eq 0) {
    Write-Host "Newman verification report exported successfully."
}
else {
    Write-Host "Newman run exited with code $($np.ExitCode)"
}

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
