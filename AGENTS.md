# AGENTS — job-platform-search-svc

> Search microservice. SRS: `job-platform-docs/docs/master-plan.md:166`, `docs/srs/en/{3-must-have-fr:SEARCH-01,8-system-architecture:8.4-8.5,10-appendices:D.1,6-nfr}`, `7-eir`. Git: `job-platform-docs/.github/git-strategy.md` (`feature/* → main`).

## Mise activation

Activate `mise` for bare `dotnet`/`infisical` without `mise exec`:

| Shell | Add to config file | Activate |
|-------|--------------------|----------|
| `bash` | `~/.bashrc` or `~/.bash_profile` | `eval "$(mise activate bash)"` |
| `zsh` | `~/.zshrc` | `eval "$(mise activate zsh)"` |
| `fish` | `~/.config/fish/config.fish` | `mise activate fish \| source` |
| `PowerShell` | `$PROFILE` | `mise activate pwsh \| Out-String \| Invoke-Expression` |

Agent uses `mise exec -- dotnet ...` / `mise exec -- infisical ...` due to non-interactive shell without `mise activate`; humans just use `dotnet` / `infisical` after `mise install`.

## Scope

`PBL6-18/19` MUST `SEARCH-01` — Elasticsearch indexing + keyword/location search, `Port 5003` `net10.0` `YARP gateway`. Owner TM2 W2. No PostgreSQL — ES + Redis only (stateless search proxy).

## Architecture — clean Api/Core/Infrastructure

```
src/Search.Api            → Web API (Program.cs JWT Bearer + Swagger + /health)
src/Search.Core           → Domain (JobDocument DTO, SearchResult, SearchQuery)
src/Search.Infrastructure → Services (ElasticsearchService, RedisCache — SHOULD W5)
tests/Search.Tests        → xunit
SearchService.sln         → mise run build/test
```

Dependency: `Api → Infrastructure → Core → SharedKernel` (`PackageReference JobPlatform.SharedKernel 0.1.0` via `local-feed` + `nuget.config`, never `ProjectReference` per `master-plan.md:132`). `MAINT-01` clean arch.

## SRS mapping (SEARCH-01)

- `GET /api/search/jobs?q={keyword}&location={city}&page={0}&size={20}` — full-text search with relevance scoring, pagination (page 0-based, size default 20 max 100), returns `{ items, total, page, size, totalPages }`.
- `GET /api/search/suggest?q={prefix}` — autocomplete suggestions (top 5-10).
- Searchable fields: `title` (text, searchable+sortable), `description` (text, searchable), `company` (text, searchable+filterable+sortable), `location` (text, searchable+filterable+sortable), `salary_min/max` (numeric, filterable range), `category` (keyword, filterable+sortable), `employment_type` (keyword, filterable+sortable), `created_at` (date, sortable).
- Handle empty results gracefully: 200 OK with message "No jobs found matching your criteria".

## Elasticsearch (SRS 8.3.4, 8.4, infra `docker-compose.yml:44`)

- ES `8.13.2` single-node `http://localhost:9200` (`xpack.security.enabled=false`), index name `jobs` (`ELASTICSEARCH_INDEX` env var).
- Index mapping: `title` → `text` (analyzer `standard`), `description` → `text`, `company_name` → `text + keyword`, `location` → `text + keyword`, `salary_min/max` → `long`, `category` → `keyword`, `employment_type` → `keyword`, `experience_level` → `keyword`, `status` → `keyword`, `recruiter_id` → `keyword`, `created_at/updated_at` → `date`.
- Vietnamese text: use `standard` analyzer initially (MUST), upgrade to `icu_analyzer` + Vietnamese plugin in SHOULD phase W6.
- `PERF-02 search p95<200ms` — keep queries efficient, avoid deep pagination (`from+size`), prefer `search_after` for large offsets.

## Events — consumer (SRS 8.5)

- Consume `job.created` → index new document, `job.updated` → update document, `job.deleted` → delete document from ES index. Kafka topic `job-events` group `search-svc`.
- Week 2 Day 1 (Tue): direct HTTP sync from Job Service (simple approach), Kafka consumer added W3 by TM1+TM2.
- Idempotent consumers: use `job_id` as ES document `_id`.

## No hard-coding (STRICT — apply to every file you touch)

**NEVER** embed literal values for any of the following in source code (`.cs`, `.json`, `.yaml`, `.toml`, …):

| Category | Examples of forbidden literals |
|----------|--------------------------------|
| ES URL | `http://localhost:9200` |
| ES index name | `"jobs"` as a string literal outside config |
| Redis URL / host | `localhost:6379` |
| Secrets / passwords | any plain-text password, API key, JWT secret |
| Ports | `5003` |

**Always** read from `IConfiguration` / environment variables:

```csharp
// CORRECT — configuration first, env var fallback, no literal fallback
var esUrl = builder.Configuration["ELASTICSEARCH_URL"]
            ?? builder.Configuration["Elasticsearch:Url"]
            ?? throw new InvalidOperationException(
                "ES URL not configured. Set ELASTICSEARCH_URL.");

var indexName = builder.Configuration["ELASTICSEARCH_INDEX"]
                ?? throw new InvalidOperationException(
                    "ES index not configured. Set ELASTICSEARCH_INDEX.");
```

- `appsettings.json` MAY contain **placeholder comments** like `"<set via env>"` but MUST NOT contain real URLs, hostnames, or credentials.
- `appsettings.Development.json` MAY point to `localhost` **only** for local-dev convenience; never commit real passwords.
- The single source of truth for all env values is `../job-platform-infra/envs/.env.dev.example` — use `mise run sync-env` to pull it.
- Required env vars for this service: `ELASTICSEARCH_URL`, `ELASTICSEARCH_INDEX`, `REDIS_URL`, `JWT_SECRET`.

## 2026 best practice (NFR `MAINT`)

- `dotnet 10.0.100` `net10.0` `nullable enable` `ImplicitUsings` file-scoped namespace, `ProblemDetails` + `UseExceptionHandler` + `ILogger` JSON `ERROR/WARN/INFO/DEBUG`, `GET /health` per `8-system-architecture.md`.
- `dotnet build --warnaserror` + `dotnet format --verify-no-changes` (mise `build/test/format`), coverage `>70%` `MAINT-02`.
- ES client: `Elastic.Clients.Elasticsearch 8.*` (official .NET client).
- Never commit `.env` (`.gitignore`), `mise run sync-env` single source `../job-platform-infra/envs/.env.dev.example` (`ELASTICSEARCH_URL`, `ELASTICSEARCH_INDEX`, `REDIS_URL`, `JWT_SECRET`).

## Workflow

```bash
mise trust && mise install
mise run sync-env && mise run verify
mise run build && mise run test && mise run format
mise run run  # http://localhost:5003/health → {"status":"ok","service":"search"}
```

`feature/* → main` (e.g., `feature/es-index-search`), PR must: Description/How to verify/Checklist `mise run build/test/format`.
