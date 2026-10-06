param(
    [string]$Model = "qwen2.5:1.5b"
)

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "Running Egyptian Arabic Model Benchmark for: $Model" -ForegroundColor Cyan
Write-Host "Hardware: Intel Core i5-12450H (8 Cores), 16 GB RAM" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

$testCases = @(
    "أهلاً بحضرتك، إيه مواعيد الكشف المتاحة بكرة في عيادة النور؟",
    "عايز أحجز كشف باطنة الساعة 2 الضهر باسم محمد عاطف تليفون 01012345678",
    "تمام أيوة، أنا بأكد الحجز بالساعة 2 الضهر",
    "أنا عايز ألغي الحجز أو أسأل عن كشف الأسنان",
    "هو الدكتور موجود النهاردة بالليل بعد الساعة 8؟"
)

$i = 1
$totalTokens = 0
$totalDurationSec = 0

foreach ($prompt in $testCases) {
    Write-Host "`n[Test $i] Prompt: $prompt" -ForegroundColor Yellow
    $body = @{
        model = $Model
        messages = @(
            @{
                role = "system"
                content = "أنتِ سارة، مساعدة عيادة النور في القاهرة. تتحدثين باللهجة المصرية العامية المهذبة فقط وبإيجاز شديد."
            },
            @{
                role = "user"
                content = $prompt
            }
        )
        stream = $false
        options = @{
            temperature = 0.2
            num_predict = 150
        }
    } | ConvertTo-Json -Depth 5

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $res = Invoke-RestMethod -Uri "http://127.0.0.1:11434/api/chat" -Method Post -Body $body -ContentType "application/json"
        $sw.Stop()

        $evalCount = $res.eval_count
        $evalDurationSec = $res.eval_duration / 1000000000.0
        $tps = if ($evalDurationSec -gt 0) { [math]::Round($evalCount / $evalDurationSec, 2) } else { 0 }
        $totalMs = [math]::Round($sw.Elapsed.TotalMilliseconds, 0)

        Write-Host "Response: $($res.message.content)" -ForegroundColor Green
        Write-Host "Metrics: Total: ${totalMs}ms | Generated Tokens: $evalCount | Speed: $tps tokens/sec" -ForegroundColor Gray

        $totalTokens += $evalCount
        $totalDurationSec += $evalDurationSec
    }
    catch {
        Write-Host "Error during benchmark: $_" -ForegroundColor Red
    }
    $i++
}

if ($totalDurationSec -gt 0) {
    $avgTps = [math]::Round($totalTokens / $totalDurationSec, 2)
    Write-Host "`n==========================================================" -ForegroundColor Cyan
    Write-Host "Benchmark Summary: Model $Model | Average Speed: $avgTps tokens/sec" -ForegroundColor Cyan
    Write-Host "==========================================================" -ForegroundColor Cyan
}
