#!/usr/bin/env bash
# End-to-end checks against a published stdb-site module.
#
#   STDB_URL=http://127.0.0.1:3000 DB=stdb-site ADMIN_TOKEN=... scripts/smoke.sh
#   SITE_LANG=csharp DB=site-csharp scripts/smoke.sh               # also check which module answered
#   PROXY_URL=http://stdb-site.localhost:8080 scripts/smoke.sh   # also test the Caddy proxy
#
# The same checks pass against the Rust, C# and TypeScript modules.
#
# Every check documents one behaviour of SpacetimeDB HTTP handlers.
set -uo pipefail

STDB_URL=${STDB_URL:-http://127.0.0.1:3000}
DB=${DB:-stdb-site}
SITE_LANG=${SITE_LANG:-}
B="$STDB_URL/v1/database/$DB/route"
PROXY_URL=${PROXY_URL:-}
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

pass=0 fail=0
check() { # check "description" expected actual
  if [[ $3 == "$2" ]]; then
    printf '  \e[32m✔\e[0m %s\n' "$1"; pass=$((pass + 1))
  else
    printf '  \e[31m✘\e[0m %s (expected %q, got %q)\n' "$1" "$2" "$3"; fail=$((fail + 1))
  fi
}
status() { curl -s -o /dev/null -w '%{http_code}' "$@"; }
ctype() { curl -s -o /dev/null -w '%{content_type}' "$@"; }
header() { curl -s -D - -o /dev/null "${@:2}" | tr -d '\r' | awk -v h="$1" 'tolower($1)==tolower(h)":" {sub(/^[^:]*: /,""); print; exit}'; }
json() { python3 -c "import json,sys; d=json.load(sys.stdin); print($1)"; }

echo "Pages and static assets"
check "home page at /route"                  200 "$(status "$B")"
check "home page at /route/"                 200 "$(status "$B/")"
check "<base href> points at the route prefix" "<base href=\"/v1/database/$DB/route/\">" "$(curl -s "$B/" | grep -o '<base[^>]*>')"
check "CSS served with its content type"     "text/css; charset=utf-8" "$(ctype "$B/style-css")"
check "JS served with its content type"      "text/javascript; charset=utf-8" "$(ctype "$B/app-js")"
check "SVG served"                           "image/svg+xml" "$(ctype "$B/logo-svg")"
check "PNG served"                           "image/png" "$(ctype "$B/icon-png")"
check "custom security headers pass through" "default-src 'self'; img-src 'self' data:; connect-src 'self'" "$(header content-security-policy "$B/")"
if [[ -n $SITE_LANG ]]; then
  want=$(case $SITE_LANG in rust) echo Rust ;; csharp) echo 'C#' ;; typescript) echo TypeScript ;; esac)
  check "served by the $want module"         "by the $want module" "$(curl -s "$B/" | grep -o 'by the [^ ]* module')"
fi

echo "Routing rules"
check "dots aren't allowed in routes"        404 "$(status "$B/style.css")"
check "uppercase isn't allowed"              404 "$(status "$B/API/stats")"
check "trailing slash is a different route"  404 "$(status "$B/api/stats/")"
check "HEAD isn't derived from GET"          404 "$(status -I "$B/style-css")"
check "host's 404 body"                      "Database has not registered a handler for this route" "$(curl -s "$B/nope")"

echo "Accounts, cookies, posts"
U="smoke$RANDOM$RANDOM"
J="$TMP/jar"
check "register sets a session"              200 "$(status -c "$J" -H 'content-type: application/json' -d "{\"name\":\"$U\",\"password\":\"hunter22\"}" "$B/api/register")"
check "cookie is HttpOnly and path-scoped"   1 "$(grep -c "#HttpOnly_.*/v1/database/$DB/route.*sid" "$J")"
check "me with cookie"                       "$U" "$(curl -s -b "$J" "$B/api/me" | json 'd["name"]')"
check "posting needs a session"              401 "$(status -d '{"body":"nope"}' "$B/api/posts")"
ID=$(curl -s -b "$J" -d '{"body":"hello <b>world</b>"}' "$B/api/posts" | json 'd["id"]')
check "post created"                         1 "$([[ $ID =~ ^[0-9]+$ ]] && echo 1)"
check "post listed"                          "hello <b>world</b>" "$(curl -s "$B/api/posts" | json "[p['body'] for p in d if p['id']==$ID][0]")"
check "server-rendered permalink escapes HTML" 1 "$(curl -s "$B/post?id=$ID" | grep -c 'hello &lt;b&gt;world&lt;/b&gt;')"
check "wrong password rejected"              401 "$(status -d "{\"name\":\"$U\",\"password\":\"wrong\"}" "$B/api/login")"
check "delete own post"                      200 "$(status -b "$J" -X DELETE "$B/api/posts?id=$ID")"
check "logout clears the session"            401 "$(curl -s -b "$J" -c "$J" -X POST "$B/api/logout" >/dev/null; status -b "$J" "$B/api/me")"

echo "HTTP features"
check "303 redirect with Location"           303 "$(status "$B/api/redirect")"
check "pre-gzipped body"                     gzip "$(header content-encoding "$B/api/gzip")"
ETAG=$(header etag "$B/asset?name=hello-txt")
check "ETag + If-None-Match gives 304"       304 "$(status -H "If-None-Match: $ETAG" "$B/asset?name=hello-txt")"
check "host forces CORS to *"                "*" "$(header access-control-allow-origin "$B/")"
check "OPTIONS never reaches the handler"    "" "$(header x-from-handler -X OPTIONS -H 'origin: https://example.org' -H 'access-control-request-method: POST' "$B/api/cors")"
check "outbound HTTP to private IPs is refused" 502 "$(status "$B/api/outbound?url=http://169.254.169.254/latest/meta-data")"

echo "Sizes"
check "16 MB response"                       16777216 "$(curl -s -o /dev/null -w '%{size_download}' "$B/api/big?kb=16384")"
check "10 MB upload"                         10485760 "$(head -c 10M /dev/zero | curl -s --data-binary @- "$B/api/upload" | json 'd["received_bytes"]')"
if [[ -n ${ADMIN_TOKEN:-} ]]; then
  head -c 300000 /dev/urandom > "$TMP/blob"
  check "admin can upload an asset"          200 "$(status -X PUT -H "Authorization: Bearer $ADMIN_TOKEN" -H 'content-type: application/octet-stream' --data-binary @"$TMP/blob" "$B/asset?name=smoke-blob")"
  check "binary asset round-trips"           ok "$(curl -s "$B/asset?name=smoke-blob" | cmp -s - "$TMP/blob" && echo ok)"
fi
check "asset upload without token rejected"  401 "$(status -X PUT --data-binary x "$B/asset?name=x")"

echo "Failure modes"
check "panic gives 500"                      500 "$(status "$B/api/panic")"
# Rust and C#: a wasm backtrace. TypeScript: the JS stack trace (source paths included).
check "panic body leaks a stack trace"       1 "$(curl -s "$B/api/panic" | grep -cE 'wasm backtrace|^Uncaught Error')"
before=$(curl -s "$B/api/stats" | json 'd["hits"].get("write-then-panic", 0)')
status "$B/api/write-then-panic" >/dev/null
check "write before a panic stays committed" $((before + 1)) "$(curl -s "$B/api/stats" | json 'd["hits"].get("write-then-panic", 0)')"

if [[ -n $PROXY_URL ]]; then
  echo "Behind the Caddy proxy ($PROXY_URL)"
  check "home page at /"                     200 "$(status "$PROXY_URL/")"
  check "<base href> is /"                   '<base href="/">' "$(curl -s "$PROXY_URL/" | grep -o '<base[^>]*>')"
  check "/style.css maps to style-css"       "text/css; charset=utf-8" "$(ctype "$PROXY_URL/style.css")"
  check "/favicon.ico maps to icon-png"      "image/png" "$(ctype "$PROXY_URL/favicon.ico")"
  check "HEAD works"                         200 "$(status -I "$PROXY_URL/style.css")"
  check "compression"                        gzip "$(header content-encoding -H 'accept-encoding: gzip' "$PROXY_URL/app.js")"
  check "custom 404 page"                    1 "$(curl -s "$PROXY_URL/nope" | grep -c 'comes from Caddy')"
  check "host CORS header stripped"          "" "$(header access-control-allow-origin "$PROXY_URL/")"
  PJ="$TMP/proxy-jar"
  status -c "$PJ" -H 'content-type: application/json' -d "{\"name\":\"p$U\",\"password\":\"hunter22\"}" "$PROXY_URL/api/register" >/dev/null
  check "session cookie scoped to /"         1 "$(grep -c $'\t/\t.*sid' "$PJ")"
  ID=$(curl -s -b "$PJ" -d '{"body":"via the proxy"}' "$PROXY_URL/api/posts" | json 'd["id"]')
  check "/posts/N maps to /post?id=N"        1 "$(curl -s "$PROXY_URL/posts/$ID" | grep -c 'via the proxy')"
  status -b "$PJ" -X DELETE "$PROXY_URL/api/posts?id=$ID" >/dev/null
  check "redirect goes to /"                 "${PROXY_URL}/" "$(curl -s -o /dev/null -w '%{redirect_url}' "$PROXY_URL/api/redirect")"
fi

echo
echo "$pass passed, $fail failed"
((fail == 0))
