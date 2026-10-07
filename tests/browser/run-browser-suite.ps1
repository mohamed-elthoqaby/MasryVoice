param(
    [string]$BackendUrl = "http://localhost:5000",
    [string]$FrontendUrl = "http://localhost:3000"
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$RootDir = Resolve-Path (Join-Path $ScriptDir "../..")

Write-Host "================================================================"
Write-Host "     MASRYVOICE LOCAL BROWSER SMOKE SUITE RUNNER                "
Write-Host "================================================================"

$BackendProc = $null
$FrontendProc = $null

try {
    # 1. Start Backend in Background
    Write-Host "`n[1/4] Starting MasryVoice Backend on $BackendUrl..."
    $backendEnv = @{
        "LlmProvider" = "DeterministicFake"
        "Voice__SttProvider" = "Simulated"
        "Voice__TtsProvider" = "Simulated"
        "Security__AdminKey" = "masryvoice_dev_admin_key"
        "Security__HmacSecret" = "masryvoice_dev_hmac_secret_key_12345"
        "Database__InitializeSchema" = "true"
        "Database__AutoSeed" = "true"
        "RateLimiting__ApiPermitLimit" = "500"
        "ASPNETCORE_ENVIRONMENT" = "Testing"
        "ASPNETCORE_URLS" = "$BackendUrl"
    }

    foreach ($k in $backendEnv.Keys) {
        [System.Environment]::SetEnvironmentVariable($k, $backendEnv[$k])
    }

    $outLog = Join-Path $RootDir "backend_out.log"
    $errLog = Join-Path $RootDir "backend_err.log"
    if (Test-Path $outLog) { Remove-Item $outLog -Force }
    if (Test-Path $errLog) { Remove-Item $errLog -Force }

    $exePath = Join-Path $RootDir "backend/MasryVoice.Api/bin/Release/net10.0/MasryVoice.Api.exe"
    $BackendProc = Start-Process -FilePath $exePath -WorkingDirectory "$RootDir" -RedirectStandardOutput $outLog -RedirectStandardError $errLog -PassThru

    # Poll readiness
    $ready = $false
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Seconds 1
        if ($BackendProc.HasExited) {
            $errContent = if (Test-Path $errLog) { Get-Content $errLog -Raw } else { "" }
            $outContent = if (Test-Path $outLog) { Get-Content $outLog -Raw } else { "" }
            Write-Host "Backend exited prematurely! ExitCode: $($BackendProc.ExitCode)`nSTDOUT: $outContent`nSTDERR: $errContent"
            break
        }
        try {
            $h = Invoke-RestMethod -Uri "$BackendUrl/api/health/ready" -Method Get -TimeoutSec 2
            if ($h.status -eq "Ready" -or $h.databaseConnected -eq $true) {
                if ($h.environment -ne "Testing") {
                    throw "Backend runtime environment mismatch! Expected 'Testing', got '$($h.environment)'"
                }
                $ready = $true
                break
            }
        } catch {
            if ($_.Exception.Message -like "*mismatch*") { throw $_ }
            if ($i % 5 -eq 0) { Write-Host " - Waiting for backend..." }
        }
    }

    if (-not $ready) {
        throw "Backend failed to become ready on $BackendUrl within 30 seconds."
    }
    Write-Host "Backend is READY and healthy."

    # 2. Start Frontend in Background
    Write-Host "`n[2/4] Starting MasryVoice Frontend on $FrontendUrl..."
    $frontendStartInfo = New-Object System.Diagnostics.ProcessStartInfo
    $frontendStartInfo.FileName = "cmd.exe"
    $frontendStartInfo.Arguments = "/c npm run start"
    $frontendStartInfo.WorkingDirectory = "$RootDir/frontend"
    $frontendStartInfo.UseShellExecute = $false
    $frontendStartInfo.CreateNoWindow = $true

    $FrontendProc = [System.Diagnostics.Process]::Start($frontendStartInfo)

    # Poll frontend
    $feReady = $false
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Seconds 1
        try {
            $res = Invoke-WebRequest -Uri "$FrontendUrl" -UseBasicParsing -TimeoutSec 2
            if ($res.StatusCode -eq 200) {
                $feReady = $true
                break
            }
        } catch { }
    }

    if (-not $feReady) {
        throw "Frontend failed to respond on $FrontendUrl within 30 seconds."
    }
    Write-Host "Frontend is READY and serving."

    # 3. Execute Browser Smoke Tests
    Write-Host "`n[3/4] Running Playwright Browser Smoke Test Suite..."
    $smokeScript = Join-Path $ScriptDir "smoke.test.js"
    $testProc = Start-Process -FilePath "node" -ArgumentList "`"$smokeScript`"" -NoNewWindow -Wait -PassThru
    $exitCode = $testProc.ExitCode

    if ($exitCode -ne 0) {
        Write-Host "Browser Smoke Tests FAILED with exit code $exitCode."
        exit $exitCode
    }

    Write-Host "`n[4/4] Browser Smoke Suite PASSED with 100% success!"
    exit 0
}
finally {
    Write-Host "`nCleaning up processes..."
    if ($BackendProc -and -not $BackendProc.HasExited) {
        try { Stop-Process -Id $BackendProc.Id -Force -ErrorAction SilentlyContinue } catch { }
    }
    if ($FrontendProc -and -not $FrontendProc.HasExited) {
        try { Stop-Process -Id $FrontendProc.Id -Force -ErrorAction SilentlyContinue } catch { }
    }
    # Ensure any stray child node or dotnet processes on those ports are stopped
    Get-NetTCPConnection -LocalPort 5000, 3000 -ErrorAction SilentlyContinue | ForEach-Object {
        try { Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue } catch { }
    }
}
