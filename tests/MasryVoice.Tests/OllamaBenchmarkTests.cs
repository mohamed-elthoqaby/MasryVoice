using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace MasryVoice.Tests;

/// <summary>
/// Side-by-side benchmark comparing qwen2.5:1.5b and qwen2.5:3b on real local Ollama
/// running on Intel Core i5-12450H CPU without dedicated GPU, alongside DeterministicFake baseline.
/// </summary>
public class OllamaBenchmarkTests
{
    private readonly ITestOutputHelper _output;

    public OllamaBenchmarkTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static readonly string[] TestCases = new[]
    {
        "أهلاً بحضرتك، إيه مواعيد الكشف المتاحة بكرة لكشف الباطنة؟",
        "عايز أحجز كشف باطنة الساعة 2 الضهر باسم محمد عاطف تليفون 01012345678",
        "تمام أيوة، أنا بأكد الحجز بالساعة 2 الضهر",
        "أنا عايز ألغي الحجز أو أسأل عن كشف الأسنان",
        "هو الدكتور موجود النهاردة بالليل بعد الساعة 8؟"
    };

    private record BenchmarkModelResult(
        string ModelName,
        double ColdStartLatencyMs,
        double WarmAvgLatencyMs,
        double AvgTps,
        int TotalTokensGenerated,
        List<string> SampleReplies
    );

    private async Task<BenchmarkModelResult> RunBenchmarkForModelAsync(HttpClient http, string model)
    {
        var endpoint = "http://127.0.0.1:11434/api/chat";
        var replies = new List<string>();
        double coldLatency = 0;
        var warmLatencies = new List<double>();
        double totalTps = 0;
        int completed = 0;
        int totalTokens = 0;

        for (int i = 0; i < TestCases.Length; i++)
        {
            var prompt = TestCases[i];
            var isCold = (i == 0);

            var body = new
            {
                model,
                messages = new object[]
                {
                    new { role = "system", content = "أنتِ سارة، مساعدة عيادة النور في القاهرة. تتحدثين باللهجة المصرية العامية المهذبة فقط وبإيجاز شديد." },
                    new { role = "user", content = prompt }
                },
                stream = false,
                options = new
                {
                    temperature = 0.2,
                    num_predict = 100
                }
            };

            var json = JsonSerializer.Serialize(body);
            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            var sw = Stopwatch.StartNew();
            var res = await http.SendAsync(req);
            sw.Stop();

            if (!res.IsSuccessStatusCode)
            {
                _output.WriteLine($"[Ollama Error] Model {model} failed on case {i + 1}: {res.StatusCode}");
                continue;
            }

            var respText = await res.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(respText);
            var root = doc.RootElement;

            var reply = root.GetProperty("message").GetProperty("content").GetString() ?? "";
            var evalCount = root.TryGetProperty("eval_count", out var ec) ? ec.GetInt32() : 0;
            var evalDurationNs = root.TryGetProperty("eval_duration", out var ed) ? ed.GetInt64() : 0;

            double tps = evalDurationNs > 0 ? (evalCount / (evalDurationNs / 1_000_000_000.0)) : 0;
            totalTps += tps;
            totalTokens += evalCount;
            completed++;
            replies.Add(reply.Trim());

            if (isCold)
            {
                coldLatency = sw.ElapsedMilliseconds;
            }
            else
            {
                warmLatencies.Add(sw.ElapsedMilliseconds);
            }
        }

        var avgWarm = warmLatencies.Count > 0 ? warmLatencies.Average() : coldLatency;
        var avgTps = completed > 0 ? (totalTps / completed) : 0;

        return new BenchmarkModelResult(model, coldLatency, avgWarm, avgTps, totalTokens, replies);
    }

    [Fact]
    public async Task Benchmark_SideBySide_Qwen15B_vs_Qwen3B_RealOllama()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };

        _output.WriteLine("==========================================================================================");
        _output.WriteLine("DUAL-MODEL BENCHMARK: qwen2.5:1.5b vs qwen2.5:3b (REAL OLLAMA ON INTEL i5-12450H CPU)");
        _output.WriteLine("==========================================================================================");

        var result15b = await RunBenchmarkForModelAsync(http, "qwen2.5:1.5b");
        var result3b = await RunBenchmarkForModelAsync(http, "qwen2.5:3b");

        _output.WriteLine("\n------------------------------------------------------------------------------------------");
        _output.WriteLine($"| {"Metric",-30} | {"qwen2.5:1.5b (Real Ollama)",-25} | {"qwen2.5:3b (Real Ollama)",-25} |");
        _output.WriteLine("------------------------------------------------------------------------------------------");
        _output.WriteLine($"| {"Cold Start Latency (ms)",-30} | {result15b.ColdStartLatencyMs,25:F0} | {result3b.ColdStartLatencyMs,25:F0} |");
        _output.WriteLine($"| {"Avg Warm Latency (ms)",-30} | {result15b.WarmAvgLatencyMs,25:F0} | {result3b.WarmAvgLatencyMs,25:F0} |");
        _output.WriteLine($"| {"Avg Generation Speed (TPS)",-30} | {result15b.AvgTps,25:F2} | {result3b.AvgTps,25:F2} |");
        _output.WriteLine($"| {"Total Generated Tokens",-30} | {result15b.TotalTokensGenerated,25} | {result3b.TotalTokensGenerated,25} |");
        _output.WriteLine("------------------------------------------------------------------------------------------");

        _output.WriteLine("\n[Sample Egyptian Arabic Output - Case 1: Checking availability]");
        _output.WriteLine($"1.5B: {result15b.SampleReplies.FirstOrDefault()}");
        _output.WriteLine($"3B:   {result3b.SampleReplies.FirstOrDefault()}");

        _output.WriteLine("\n[Sample Egyptian Arabic Output - Case 2: Staging details]");
        _output.WriteLine($"1.5B: {result15b.SampleReplies.ElementAtOrDefault(1)}");
        _output.WriteLine($"3B:   {result3b.SampleReplies.ElementAtOrDefault(1)}");

        Assert.True(result15b.AvgTps > 5.0, $"Expected qwen2.5:1.5b TPS > 5.0, got {result15b.AvgTps}");
        Assert.True(result3b.AvgTps > 2.0, $"Expected qwen2.5:3b TPS > 2.0, got {result3b.AvgTps}");
    }
}
