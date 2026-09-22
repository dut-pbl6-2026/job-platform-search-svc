"""Recreate the search index after a mapping/analyzer change (W6 vietnamese_icu).

Flow (FIX #3 index versioning):
  1. Read ELASTICSEARCH_URL (default http://localhost:9200) and ELASTICSEARCH_INDEX
     (default jobs_v2 -- the NEW versioned index from the bumped env).
  2. DELETE the index if it exists (fresh mapping; old versioned index is disposable).
  3. Wait for search-svc startup (ElasticsearchInitializer) to recreate it, OR
     restart search-svc so the initializer runs.
  4. Report _count so you can confirm re-ingest (crawler / seed / job-svc re-sync).

Usage:
  ELASTICSEARCH_URL=http://localhost:9200 ELASTICSEARCH_INDEX=jobs_v2 python scripts/recreate_index.py
  # then re-ingest: crawler seed_loader / job-svc re-sync, then re-run this script to check _count
"""

import json
import os
import sys
import time
import urllib.request
import urllib.error

ES_URL = os.environ.get("ELASTICSEARCH_URL", "http://localhost:9200").rstrip("/")
INDEX = os.environ.get("ELASTICSEARCH_INDEX", "jobs_v2")
TIMEOUT = 10


def req(method, path):
    r = urllib.request.Request(f"{ES_URL}/{path}", method=method)
    try:
        with urllib.request.urlopen(r, timeout=TIMEOUT) as resp:
            return resp.status, resp.read().decode()
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode()
    except urllib.error.URLError:
        return 0, ""  # 0 = unreachable (connection refused / DNS / timeout)


def main():
    print(f"Target: {ES_URL}/{INDEX}")

    status, _ = req("DELETE", INDEX)
    if status == 200:
        print(f"Deleted index {INDEX}.")
    elif status == 404:
        print(f"Index {INDEX} does not exist (fresh) — OK.")
    elif status == 0:
        print(f"Cannot reach Elasticsearch at {ES_URL}. Start it first:", file=sys.stderr)
        print("  docker compose build elasticsearch && docker compose up -d elasticsearch", file=sys.stderr)
        return 1
    else:
        print(f"DELETE failed with HTTP {status}. Is Elasticsearch running?", file=sys.stderr)
        return 1

    print("Now (re)start search-svc so ElasticsearchInitializer recreates the index,")
    print("then re-ingest (crawler / seed_loader / job-svc re-sync) and re-run this script.")
    print("Waiting 5s then checking _count...")

    time.sleep(5)
    status, body = req("GET", f"{INDEX}/_count")
    if status == 200:
        count = json.loads(body).get("count", "?")
        print(f"_count = {count}")
        return 0

    if status == 0:
        print(f"Index {INDEX} not reachable. Start ES + search-svc and re-run.", file=sys.stderr)
    else:
        print(f"Index {INDEX} not ready yet (HTTP {status}). Start search-svc and re-run.", file=sys.stderr)
    return 1


if __name__ == "__main__":
    sys.exit(main())
