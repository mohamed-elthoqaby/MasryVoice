$models = @("qwen2.5:1.5b", "qwen2.5:3b")

foreach ($m in $models) {
    Write-Host "================ Testing Model: $m ================"
    $body = @{
        model = $m
        messages = @(
            @{ role = "user"; content = "أهلاً، إيه مواعيد الكشف المتاحة؟" }
        )
        stream = $false
    } | ConvertTo-Json

    $resp = Invoke-RestMethod -Uri "http://127.0.0.1:11434/api/chat" -Method Post -Body $body -ContentType "application/json"
    
    $evalCount = $resp.eval_count
    $evalSec = $resp.eval_duration / 1e9
    $promptSec = $resp.prompt_eval_duration / 1e9
    $totalSec = $resp.total_duration / 1e9
    $pureTps = if ($evalSec -gt 0) { [Math]::Round($evalCount / $evalSec, 2) } else { 0 }
    $wallTps = if ($totalSec -gt 0) { [Math]::Round($evalCount / $totalSec, 2) } else { 0 }

    Write-Host "Prompt Eval Duration:" ([Math]::Round($promptSec, 3)) "s"
    Write-Host "Eval Count (Tokens):" $evalCount
    Write-Host "Eval Duration:" ([Math]::Round($evalSec, 3)) "s"
    Write-Host "Pure Generation TPS (eval_count / eval_duration):" $pureTps "tokens/s"
    Write-Host "Total Duration (Wall-clock):" ([Math]::Round($totalSec, 3)) "s"
    Write-Host "Wall-Clock TPS (eval_count / total_duration):" $wallTps "tokens/s"
    Write-Host "Response text sample:" ($resp.message.content.Substring(0, [Math]::Min(100, $resp.message.content.Length)))
    Write-Host ""
}
