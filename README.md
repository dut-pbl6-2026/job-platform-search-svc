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
