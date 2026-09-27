"""
Search service latency benchmark — PERF-02 (p95 < 200ms).

Usage:
    python scripts/benchmark.py                 # cold: unique cache key per req, N=100
    python scripts/benchmark.py --warm          # warm: same query, Redis hit path
    python scripts/benchmark.py --n 200         # custom request count
    python scripts/benchmark.py --url http://host:5003
    python scripts/benchmark.py --concurrency 5

Env: SEARCH_URL (optional, overrides --url default http://localhost:5003)

Cold warm-up uses isolated cache keys and does not overlap with cold measurement.
Warm warm-up primes the same cache key used by measurement.
Output: p50 / p95 / p99 / min / max in ms, PASS/FAIL vs 200ms threshold.
"""

import argparse
import os
import sys
import time
import urllib.error
import urllib.request
from concurrent.futures import ThreadPoolExecutor, as_completed
from statistics import quantiles
from urllib.parse import quote

DEFAULT_URL = os.environ.get("SEARCH_URL", "http://localhost:5003")
PERF_TARGET_P95_MS = 200.0

QUERY_POOL = [
    "software engineer", "backend developer", "frontend", "data analyst",
    "devops", "product manager", "java", "python developer", "react",
    "nodejs", "machine learning", "cloud architect", "qa engineer",
    "scrum master", "ux designer", "mobile developer", "android",
    "ios swift", "fullstack", "security engineer", "sre", "data science",
    "golang", "rust developer", "embedded systems", "network engineer",
    "database administrator", "solution architect", "tech lead", "cto",
]


def build_search_url(base_url: str, keyword: str) -> str:
    """Build the search endpoint URL with correct query encoding."""
    return f"{base_url}/api/search/jobs?q={quote(keyword)}&size=20"


def measure_request(base_url: str, keyword: str) -> float:
    """Send one GET /api/search/jobs and return latency in ms."""
    url = build_search_url(base_url, keyword)
    req = urllib.request.Request(url)
    start = time.perf_counter()
    try:
        with urllib.request.urlopen(req, timeout=10) as resp:
            _ = resp.read()
    except urllib.error.HTTPError as e:
        _ = e.read()
    except urllib.error.URLError as e:
        print(f"[WARN] Request failed: {e}", file=sys.stderr)
        return -1.0
    end = time.perf_counter()
    return (end - start) * 1000.0


def cold_keyword(index: int) -> str:
    """Create a new search query/cache key for every cold measurement."""
    base = QUERY_POOL[index % len(QUERY_POOL)]
    return f"{base} benchmark-cold-{index}"


def cold_warmup_keyword(index: int) -> str:
    """Use a namespace that can never overlap cold measurement keys."""
    return f"benchmark-warmup-{index}"


def warm_keyword() -> str:
    """Single cache key used by warm measurement."""
    return QUERY_POOL[0]


def run_benchmark(base_url: str, n: int, warm: bool, concurrency: int) -> list[float]:
    if warm:
        # Prime the exact cache key used by warm measurement.
        print("Warm-up: 20 requests (warm cache prime)...")
        keyword = warm_keyword()
        for _ in range(20):
            measure_request(base_url, keyword)

        def measurement_keyword(index: int) -> str:
            return keyword
    else:
        # IMPORTANT: cold warm-up keys must not overlap measurement keys.
        print("Warm-up: 20 isolated requests (cold cache-safe)...")
        for index in range(20):
            measure_request(base_url, cold_warmup_keyword(index))

        def measurement_keyword(index: int) -> str:
            # Every measurement gets a new cache key.
            return cold_keyword(index)

    print(f"Measuring: {n} requests, concurrency={concurrency}...")
    latencies: list[float] = []

    with ThreadPoolExecutor(max_workers=concurrency) as pool:
        futures = {
            pool.submit(
                measure_request,
                base_url,
                measurement_keyword(index),
            ): index
            for index in range(n)
        }
        for future in as_completed(futures):
            ms = future.result()
            if ms >= 0:
                latencies.append(ms)

    return latencies


def print_report(latencies: list[float], warm: bool, target: float) -> int:
    if not latencies:
        print("No successful measurements.", file=sys.stderr)
        return 1

    latencies.sort()
    qs = quantiles(latencies, n=100)
    p50 = qs[49]
    p95 = qs[94]
    p99 = qs[98]
    mn = latencies[0]
    mx = latencies[-1]

    mode = "warm (cache hit)" if warm else "cold (cache miss)"
    verdict = "PASS" if p95 < target else "FAIL"
    print(f"\n=== Search Benchmark ({mode}, N={len(latencies)}) ===")
    print(f"  p50  : {p50:7.1f}ms")
    print(f"  p95  : {p95:7.1f}ms   [{verdict} < {target:.0f}ms]")
    print(f"  p99  : {p99:7.1f}ms")
    print(f"  min  : {mn:7.1f}ms")
    print(f"  max  : {mx:7.1f}ms")

    if verdict == "FAIL":
        print(
            f"\n[FAIL] p95 {p95:.1f}ms exceeds {target:.0f}ms. "
            "See mitigation in implementation plan (M-1..M-5)."
        )
        return 1

    print(f"\n[PASS] PERF-02 satisfied: p95 {p95:.1f}ms < {target:.0f}ms.")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Search service latency benchmark (PERF-02)"
    )
    parser.add_argument("--url", default=DEFAULT_URL)
    parser.add_argument("--n", type=int, default=100)
    parser.add_argument("--warm", action="store_true")
    parser.add_argument("--concurrency", type=int, default=1)
    args = parser.parse_args()

    if args.n < 2:
        parser.error("--n must be >= 2")
    if args.concurrency < 1:
        parser.error("--concurrency must be >= 1")

    print(f"Target: {args.url}/api/search/jobs")
    latencies = run_benchmark(
        args.url,
        args.n,
        args.warm,
        args.concurrency,
    )
    return print_report(latencies, args.warm, PERF_TARGET_P95_MS)


if __name__ == "__main__":
    sys.exit(main())
