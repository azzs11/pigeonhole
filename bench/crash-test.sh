#!/usr/bin/env bash
# Real crash test: SIGKILL live worker processes (docker kill -s KILL) while they deliver, then count
# what reached the inbox against what the database believes. Unlike MailChaos, nothing is simulated:
# real containers, real PostgreSQL, real SMTP into Mailpit, real kill -9 at random moments.
#
# Needs the benchmark stack:  docker compose -f compose.yaml -f bench/stack/compose.bench.yaml up -d --build
# Usage: bench/crash-test.sh <api-key> [messages=3000] [workers=3] [kill-every-seconds=1.5] [kill-for-seconds=40]
set -euo pipefail
cd "$(dirname "$0")/.."
key="$1"; n="${2:-3000}"; workers="${3:-3}"; every="${4:-1.5}"; window="${5:-40}"
api=http://localhost:8080
run="crash-$(date +%s)"
compose=(docker compose -f compose.yaml -f bench/stack/compose.bench.yaml)
out="bench/results/crash/${run}.json"
mkdir -p bench/results/crash

"${compose[@]}" up -d --scale worker="$workers" --no-recreate worker >/dev/null 2>&1

echo "accepting $n messages ($run)"
for ((start = 0; start < n; start += 500)); do
  count=$(( n - start < 500 ? n - start : 500 ))
  recipients=$(python3 -c "import json,sys; s,c,r=int(sys.argv[1]),int(sys.argv[2]),sys.argv[3]; print(json.dumps([f'{r}-{i}@example.test' for i in range(s,s+c)]))" "$start" "$count" "$run")
  curl -sf -o /dev/null -X POST "$api/v1/messages/bulk" -H "Authorization: Bearer $key" -H "Idempotency-Key: $run-$start" \
    -H "Content-Type: application/json" -d "{\"recipients\":$recipients,\"subject\":\"$run\",\"text\":\"crash test\"}"
done

echo "killing a random worker every ${every}s for ${window}s"
kills=0
killfile="bench/results/crash/${run}.kills.txt"; : > "$killfile"
end=$(( $(date +%s) + window ))
while (( $(date +%s) < end )); do
  victim=$("${compose[@]}" ps -q worker | awk 'BEGIN{srand()} {a[NR]=$0} END{if (NR) print a[int(rand()*NR)+1]}')
  if [[ -n "$victim" ]]; then
    docker kill -s KILL "$victim" >/dev/null 2>&1 && kills=$((kills + 1)) && python3 -c "import time; print(time.time())" >> "$killfile"
    "${compose[@]}" up -d --scale worker="$workers" --no-recreate worker >/dev/null 2>&1 || true
  fi
  sleep "$every"
done
"${compose[@]}" up -d --scale worker="$workers" --no-recreate worker >/dev/null 2>&1

echo "kills: $kills; waiting for the outbox to drain"
psql() { docker exec pigeonhole-postgres-1 psql -U relay -d relay -At -c "$1"; }
for _ in $(seq 1 180); do
  open=$(psql "select count(*) from messages where subject = '$run' and status in ('Accepted','Sending')")
  [[ "$open" == "0" ]] && break
  sleep 2
done
sleep 3

psql "select internet_message_id, status from messages where subject = '$run'" > "bench/results/crash/${run}.db.txt"
window_db=$(psql "select extract(epoch from min(created_at)), extract(epoch from max(updated_at)) from messages where subject = '$run'")
python3 -I - "$run" "$kills" "$workers" "$every" "$window" "bench/results/crash/${run}.db.txt" "$out" "$killfile" "$window_db" <<'PY'
import json, sys, urllib.parse, urllib.request, collections, datetime
run, kills, workers, every, window, dbfile, out, killfile, window_db = sys.argv[1:10]
first, last = (float(x) for x in window_db.split("|"))
kill_times = [float(x) for x in open(killfile).read().split()]
db = dict(line.split("|", 1) for line in open(dbfile).read().split("\n") if line)
seen = collections.Counter()
start = 0
while True:
    q = urllib.parse.quote(f'subject:"{run}"')
    page = json.load(urllib.request.urlopen(f"http://localhost:8025/api/v1/search?query={q}&start={start}&limit=1000"))
    for m in page["messages"]:
        seen["<" + m["MessageID"].strip("<>") + ">"] += 1
    start += len(page["messages"])
    if not page["messages"] or start >= page["messages_count"]:
        break
status = collections.Counter(db.values())
duplicates = sum(c - 1 for c in seen.values() if c > 1)
delivered = sum(1 for mid in db if seen[mid] >= 1)
lost = sum(1 for mid, s in db.items() if seen[mid] == 0 and s not in ("Unknown", "DeadLettered"))
unknown_delivered = sum(1 for mid, s in db.items() if s == "Unknown" and seen[mid] >= 1)
sent_not_seen = sum(1 for mid, s in db.items() if s == "Sent" and seen[mid] == 0)
report = {
    "benchmark": "crash test (real SIGKILL of worker containers)",
    "run": run, "generatedAt": datetime.datetime.now(datetime.timezone.utc).isoformat(),
    "messages": len(db), "workers": int(workers), "kills": int(kills),
    "killsDuringDelivery": sum(1 for t in kill_times if first <= t <= last),
    "deliverySeconds": round(last - first, 1),
    "killEverySeconds": float(every), "killWindowSeconds": int(window),
    "statusAtEnd": dict(status),
    "deliveredAtLeastOnce": delivered, "duplicates": duplicates, "lost": lost,
    "unknown": status.get("Unknown", 0), "unknownThatHadArrived": unknown_delivered,
    "sentButNotInInbox": sent_not_seen,
}
json.dump(report, open(out, "w"), indent=2)
print(json.dumps(report, indent=2))
PY
rm -f "bench/results/crash/${run}.db.txt" "$killfile"
echo "wrote $out"
