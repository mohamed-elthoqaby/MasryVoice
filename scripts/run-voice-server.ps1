param(
    [int]$Port = 8000,
    [string]$WhisperModel = "base",
    [string]$Voice = "ar_JO-kareem-low"
)

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$RootDir = Resolve-Path (Join-Path $ScriptDir "..")
$VenvPython = Join-Path $RootDir ".venv_voice\Scripts\python.exe"
$PiperModelDir = Join-Path $RootDir "models\piper\ar_JO-kareem-low"
$PiperOnnx = Join-Path $PiperModelDir "ar_JO-kareem-low.onnx"

if (-not (Test-Path $VenvPython)) {
    Write-Error "Voice virtual environment python not found at $VenvPython! Please run setup."
    exit 1
}

if (-not (Test-Path $PiperOnnx)) {
    Write-Warning "Local Piper weights not found at $PiperOnnx. Downloading offline weights..."
    & "$VenvPython" -c "
import urllib.request, os
os.makedirs('$PiperModelDir'.replace('\', '/'), exist_ok=True)
base = 'https://huggingface.co/rhasspy/piper-voices/resolve/main/ar/ar_JO/kareem/low/'
for fname in ['ar_JO-kareem-low.onnx', 'ar_JO-kareem-low.onnx.json', 'MODEL_CARD']:
    url = base + fname
    dest = os.path.join('$PiperModelDir'.replace('\', '/'), fname)
    if not os.path.exists(dest):
        urllib.request.urlretrieve(url, dest)
"
}

$env:VOICE_PORT = "$Port"
$env:WHISPER_MODEL = "$WhisperModel"
$env:TTS_VOICE = "$Voice"
$env:PYTHONIOENCODING = "utf-8"

Write-Host "================================================================"
Write-Host "     MASRYVOICE 100% LOCAL OFFLINE SPEECH SERVER ($0 Cost)      "
Write-Host "================================================================"
Write-Host "Port:          http://127.0.0.1:$Port"
Write-Host "STT Engine:    faster-whisper int8 CPU (local weights)"
Write-Host "TTS Engine:    Piper ONNX CPU (16kHz WAV, $Voice)"
Write-Host "Network:       100% Offline / Local Loopback Only"
Write-Host "================================================================"

& "$VenvPython" -m uvicorn "scripts.voice_server:app" --host 127.0.0.1 --port $Port
