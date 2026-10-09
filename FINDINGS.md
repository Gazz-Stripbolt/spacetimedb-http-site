# Findings: hosting a website from SpacetimeDB HTTP handlers

**Tested on:** SpacetimeDB **2.11.0** (standalone, CLI and server), with the same site as a **Rust**, **C#** (NativeAOT-LLVM, .NET 10) and **TypeScript** module, Linux x86-64 (2 vCPU, 4 GB RAM).
**Source read:** `clockworklabs/SpacetimeDB` master at `f0d2ad4` (October 2026).
**Not tested:** Maincloud. Its limits are listed as unknowns at the end.

HTTP handlers are **beta** (Rust needs `features = ["unstable"]`). Expect details here to change between releases. Most rows below are asserted by [`scripts/smoke.sh`](scripts/smoke.sh). The big sizes, CPU limit, concurrency, throughput, republish and subscription rows were measured by hand.

## TL;DR

**Yes, a whole site can live in one module.** That covers HTML, CSS, JS, images, a JSON API called with `fetch`, cookie logins, server-rendered pages, and live updates through the normal WebSocket subscriptions.

The limits that matter, roughly in order of how much they hurt:

1. **The URL shape is fixed.** Everything lives under `/v1/database/<db>/route/...`.
   - Route paths may only use **lowercase letters, digits and `-_~/`**. No dots (`style.css` ❌), no uppercase.
   - No wildcards, no path params, no catch-all. `Router::nest` only adds a prefix to each route at build time.
2. **Wasm handlers share the database's single thread.** That applies to Rust, C# and C++ modules. A slow handler stalls every other request *and* every reducer. TypeScript handlers run on a V8 worker pool instead (measured below), but have no CPU limit.
3. **No streaming.** Request and response bodies are fully buffered, so there's no SSE or chunked progress. Use subscriptions for anything live.
4. **No caller identity.** Handlers get no sender and no JWT, so you build auth yourself. The handler RNG is **seeded from the request timestamp**, so don't make session tokens from it alone.
5. **The host owns CORS and OPTIONS.** `Access-Control-Allow-Origin: *` is forced on every response, credentials are never allowed, and `.options()` handlers never run.

A reverse proxy (see [`proxy/Caddyfile`](proxy/Caddyfile)) removes most of the URL pain.

## Results

| Area | Behaviour |
|---|---|
| HTML / CSS / JS / SVG / PNG | ✅ Served with any Content-Type you set. Binary bodies round-trip byte for byte. |
| `/style.css`, `/API/x`, `/api/x/` | ❌ 404. No dots, no uppercase, and a trailing slash is a different route. |
| Unknown route or wrong method | 404 with plain text `Database has not registered a handler for this route`. You can't customise this 404 from the module. |
| `""` vs `/` | Two separate routes; register both. From `/route` (no slash), relative links resolve *outside* the route tree, so the demo injects `<base href>`. |
| Request URI | The handler receives the **full external URL**, rebuilt from `Host` / `X-Forwarded-*`, including the query string. |
| Request headers | ✅ All passed through: `Cookie`, `Authorization`, custom headers. SpacetimeDB doesn't validate `Authorization` on `/route`. |
| Response headers | ✅ Passed through untouched: `Set-Cookie`, `Cache-Control`, `ETag`, `Location`, `Content-Encoding`, CSP. The host only adds CORS headers and `Vary`. |
| Status codes | ✅ Anything valid: 201, 303, 304, 401, 409, … |
| Conditional requests | ✅ Your handler can do ETag / `If-None-Match` → 304 itself; the host doesn't. |
| Compression | ✅ A pre-gzipped body with `Content-Encoding: gzip` works (24 KB → 155 B). The host doesn't compress for you. |
| HEAD | ❌ Not derived from GET. Register `.head()` or `.any()`. |
| OPTIONS | ❌ The host's CORS layer answers every OPTIONS request itself; `.options()` handlers are unreachable. |
| Response size | ✅ 1 / 16 / 64 / **256 MB** all fine (256 MB in 0.3 s locally). |
| Request size | ✅ 1 / 10 / **100 MB** uploads fine. |
| Module size | ✅ A **100 MB** wasm module (embedded asset) published and served fine. |
| Assets in a table | ✅ A 5 MB row served in 6 ms. They can be updated at runtime without republishing. |
| Republish | ✅ Table data survives. Embedded assets update with the module. |
| HTTP write → subscribers | ✅ A row inserted in `with_tx` from a handler is pushed live to WebSocket subscribers. |
| Outbound HTTP | ✅ `ctx.http.get` works from handlers. Loopback and private IPs are refused. It can't be called inside an open transaction. |
| Panic / exception | ⚠️ 500, and the response body contains a stack trace: the **wasm backtrace** (Rust, C#) or the **JS stack with source paths** (TypeScript). See [Languages](#languages). |
| Write, then panic | ⚠️ Earlier `with_tx` commits **stay committed**. A handler is not one transaction. |
| CPU limit | Rust: a busy loop was killed after **~13 s** (fuel budget) with a 500. C#: **~22 s**. TypeScript: **none**, a 32 s loop finished with a 200. Handler energy isn't charged yet (`TODO` in the source). |
| Concurrency | ⚠️ Rust/C#: while one handler spun for ~1.4 s, a static CSS request and a `spacetime sql` query **both waited ~1 s**. TypeScript: the CSS request took 0.8 ms during an 11 s spin. |
| Throughput | See [Languages](#languages): roughly 9-12k req/s for small responses on a 2-vCPU box, in all three languages. |

## Languages

The same site, routes and checks in Rust, C# and TypeScript. All three pass the same 49 smoke checks (direct and
behind Caddy).

### Throughput

[`scripts/bench.mjs`](scripts/bench.mjs): 16 keep-alive clients for 5 s per URL, on the same 2-vCPU VM as the server
(so the load generator competes for CPU; treat these as relative).

| | Rust | C# (NativeAOT-LLVM) | TypeScript |
|---|---|---|---|
| `/v1/ping` (host baseline, no module code) | 14.9k req/s | 14.9k | 14.7k |
| Static asset (`/style-css`) | **12.2k** req/s · p99 3.4 ms | 10.8k · 3.5 ms | 9.4k · 4.4 ms |
| JSON read from tables (`/api/stats`) | 10.0k · 3.5 ms | 9.5k · 3.6 ms | 9.0k · 3.6 ms |
| Home page + a write transaction | 9.4k · 3.6 ms | 7.9k · 3.1 ms | 7.3k · 4.1 ms |
| 1 MB response bodies | 1.1k · 30 ms | 1.1k · 29 ms | 0.8k · 40 ms |

### Differences that matter

| | Rust | C# | TypeScript |
|---|---|---|---|
| Threading | One thread shared with reducers | One thread shared with reducers | V8 worker pool: a slow handler doesn't block others |
| CPU limit | ~13 s, then 500 | ~22 s, then 500 | **None** (a 32 s loop finished). Guard expensive routes yourself |
| Tight-loop speed (`/api/spin?n=1000`) | 1.4 s | 1.0 s | 11 s (JS number math) |
| Unhandled error | `panic!` → 500 + wasm backtrace, ~1 ms | exception → 500 + wasm backtrace. The generated dispatcher logs and **rethrows**, so NativeAOT fail-fasts and the instance is recreated: that request and the next one take **~17 ms** | `throw` → 500 + `Uncaught Error` with the JS stack **and source file paths** |
| Embedding the site | `include_str!` / `include_bytes!` | `EmbeddedResource` + `GetManifestResourceStream` | generated `site.gen.ts` (run `npm run gen` before publishing) |
| Hashing | `sha2` crate | **No `System.Security.Cryptography` on wasi**: managed SHA-256 in `Lib.cs` | `@noble/hashes` |
| gzip | `flate2` | **`GZipStream` aborts** (no native zlib in NativeAOT on wasi): a ~100-line managed encoder (24 KB → 266 B) | `fflate` |
| Randomness for tokens | `new_uuid_v4()` (timestamp-seeded) | `ctx.Rng` (timestamp-seeded) | `newUuidV4()` (timestamp-seeded) |
| HTTP handler API status | `unstable` feature | `#pragma warning disable STDB_UNSTABLE` (`WithTx` in handlers is marked unstable) | no opt-in needed |

### Per-language gotchas

- **C#:** don't let exceptions escape a handler. Catch and return your own 500: it saves the instance restart and keeps
  backtraces out of responses. Request headers arrive as a `List<HttpHeader>` with byte values, so look them up
  case-insensitively.
- **C#:** `HttpRequest.Uri` is the full external URL as a string; `Lib.cs` has small helpers to pull out the path, the
  query parameters and cookies.
- **TypeScript:** the 500 body for an uncaught error includes your source file paths and line numbers. Wrap handlers
  that touch untrusted input.
- **TypeScript:** `ctx.http.fetch` throws for host refusals (private IPs) and for status codes the `statuses` package
  doesn't know (e.g. 530), so wrap it in `try`.

## Auth

- **The route is public.** Every handler has to check credentials itself.
- **Don't use the handler RNG alone for secrets.** `HandlerContext::rng()` and `new_uuid_v4()` are seeded with `StdbRng::seed_from_ts(ctx.timestamp)`. Anyone who knows roughly when a token was minted can narrow it down. The demo hashes an owner-set `SITE_SECRET` (an [environment variable](https://spacetimedb.com/docs/databases/environment-variables)) together with the RNG output, the timestamp and per-request data. Only token *hashes* are stored.
- **Cookies:** `HttpOnly; SameSite=Lax`, with `Path` scoped to the site's base path, so the cookie isn't sent to other databases on the same host.
- **Cross-origin:** the forced `ACAO: *` without credentials means cookie auth only works same-origin. Cross-origin clients need bearer tokens. The host's preflight only allows `authorization`, `accept` and `content-type` request headers.
- **Passwords:** the demo uses salted SHA-256 to stay small. For anything real, use argon2/scrypt (both compile to wasm), and remember that a slow KDF blocks the shared thread while it runs.

## Workarounds

| Limit | Workaround |
|---|---|
| No dots / uppercase | Dot-free asset routes (`/style-css`) with the right Content-Type, plus `<link rel="icon">` for favicons. Or let a proxy rewrite `/style.css` → `/style-css`. |
| No path params / catch-all | Query strings (`/post?id=1`), hash routing for SPAs, or proxy rewrites (`/posts/1` → `/post?id=1`). |
| URL prefix | `<base href>` injected per request. Or a proxy that maps a domain's `/` onto the route prefix and sends `X-Site-Base: /`. |
| Shared thread | Keep handlers small and cache rendered output in a table. Or use a TypeScript module for the HTTP layer. |
| No streaming | WebSocket subscriptions for live data. |
| Panics | Return your own 500s; never `unwrap` request data. |
| Non-atomic handlers | Do all of a request's writes in a single `with_tx`. |
| No HEAD / compression / custom 404 | Do it in the handler, or let the proxy handle it. |

## Behind a reverse proxy

[`proxy/Caddyfile`](proxy/Caddyfile) serves the site at `http://stdb-site.localhost:8080`. Checked by the smoke test and in a browser:

- Clean URLs.
- `/style.css`, `/app.js`, `/favicon.ico` → the dot-free routes.
- `/posts/1` → `/post?id=1`.
- HEAD works.
- zstd/gzip (app.js 4.4 KB → 1.7 KB).
- A custom 404 page.
- The host's CORS headers are stripped.
- Cookies get `Path=/` and redirects go to `/`.

The module reads `X-Site-Base` to learn the base path the browser sees. Anyone can send that header directly, so the value is restricted to `[A-Za-z0-9-_~/]`; it only changes that one response.

Caddy gotcha: all `rewrite`s in one block are mutually exclusive (first match wins), so each one has to map to the full route prefix.

For Maincloud, point the upstream at `https://maincloud.spacetimedb.com` with `header_up Host maincloud.spacetimedb.com`. Every request still counts as Maincloud egress.

## Unknowns

- Maincloud: front-proxy body limits, module size limits, rate limits, egress billing.
- HTTP/2 specifics, Range requests (not handled by the host), and behaviour with thousands of routes (matching is a linear scan).

## Ideas for SpacetimeDB

1. Return a generic 500 for handler panics and log the backtrace server-side (all three languages leak it today).
   In C#, catch handler exceptions in the generated dispatcher instead of rethrowing, so the instance isn't torn down.
2. Let `OPTIONS` reach a registered `.options()` handler, or document that it never will.
3. Offer a CSPRNG to handlers and procedures for tokens and secrets.
4. Wildcard / parameter routes (`*`, `:` and `{}` are already reserved in the source) and dots in paths. That alone would make static hosting easy.
5. A CPU limit for TypeScript handlers, like the wasm fuel budget.
6. Docs: `nest` doesn't hand "all paths that start with prefix" to the sub-router; it only prefixes the routes that sub-router registered.
