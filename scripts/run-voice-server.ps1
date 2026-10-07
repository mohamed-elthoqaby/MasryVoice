param(
    [int]$Port = 8000,
    [string]$WhisperModel = "tiny",
    [string]$Voice = "ar-EG-SalmaNeural"
)

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$RootDir = Resolve-Path (Join-Path $ScriptDir "..")
$VenvPython = Join-Path $RootDir ".venv_voice\Scripts\python.exe"

if (-not (Test-Path $VenvPython)) {
    Write-Error "Voice virtual environment python not found at $VenvPython! Run setup first."
    exit 1
}

$env:VOICE_PORT = "$Port"
$env:WHISPER_MODEL = "$WhisperModel"
$env:TTS_VOICE = "$Voice"
$env:PYTHONIOENCODING = "utf-8"

Write-Host "================================================================"
Write-Host "     STARTING MASRYVOICE LOCAL FREE SPEECH SERVER               "
Write-Host "================================================================"
Write-Host "Port:          http://127.0.0.1:$Port"
Write-Host "STT Model:     $WhisperModel (faster-whisper int8 CPU)"
Write-Host "TTS Voice:     $Voice (Egyptian Arabic)"
Write-Host "Status:        100% Local, Free, Zero-GPU Required"
Write-Host "================================================================"

& "$VenvPython" -m uvicorn "scripts.voice_server:app" --host 127.0.0.1 --port $Port
