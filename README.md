# job-platform-search-svc
.NET Web API Search — part of **Vietnam Job Platform** (`pbl6`) under [`dut-pbl6-2026`](https://github.com/dut-pbl6-2026).
- Tech: .NET Web API Search
- Branch flow: `feature/* → main` (see job-platform-docs/.github/git-strategy.md)
- Jira PBL6 skid.atlassian.net, Master plan docs/master-plan.md
- TM: TM1 Hoai, TM2 Thanh, TM3 Chi Bao, TM4 Khoa

## Advanced Search (W6, PBL6-6)
- Text fields (`title`, `description`, `company_name`, `location`, `requirements`, `benefits`)
  use custom analyzer `vietnamese_icu` (`icu_tokenizer` + `icu_folding`) — typing without
  diacritics matches accented docs. Requires the `analysis-icu` plugin in the custom ES
  image (`job-platform-infra/docker/es`): `docker compose build elasticsearch` before `up`.
- Filters: `q` (keyword) + `minSalary`/`maxSalary` (overlap) + `location` + `skills` (comma
  list, OR) + `category`/`employmentType`/`experienceLevel`.
- `skills` is a keyword list with a lowercase normalizer. The ingest DTO (`JobSyncDto`)
  accepts it, but producers (job-svc HTTP sync / crawler) must send it — until then the
  `skills` filter returns empty (no error).
- **Reindex after mapping change:** analyzer/mapping only applies to a fresh index. Full
  sequence — export env once, then reuse it for every step:
  ```bash
  export ELASTICSEARCH_URL=http://localhost:9200 ELASTICSEARCH_INDEX=jobs_v2
  python scripts/recreate_index.py   # DELETEs the index (both vars required, no defaults)
  # 1. restart search-svc so ElasticsearchInitializer recreates the index with new mapping
  # 2. re-ingest: crawler / seed_loader / job-svc re-sync
  # 3. verify WITHOUT deleting (re-running the script above would wipe re-ingested data):
  python scripts/recreate_index.py --check
  curl "$ELASTICSEARCH_URL/$ELASTICSEARCH_INDEX/_count"
  ```
  Bump `ELASTICSEARCH_INDEX` (e.g. `jobs` → `jobs_v2`) in `envs/.env.dev.example` so the
  initializer targets the new index; the old index stays as backup until data is confirmed.
- **Coverage gap:** no Testcontainers here (needs a Docker daemon) — ES query behavior
  (Terms filter, normalizer, `icu_folding`) is verified manually via curl, not unit tests.

## Hybrid search (SRS NIC VECTOR-01-03/04/05)

`GET /api/search/jobs` with `q` now ranks by **BM25 + vector similarity** (default keyword 40% /
vector 60%), while keeping the same filters, response shape and pagination. There is no new
client parameter: the web and mobile apps need no change.

- **Recall gate:** the keyword query requires **every word** (`minimum_should_match: 100%`); OR
  matching made `"công nghệ thông tin"` match every job because `"công"` is in every description. A
  job that does not match the keywords is only **added** when the keyword side found nothing at all
  and its vector score reaches `SEARCH_HYBRID_MIN_VECTOR_SCORE` (default `0.72`, where an unrelated
  job sits ~0.55–0.70 and a real match starts ~0.72). Vector scores still re-rank keyword matches;
  the floor only gates additions. Without this gate every query returned the whole index, reordered.
- **Fusion:** each retriever's candidate list is min-max normalised (BM25 scores are 0–20+, cosine
  scores are 0–1, so a raw weighted sum would let BM25 decide alone), then scored as
  `keywordWeight * norm(BM25) + vectorWeight * norm(cosine)`. A job found by only one retriever keeps
  that side's contribution; ties break on `created_at` desc, then id, so paging is stable.
- **Filters:** location / status / category / employment type / experience / salary / skills are sent
  to **both** the BM25 query and the kNN `filter` (a pre-filter; post-filtering would return short
  pages). An explicit `sortBy` other than `relevance` skips fusion and keeps the requested order.
- **Fallback (VECTOR-01-05):** an embedding failure logs a warning and answers with BM25 only; a kNN
  query failure does the same. Search never returns 5xx because ai-svc is down.
- **Pagination:** the candidate pool is `max(page*size + size, size * CandidateMultiplier)` per
  retriever, clamped by `MaxCandidates` (default 600, above the API's `page*size <= 500` guard). The
  `total` is exact when both retrievers returned fewer than the pool, otherwise the BM25 total (the
  best available estimate of the fused universe).
- **Latency:** the embedding call and both ES queries run before the response; a structured log line
  `Hybrid search: pool=… keywordHits=… vectorHits=… elapsedMs=…` (no query text) records each call.
  On the seeded 8-job index, requests take ~50–70 ms warm; the first request after a restart pays the
  ONNX warm-up.

| Setting | Env | Default |
|---|---|---|
| Enabled | `SEARCH_HYBRID_ENABLED` | `true` |
| Keyword weight | `SEARCH_HYBRID_KEYWORD_WEIGHT` | `0.4` |
| Vector weight | `SEARCH_HYBRID_VECTOR_WEIGHT` | `0.6` |
| Semantic-add floor | `SEARCH_HYBRID_MIN_VECTOR_SCORE` | `0.72` |
| Candidates per page | `SEARCH_HYBRID_CANDIDATE_MULTIPLIER` | `3` |
| Candidate cap | `SEARCH_HYBRID_MAX_CANDIDATES` | `600` |

```bash
# Hybrid ordering: the IT jobs should lead, and the location filter must still apply.
curl -s --get http://localhost:5003/api/search/jobs --data-urlencode "q=công nghệ thông tin" --data "page=0&size=10"
curl -s --get http://localhost:5003/api/search/jobs --data-urlencode "q=Java" --data-urlencode "location=Hà Nội"
curl -s --get http://localhost:5003/api/search/jobs --data-urlencode "q=zzzxx"   # 0 results, not the whole index
# Keyword-only (rollback switch, no code change):
SEARCH_HYBRID_ENABLED=false
```

`GET /api/search/jobs/semantic` still exists as the raw kNN endpoint (top-N by meaning, Active jobs
only, no filters, no paging); the product UI does not call it. Search responses are cached for 5
minutes (`RedisSearchCache`), so after changing a weight flush the `search:` keys or wait out the TTL.

## Performance (PERF-02)

Target: `PERF-02` — p95 of `GET /api/search/jobs` < 200ms (SRS NFR). The benchmark
measures end-to-end HTTP latency (API + Redis lookup + Elasticsearch + response
overhead), not the raw ES round-trip.

Benchmark environment: local dev — ES 8.13.2 single-node, Redis local, dotnet 10,
docker compose; minimum benchmark dataset: 50 documents in the index.

| Metric | Cold (cache miss -> ES) | Warm (cache hit) |
|:-------|:-----------------------|:-----------------|
| p50    | TBD                    | TBD              |
| p95    | TBD (target < 200ms)   | TBD (target < 20ms) |
| p99    | TBD                    | TBD              |

> Replace TBD with actual values once the index has data (>= 50 docs; verify with
> `python scripts/recreate_index.py --check`) and the commands below have been run.

Primary PERF-02 measurement: cold (cache miss), `N=100`, `concurrency=1`. The warm
run (Redis cache-hit path, target p95 < 20ms) and `concurrency=3` are secondary
observations (cache-hit overhead, 3 concurrent users).

```bash
# Prerequisites: ES + Redis up, search-svc running (mise run run), index has >= 50 docs
curl -s http://localhost:5003/health
python scripts/recreate_index.py --check

# Primary PERF-02 (cold cache-miss path)
python scripts/benchmark.py --n 100

# Secondary: Redis cache-hit path / 3 concurrent users
python scripts/benchmark.py --n 100 --warm
python scripts/benchmark.py --n 100 --concurrency 3

# Remote server: SEARCH_URL env first, --url to override, localhost:5003 is dev fallback
SEARCH_URL=http://YOUR_SERVER:5003 python scripts/benchmark.py --n 100
```

Latency on shared CPU may be higher — the local
benchmark is the PERF-02 baseline, not a production SLA. Deep pagination is
guarded: `page * size > 500` returns 400 Bad Request (use a more specific query
to narrow results instead).

## Health, logging and code quality

- `GET /health` — cheap liveness, always `{"status":"ok","service":"search"}`.
- `GET /health/ready` — readiness: Elasticsearch ping (3s timeout; `503` when down) and Redis probe (only when `REDIS_URL` is set; down = `degraded`, cache is bypassed).
- Logging via Serilog: compact JSON console in non-Development, request logging, `ServiceName` enricher, `X-Correlation-Id` (generated if absent, echoed in the response). Secrets and `Authorization` headers are never logged.
- CI runs restore, format check, build with `--warnaserror` and tests. SonarCloud (optional): job `sonarcloud` analyses `dut-pbl6-2026_job-platform-search-svc` only when the `SONAR_TOKEN` repo secret exists; otherwise it skips.
