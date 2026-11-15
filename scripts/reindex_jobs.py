"""Copy every document of SOURCE_INDEX into the index search-svc writes to (ELASTICSEARCH_INDEX).

Used after a mapping change (new index name, PBL6-56 added the embedding field). Documents go through
search-svc's bulk-index endpoint, so each one gets its embedding; an ES _reindex would copy them without vectors.
search-svc must already be running against the new index (it creates the index at startup).

Required env: ELASTICSEARCH_URL, SOURCE_INDEX, SEARCH_URL (e.g. http://localhost:5003).
Optional: SEARCH_INDEX_TOKEN (sent as X-Internal-Token when search-svc enforces it).

Usage: python scripts/reindex_jobs.py
"""

import json
import os
import sys
import urllib.error
import urllib.request

BATCH = 200
TIMEOUT = 60


def env(name):
    value = os.environ.get(name, "").strip()
    if not value:
        print(f"Set {name} (no defaults: this script writes to search-svc).", file=sys.stderr)
        sys.exit(2)
    return value.rstrip("/")


ES_URL = env("ELASTICSEARCH_URL")
SOURCE = env("SOURCE_INDEX")
SEARCH_URL = env("SEARCH_URL")
TOKEN = os.environ.get("SEARCH_INDEX_TOKEN", "").strip()

# ES _source (snake_case) -> JobSyncDto (camelCase)
FIELD_MAP = {
    "id": "id",
    "title": "title",
    "description": "description",
    "company_id": "companyId",
    "company_name": "companyName",
    "location": "location",
    "salary_min": "salaryMin",
    "salary_max": "salaryMax",
    "currency": "currency",
    "category_id": "categoryId",
    "category_name": "categoryName",
    "employment_type": "employmentType",
    "experience_level": "experienceLevel",
    "status": "status",
    "recruiter_id": "recruiterId",
    "created_at": "createdAt",
    "updated_at": "updatedAt",
    "expires_at": "expiresAt",
    "requirements": "requirements",
    "benefits": "benefits",
    "skills": "skills",
}


def post_json(url, payload, headers=None):
    data = json.dumps(payload).encode()
    req = urllib.request.Request(url, data=data, method="POST", headers={"Content-Type": "application/json", **(headers or {})})
    try:
        with urllib.request.urlopen(req, timeout=TIMEOUT) as resp:
            return resp.status, json.loads(resp.read().decode() or "null")
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode()


def to_dto(source):
    return {camel: source[snake] for snake, camel in FIELD_MAP.items() if source.get(snake) is not None}


def main():
    headers = {"X-Internal-Token": TOKEN} if TOKEN else {}
    search_after = None
    read = written = 0

    while True:
        query = {"size": BATCH, "sort": [{"id": "asc"}], "query": {"match_all": {}}}
        if search_after is not None:
            query["search_after"] = search_after
        status, body = post_json(f"{ES_URL}/{SOURCE}/_search", query)
        if status != 200:
            print(f"Reading {SOURCE} failed with HTTP {status}: {body}", file=sys.stderr)
            return 1

        hits = body["hits"]["hits"]
        if not hits:
            break
        search_after = hits[-1]["sort"]
        dtos = [to_dto(h["_source"]) for h in hits]
        read += len(dtos)

        status, result = post_json(f"{SEARCH_URL}/api/search/bulk-index", dtos, headers)
        if status != 200:
            print(f"bulk-index failed with HTTP {status} after {written} documents: {result}", file=sys.stderr)
            return 1
        if result["totalIndexed"] != len(dtos):
            print(
                f"Only {result['totalIndexed']}/{len(dtos)} documents indexed in this batch (embedding service down?). "
                f"Stopped after {written} documents; re-run to continue.",
                file=sys.stderr,
            )
            return 1
        written += result["totalIndexed"]
        print(f"indexed {written}/{read} read so far")

    print(f"Done: {written} documents copied from {SOURCE}.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
