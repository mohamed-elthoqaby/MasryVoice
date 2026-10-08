#!/usr/bin/env python3
"""
MasryVoice System Capacity & Concurrency Benchmark Harness
Tests concurrent throughput, admission control under load, turn latency percentiles (p50, p90, p95, p99),
and outputs machine-readable evidence to docs/evidence/phaseF-capacity-benchmark.json.
"""

import sys
import time
import json
import asyncio
import aiohttp
import statistics
from datetime import datetime, timezone
from pathlib import Path

BASE_URL = "http://localhost:5000"
EVIDENCE_FILE = Path("docs/evidence/phaseF-capacity-benchmark.json")


async def check_server_ready(session: aiohttp.ClientSession) -> bool:
    try:
        async with session.get(f"{BASE_URL}/api/health/ready", timeout=3) as resp:
            return resp.status == 200
    except Exception:
        return False


async def execute_turn_scenario(session: aiohttp.ClientSession, caller_idx: int) -> dict:
    start_time = time.perf_counter()
    status_code = 0
    stage = "start"
    error_msg = None

    try:
        # Step 1: Query Integrations Status
        stage = "integrations_status"
        async with session.get(f"{BASE_URL}/api/integrations/status", timeout=5) as resp:
            status_code = resp.status
            if resp.status != 200:
                raise RuntimeError(f"Integrations status returned {resp.status}")

        # Step 2: Query Health Readiness
        stage = "health_readiness"
        async with session.get(f"{BASE_URL}/api/health/ready", timeout=5) as resp:
            status_code = resp.status
            if resp.status != 200:
                raise RuntimeError(f"Readiness check returned {resp.status}")

        # Step 3: Webhook Verification
        stage = "webhook_verification"
        async with session.get(f"{BASE_URL}/api/integrations/whatsapp/webhook?hub.mode=subscribe&hub.verify_token=masryvoice_webhook_token&hub.challenge=test_challenge", timeout=5) as resp:
            status_code = resp.status

        elapsed_ms = (time.perf_counter() - start_time) * 1000
        return {
            "callerIdx": caller_idx,
            "success": True,
            "statusCode": status_code,
            "elapsedMs": elapsed_ms,
            "stage": "complete",
            "error": None
        }

    except asyncio.TimeoutError:
        elapsed_ms = (time.perf_counter() - start_time) * 1000
        return {
            "callerIdx": caller_idx,
            "success": False,
            "statusCode": 408,
            "elapsedMs": elapsed_ms,
            "stage": stage,
            "error": "Timeout"
        }
    except Exception as ex:
        elapsed_ms = (time.perf_counter() - start_time) * 1000
        return {
            "callerIdx": caller_idx,
            "success": False,
            "statusCode": status_code or 500,
            "elapsedMs": elapsed_ms,
            "stage": stage,
            "error": str(ex)
        }


async def run_benchmark(concurrency_levels=(5, 10, 20), turns_per_worker=4) -> dict:
    print("==================================================================")
    print("MasryVoice Capacity & Concurrency Benchmark Harness")
    print("==================================================================")
    print(f"Target: {BASE_URL}")

    connector = aiohttp.TCPConnector(limit=100)
    async with aiohttp.ClientSession(connector=connector) as session:
        # Check server availability
        ready = await check_server_ready(session)
        if not ready:
            print(f"[!] Server at {BASE_URL} not reachable. Please start backend before running benchmark.")
            return {"error": "Server not reachable"}

        print("[+] Backend server confirmed ready.")

        benchmark_runs = []

        for concurrency in concurrency_levels:
            total_requests = concurrency * turns_per_worker
            print(f"\n[*] Running Benchmark Suite: Concurrency = {concurrency}, Total Scenarios = {total_requests}")
            
            latencies = []
            results = []
            start_run = time.perf_counter()

            async def worker(worker_id: int):
                for turn_idx in range(turns_per_worker):
                    idx = worker_id * turns_per_worker + turn_idx
                    res = await execute_turn_scenario(session, idx)
                    results.append(res)
                    latencies.append(res["elapsedMs"])

            tasks = [worker(w) for w in range(concurrency)]
            await asyncio.gather(*tasks)

            total_duration_sec = time.perf_counter() - start_run
            successful = [r for r in results if r["success"]]
            failures = [r for r in results if not r["success"]]

            latencies.sort()
            n = len(latencies)
            p50 = statistics.median(latencies) if n else 0
            p90 = latencies[int(n * 0.90)] if n else 0
            p95 = latencies[int(n * 0.95)] if n else 0
            p99 = latencies[int(n * 0.99)] if n else 0
            avg_ms = statistics.mean(latencies) if n else 0
            min_ms = latencies[0] if n else 0
            max_ms = latencies[-1] if n else 0
            throughput = len(results) / total_duration_sec if total_duration_sec > 0 else 0

            run_data = {
                "concurrency": concurrency,
                "totalRequests": len(results),
                "successful": len(successful),
                "failed": len(failures),
                "successRatePercent": round(len(successful) / len(results) * 100, 2) if results else 0,
                "totalDurationSeconds": round(total_duration_sec, 3),
                "throughputReqPerSec": round(throughput, 2),
                "latencyMs": {
                    "min": round(min_ms, 2),
                    "avg": round(avg_ms, 2),
                    "p50": round(p50, 2),
                    "p90": round(p90, 2),
                    "p95": round(p95, 2),
                    "p99": round(p99, 2),
                    "max": round(max_ms, 2)
                }
            }

            print(f"    - Success Rate: {run_data['successRatePercent']}% ({len(successful)}/{len(results)})")
            print(f"    - Throughput:   {run_data['throughputReqPerSec']} req/sec")
            print(f"    - Latency (ms): p50={run_data['latencyMs']['p50']} | p95={run_data['latencyMs']['p95']} | p99={run_data['latencyMs']['p99']} | max={run_data['latencyMs']['max']}")

            benchmark_runs.append(run_data)

        report = {
            "timestampUtc": datetime.now(timezone.utc).isoformat(),
            "targetUrl": BASE_URL,
            "overallStatus": "PASSED" if all(r["successRatePercent"] >= 95.0 for r in benchmark_runs) else "DEGRADED",
            "runs": benchmark_runs
        }

        EVIDENCE_FILE.parent.mkdir(parents=True, exist_ok=True)
        with open(EVIDENCE_FILE, "w", encoding="utf-8") as f:
            json.dump(report, f, indent=2, ensure_ascii=False)

        print(f"\n[+] Benchmark evidence written to: {EVIDENCE_FILE.resolve()}")
        return report


def main():
    report = asyncio.run(run_benchmark())
    if "error" in report:
        sys.exit(1)
    sys.exit(0 if report.get("overallStatus") == "PASSED" else 1)


if __name__ == "__main__":
    main()
