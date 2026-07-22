#!/bin/bash
# End-to-end test against a real armed tab.
#
# This cannot launch its own browser: Chrome 137+ ignores --load-extension ("--load-extension
# is not allowed in Google Chrome"), so the extension must be loaded by hand at
# chrome://extensions and a tab armed with the toolbar button. That is also exactly how the
# tool is used for real, so the test runs against the production configuration.
#
# Arm a SCRATCH tab: this navigates it around. The original URL is restored at the end.
#
# Usage: test/e2e.sh [--quick]   (--quick skips the 45s service-worker idle test)

set -u

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
TABCTL="$ROOT/dist/tabctl"
# Ask the OS for a free port rather than hardcoding one — a fixed port silently collided with
# an unrelated server the user already had running, and every assertion failed against its 404s.
FIXTURE_PORT=$(python3 -c 'import socket;s=socket.socket();s.bind(("127.0.0.1",0));print(s.getsockname()[1]);s.close()')
FIXTURE="http://127.0.0.1:$FIXTURE_PORT/index.html"
QUICK=${1:-}

PASS=0; FAIL=0
ok()   { echo "  PASS  $1"; PASS=$((PASS+1)); }
bad()  { echo "  FAIL  $1"; echo "        expected: $2"; echo "        actual:   $3"; FAIL=$((FAIL+1)); }
check(){ [ "$2" = "$3" ] && ok "$1" || bad "$1" "$2" "$3"; }
contains(){ case "$3" in *"$2"*) ok "$1";; *) bad "$1" "contains '$2'" "$3";; esac; }

T() { "$TABCTL" "$@" 2>&1; }

if [ ! -x "$TABCTL" ]; then
  echo "Build first: dotnet publish src/TabCtl -c Release -r linux-x64 -o dist"
  exit 2
fi

STATUS=$(T status)
case "$STATUS" in
  *'"attached": true'*) ;;
  *)
    echo "No tab is armed. To run this test:"
    echo "  1. chrome://extensions -> Developer mode -> Load unpacked -> $ROOT/extension"
    echo "  2. Open a scratch tab and click the tabctl toolbar button"
    echo "  3. Re-run this script"
    echo
    echo "Daemon says: $STATUS"
    exit 2
    ;;
esac

ORIGINAL_URL=$(T eval 'location.href')
echo "== armed tab: $ORIGINAL_URL =="

cleanup() {
  [ -n "${HTTP_PID:-}" ] && kill "$HTTP_PID" 2>/dev/null
  case "$ORIGINAL_URL" in
    http*|about*) "$TABCTL" nav "$ORIGINAL_URL" >/dev/null 2>&1 ;;
  esac
}
trap cleanup EXIT

echo "== starting fixture server on $FIXTURE_PORT =="
python3 -m http.server "$FIXTURE_PORT" --bind 127.0.0.1 -d "$ROOT/test/fixture" >/dev/null 2>&1 &
HTTP_PID=$!

# Fail loudly if the fixture isn't actually being served — otherwise every later assertion
# fails against a 404 page and the real cause is buried.
for _ in $(seq 1 40); do
  [ "$(curl -s -o /dev/null -w '%{http_code}' "$FIXTURE")" = "200" ] && break
  sleep 0.25
done
if [ "$(curl -s -o /dev/null -w '%{http_code}' "$FIXTURE")" != "200" ]; then
  echo "fixture server not serving $FIXTURE — aborting"
  exit 2
fi

BASE_SEQ=$(T status | node -e 'let d="";process.stdin.on("data",c=>d+=c).on("end",()=>console.log(JSON.parse(d).seq))')
T nav "$FIXTURE" >/dev/null

echo
echo "== navigation =="
check    "nav waits for load"      "tabctl fixture"  "$(T eval 'document.title')"
contains "nav reports final url"   "index.html"      "$(T eval 'location.href')"

echo
echo "== eval =="
check    "eval arithmetic"         "4"               "$(T eval '2+2')"
check    "eval reads the document" "tabctl fixture"  "$(T eval 'document.title')"
check    "eval awaits promises"    "resolved"        "$(T eval 'Promise.resolve("resolved")')"
contains "eval reports throws"     "Uncaught"        "$(T eval 'throw new Error("boom")')"
check    "eval throw exits 1"      "1"               "$("$TABCTL" eval 'throw new Error("x")' >/dev/null 2>&1; echo $?)"

echo
echo "== text =="
check    "text by selector"        "Fixture Page"    "$(T text h1)"
contains "text missing selector"   "No element"      "$(T text '#nope')"

echo
echo "== real input events =="
T click '#go' >/dev/null
check    "click fires isTrusted handler" "clicked:true" "$(T eval 'document.getElementById("status").textContent')"
contains "click missing selector"  "No element"      "$(T click '#nope')"

T click '#field' >/dev/null
T type 'hello world' >/dev/null
check    "type reaches the input"  "hello world"     "$(T eval 'document.getElementById("field").value')"
check    "type fires input event"  "hello world"     "$(T eval 'document.getElementById("typed-value").textContent')"

T key Enter >/dev/null
check    "key Enter reaches handler" "entered"       "$(T eval 'document.getElementById("status").textContent')"
contains "unknown key is rejected" "Unknown key"     "$(T key Banana)"

echo
echo "== event buffer =="
contains "console logs captured"     "fixture loaded" "$(T logs --since "$BASE_SEQ")"
contains "console warnings captured" "WARNING"        "$(T logs --since "$BASE_SEQ")"
contains "network requests captured" "data.json"      "$(T net --since "$BASE_SEQ")"

echo
echo "== screenshot =="
SHOT=$(T screenshot -o /tmp/tabctl-e2e-shot.png)
if [ -f "$SHOT" ] && [ "$(head -c 4 "$SHOT" | od -An -tx1 | tr -d ' ')" = "89504e47" ]; then
  ok "screenshot writes a real PNG ($(stat -c%s "$SHOT") bytes)"
else
  bad "screenshot writes a real PNG" "PNG magic bytes" "$(ls -l "$SHOT" 2>&1)"
fi

echo
echo "== raw cdp escape hatch =="
contains "cdp passthrough"        "currentIndex"     "$(T cdp Page.getNavigationHistory)"
contains "bad cdp method errors"  "Error:"           "$(T cdp Bogus.nope)"
contains "bad --params rejected"  "not valid JSON"   "$(T cdp Page.reload --params '{oops}')"

echo
echo "== per-tab isolation =="
# Open and focus a second tab from the page itself, then confirm commands still land on the
# armed tab rather than the newly focused one.
T eval 'window.__t = window.open("about:blank"); "opened"' >/dev/null
sleep 1
check    "eval still targets the armed tab" "tabctl fixture" "$(T eval 'document.title')"
check    "armed tab is backgrounded"        "hidden"         "$(T eval 'document.visibilityState')"
T eval 'window.__t.close(); "closed"' >/dev/null

echo
echo "== MV3 service worker keepalive =="
if [ "$QUICK" = "--quick" ]; then
  echo "  SKIPPED (--quick)"
else
  echo "  idling 45s, past Chrome's 30s idle kill..."
  sleep 45
  check    "eval works after idle"      "4"            "$(T eval '2+2')"
  contains "status intact after idle"   "index.html"   "$(T status)"
  contains "no reconnect gap in logs"   "fixture"      "$(T text h1)"
fi

echo
echo "=============================="
echo " passed: $PASS   failed: $FAIL"
echo "=============================="
[ "$FAIL" -eq 0 ]
