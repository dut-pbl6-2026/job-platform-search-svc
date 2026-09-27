# job-platform-search-svc
.NET Web API Search — part of **Vietnam Job Platform** (`pbl6`) under [`dut-pbl6-2026`](https://github.com/dut-pbl6-2026).
- Tech: .NET Web API Search
- Branch flow: `feature/* → main` (see job-platform-docs/.github/git-strategy.md)
- Jira PBL6 skid.atlassian.net, Master plan docs/master-plan.md
- TM: TM1 Hoai, TM2 Thanh, TM3 Chi Bao, TM4 Khoa

## Deploy (Render Free jp-search — TM2 Thanh)
- Service: `jp-search` `https://jp-search.onrender.com` `5003` (ES Bonsai)
- Hook: `RENDER_DEPLOY_HOOK_SEARCH`

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

Production (Render free): latency may be higher due to shared CPU — the local
benchmark is the PERF-02 baseline, not a production SLA. Deep pagination is
guarded: `page * size > 500` returns 400 Bad Request (use a more specific query
to narrow results instead).
