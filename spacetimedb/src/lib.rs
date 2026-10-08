//! A whole website (pages, assets, JSON API, cookie sessions, server-rendered
//! pages) served from a single SpacetimeDB module through HTTP handlers.
//!
//! Every route lives under `/v1/database/<db>/route`, or under `/` when a
//! reverse proxy sends `X-Site-Base` (see `proxy/Caddyfile`).

use http::{Method, StatusCode, header};
use serde::Deserialize;
use serde_json::json;
use sha2::{Digest, Sha256};
use spacetimedb::http::{Body, HandlerContext, Request, Response, Router, handler, router};
use spacetimedb::{ReducerContext, Table, Timestamp, TxContext};

// ---------- configuration ----------

/// Owner-set secrets (`SITE_SECRET=... ADMIN_TOKEN=... spacetime publish`).
/// The handler RNG is seeded from the request timestamp, so session tokens
/// mix in SITE_SECRET to stay unguessable.
#[spacetimedb::env]
pub struct Env {
    pub SITE_SECRET: String,
    pub ADMIN_TOKEN: String,
}

// ---------- tables ----------

#[spacetimedb::table(accessor = post, public)]
pub struct Post {
    #[primary_key]
    #[auto_inc]
    id: u64,
    author: String,
    body: String,
    created: Timestamp,
}

#[spacetimedb::table(accessor = account)]
pub struct Account {
    #[primary_key]
    name: String,
    salt: String,
    pass_hash: String,
}

#[spacetimedb::table(accessor = session)]
pub struct Session {
    #[primary_key]
    token_hash: String,
    name: String,
    created: Timestamp,
}

#[spacetimedb::table(accessor = asset)]
pub struct Asset {
    #[primary_key]
    name: String,
    content_type: String,
    etag: String,
    data: Vec<u8>,
}

#[spacetimedb::table(accessor = hit)]
pub struct Hit {
    #[primary_key]
    path: String,
    count: u64,
}

#[spacetimedb::reducer(init)]
pub fn init(ctx: &ReducerContext) {
    put_asset(
        ctx,
        "hello-txt",
        "text/plain; charset=utf-8",
        b"Hello from a table row!\n".to_vec(),
    );
}

// ---------- helpers ----------

const SESSION_COOKIE: &str = "sid";
const SESSION_TTL_MICROS: i64 = 7 * 24 * 3600 * 1_000_000;

fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}

fn sha256_hex(parts: &[&[u8]]) -> String {
    let mut h = Sha256::new();
    for p in parts {
        h.update((p.len() as u64).to_le_bytes());
        h.update(p);
    }
    hex(&h.finalize())
}

fn put_asset(ctx: &ReducerContext, name: &str, content_type: &str, data: Vec<u8>) {
    let etag = format!("\"{}\"", &sha256_hex(&[&data])[..16]);
    let row = Asset {
        name: name.into(),
        content_type: content_type.into(),
        etag,
        data,
    };
    if ctx.db.asset().name().find(&row.name).is_some() {
        ctx.db.asset().name().update(row);
    } else {
        ctx.db.asset().insert(row);
    }
}

/// The path prefix the *browser* sees for this site. Normally that's
/// `/v1/database/<db>/route`. Behind a reverse proxy that maps a domain's `/`
/// onto the route prefix, the proxy sends `X-Site-Base` (e.g. `/`) instead.
/// Only plain path characters are accepted, since the value lands in HTML.
/// `req.uri()` may be absolute (Maincloud) or path-only, so take the path either way.
fn base_path(req: &Request) -> String {
    if let Some(base) = req
        .headers()
        .get("x-site-base")
        .and_then(|v| v.to_str().ok())
        && base.starts_with('/')
        && base
            .chars()
            .all(|c| c.is_ascii_alphanumeric() || "-_~/".contains(c))
    {
        return base.trim_end_matches('/').to_string();
    }
    let path = req.uri().path();
    match path.find("/route") {
        Some(i) => path[..i + "/route".len()].to_string(),
        None => String::new(),
    }
}

fn percent_decode(s: &str) -> String {
    let bytes = s.as_bytes();
    let mut out = Vec::with_capacity(bytes.len());
    let mut i = 0;
    while i < bytes.len() {
        let decoded = (bytes[i] == b'%' && i + 2 < bytes.len())
            .then(|| std::str::from_utf8(&bytes[i + 1..i + 3]).ok())
            .flatten()
            .and_then(|h| u8::from_str_radix(h, 16).ok());
        match (bytes[i], decoded) {
            (b'%', Some(b)) => {
                out.push(b);
                i += 2;
            }
            (b'+', _) => out.push(b' '),
            (b, _) => out.push(b),
        }
        i += 1;
    }
    String::from_utf8_lossy(&out).into_owned()
}

fn query(req: &Request, key: &str) -> Option<String> {
    req.uri().query()?.split('&').find_map(|pair| {
        let (k, v) = pair.split_once('=').unwrap_or((pair, ""));
        (percent_decode(k) == key).then(|| percent_decode(v))
    })
}

fn cookie(req: &Request, key: &str) -> Option<String> {
    req.headers().get_all(header::COOKIE).iter().find_map(|v| {
        v.to_str().ok()?.split(';').find_map(|c| {
            let (k, v) = c.trim().split_once('=')?;
            (k == key).then(|| v.to_string())
        })
    })
}

fn respond(status: StatusCode, content_type: &str, body: impl Into<Body>) -> Response {
    Response::builder()
        .status(status)
        .header(header::CONTENT_TYPE, content_type)
        .body(body.into())
        .unwrap()
}

fn json_response(status: StatusCode, value: serde_json::Value) -> Response {
    respond(status, "application/json", value.to_string())
}

fn error(status: StatusCode, msg: &str) -> Response {
    json_response(status, json!({ "error": msg }))
}

fn count_hit(ctx: &mut HandlerContext, path: &str) {
    ctx.with_tx(|tx| match tx.db.hit().path().find(path.to_string()) {
        Some(mut h) => {
            h.count += 1;
            tx.db.hit().path().update(h);
        }
        None => {
            tx.db.hit().insert(Hit {
                path: path.to_string(),
                count: 1,
            });
        }
    });
}

/// Name of the logged-in user, from the session cookie.
fn current_user(tx: &TxContext, req_token: &Option<String>) -> Option<String> {
    let token = req_token.as_ref()?;
    let s = tx
        .db
        .session()
        .token_hash()
        .find(sha256_hex(&[token.as_bytes()]))?;
    let age = tx.timestamp.to_micros_since_unix_epoch() - s.created.to_micros_since_unix_epoch();
    (age < SESSION_TTL_MICROS).then_some(s.name)
}

fn session_user(ctx: &mut HandlerContext, req: &Request) -> Option<String> {
    let token = cookie(req, SESSION_COOKIE);
    ctx.with_tx(|tx| current_user(tx, &token))
}

fn session_cookie(base: &str, value: &str, max_age: i64) -> String {
    // Path scopes the cookie to this database's routes only, not the whole host.
    let path = if base.is_empty() { "/" } else { base };
    format!("{SESSION_COOKIE}={value}; Path={path}; Max-Age={max_age}; HttpOnly; SameSite=Lax")
}

// ---------- pages and static assets ----------

const INDEX_HTML: &str = include_str!("../site/index.html");
const APP_JS: &str = include_str!("../site/app.js");
const STYLE_CSS: &str = include_str!("../site/style.css");
const LOGO_SVG: &str = include_str!("../site/logo.svg");
const ICON_PNG: &[u8] = include_bytes!("../site/icon.png");

fn html_escape(s: &str) -> String {
    s.replace('&', "&amp;")
        .replace('<', "&lt;")
        .replace('>', "&gt;")
        .replace('"', "&quot;")
}

#[handler]
fn index(ctx: &mut HandlerContext, req: Request) -> Response {
    count_hit(ctx, "index");
    let page = INDEX_HTML
        .replace("{{BASE}}", &base_path(&req))
        .replace("{{RENDERED}}", &ctx.timestamp.to_string());
    let mut res = respond(StatusCode::OK, "text/html; charset=utf-8", page);
    let h = res.headers_mut();
    h.insert(header::CACHE_CONTROL, "no-cache".parse().unwrap());
    h.insert(
        header::CONTENT_SECURITY_POLICY,
        "default-src 'self'; img-src 'self' data:; connect-src 'self'"
            .parse()
            .unwrap(),
    );
    res
}

fn static_asset(content_type: &str, body: impl Into<Body>) -> Response {
    let mut res = respond(StatusCode::OK, content_type, body);
    res.headers_mut().insert(
        header::CACHE_CONTROL,
        "public, max-age=300".parse().unwrap(),
    );
    res
}

#[handler]
fn app_js(_ctx: &mut HandlerContext, _req: Request) -> Response {
    static_asset("text/javascript; charset=utf-8", APP_JS)
}

#[handler]
fn style_css(_ctx: &mut HandlerContext, _req: Request) -> Response {
    static_asset("text/css; charset=utf-8", STYLE_CSS)
}

#[handler]
fn logo_svg(_ctx: &mut HandlerContext, _req: Request) -> Response {
    static_asset("image/svg+xml", LOGO_SVG)
}

#[handler]
fn icon_png(_ctx: &mut HandlerContext, _req: Request) -> Response {
    static_asset("image/png", ICON_PNG)
}

/// Server-rendered permalink page: /post?id=N (no path params, so a query string).
#[handler]
fn post_page(ctx: &mut HandlerContext, req: Request) -> Response {
    let base = base_path(&req);
    let Some(id) = query(&req, "id").and_then(|s| s.parse::<u64>().ok()) else {
        return respond(StatusCode::BAD_REQUEST, "text/plain", "missing ?id=");
    };
    let Some(p) = ctx.with_tx(|tx| tx.db.post().id().find(id)) else {
        return respond(
            StatusCode::NOT_FOUND,
            "text/html; charset=utf-8",
            "<h1>No such post</h1>",
        );
    };
    let page = format!(
        r#"<!doctype html><html><head><meta charset="utf-8"><base href="{base}/">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Post #{id}</title><link rel="stylesheet" href="style-css"><link rel="icon" href="icon-png"></head>
<body><main><section class="card"><h2>Post #{id} by {author}</h2><p>{body}</p>
<p class="meta">{created}</p><a href="">← back</a></section></main></body></html>"#,
        author = html_escape(&p.author),
        body = html_escape(&p.body),
        created = p.created,
    );
    respond(StatusCode::OK, "text/html; charset=utf-8", page)
}

/// Assets stored in a table, so they can change without republishing: /asset?name=...
#[handler]
fn get_asset(ctx: &mut HandlerContext, req: Request) -> Response {
    let Some(name) = query(&req, "name") else {
        return error(StatusCode::BAD_REQUEST, "missing ?name=");
    };
    let Some(a) = ctx.with_tx(|tx| tx.db.asset().name().find(&name)) else {
        return error(StatusCode::NOT_FOUND, "no such asset");
    };
    let not_modified = req
        .headers()
        .get(header::IF_NONE_MATCH)
        .is_some_and(|v| v.as_bytes() == a.etag.as_bytes());
    let builder = Response::builder()
        .header(header::ETAG, &a.etag)
        .header(header::CACHE_CONTROL, "public, max-age=0, must-revalidate");
    if not_modified {
        return builder
            .status(StatusCode::NOT_MODIFIED)
            .body(Body::empty())
            .unwrap();
    }
    builder
        .status(StatusCode::OK)
        .header(header::CONTENT_TYPE, &a.content_type)
        .body(Body::from_bytes(a.data))
        .unwrap()
}

/// PUT /asset?name=... with `Authorization: Bearer $ADMIN_TOKEN`, raw body, Content-Type kept.
#[handler]
fn put_asset_route(ctx: &mut HandlerContext, req: Request) -> Response {
    let expected = format!("Bearer {}", ctx.env.ADMIN_TOKEN());
    let authorized = req
        .headers()
        .get(header::AUTHORIZATION)
        .is_some_and(|v| constant_time_eq(v.as_bytes(), expected.as_bytes()));
    if !authorized {
        return error(StatusCode::UNAUTHORIZED, "admin token required");
    }
    let Some(name) = query(&req, "name") else {
        return error(StatusCode::BAD_REQUEST, "missing ?name=");
    };
    let content_type = req
        .headers()
        .get(header::CONTENT_TYPE)
        .and_then(|v| v.to_str().ok())
        .unwrap_or("application/octet-stream")
        .to_string();
    let data = req.into_body().into_bytes().to_vec();
    let size = data.len();
    ctx.with_tx(|tx| put_asset(tx, &name, &content_type, data.clone()));
    json_response(
        StatusCode::OK,
        json!({ "name": name, "bytes": size, "content_type": content_type }),
    )
}

fn constant_time_eq(a: &[u8], b: &[u8]) -> bool {
    a.len() == b.len() && a.iter().zip(b).fold(0u8, |acc, (x, y)| acc | (x ^ y)) == 0
}

// ---------- JSON API: accounts and sessions ----------

#[derive(Deserialize)]
struct Credentials {
    name: String,
    password: String,
}

fn parse_json<T: for<'de> Deserialize<'de>>(req: Request) -> Result<T, Response> {
    serde_json::from_slice(&req.into_body().into_bytes())
        .map_err(|e| error(StatusCode::BAD_REQUEST, &format!("bad JSON: {e}")))
}

/// 32 bytes of token material: timestamp-seeded RNG output mixed with the
/// owner's secret, so knowing the request time alone isn't enough to guess it.
fn new_token(ctx: &HandlerContext, extra: &[u8]) -> String {
    let rand = ctx.new_uuid_v4().map(|u| u.to_string()).unwrap_or_default();
    let micros = ctx.timestamp.to_micros_since_unix_epoch().to_le_bytes();
    sha256_hex(&[
        ctx.env.SITE_SECRET().as_bytes(),
        rand.as_bytes(),
        &micros,
        extra,
    ])
}

fn hash_password(salt: &str, password: &str) -> String {
    // Demo only: a real site should use a slow KDF (argon2/scrypt), which also compiles to wasm.
    sha256_hex(&[salt.as_bytes(), password.as_bytes()])
}

fn start_session(ctx: &mut HandlerContext, base: &str, name: &str, extra: &[u8]) -> Response {
    let token = new_token(ctx, extra);
    let token_hash = sha256_hex(&[token.as_bytes()]);
    let name = name.to_string();
    ctx.with_tx(|tx| {
        // Prune expired sessions on the way in, so the table can't grow forever.
        let now = tx.timestamp.to_micros_since_unix_epoch();
        let expired: Vec<String> = tx
            .db
            .session()
            .iter()
            .filter(|s| now - s.created.to_micros_since_unix_epoch() >= SESSION_TTL_MICROS)
            .map(|s| s.token_hash)
            .collect();
        for t in &expired {
            tx.db.session().token_hash().delete(t);
        }
        tx.db.session().insert(Session {
            token_hash: token_hash.clone(),
            name: name.clone(),
            created: tx.timestamp,
        });
    });
    let mut res = json_response(StatusCode::OK, json!({ "name": name }));
    res.headers_mut().insert(
        header::SET_COOKIE,
        session_cookie(base, &token, SESSION_TTL_MICROS / 1_000_000)
            .parse()
            .unwrap(),
    );
    res
}

#[handler]
fn register(ctx: &mut HandlerContext, req: Request) -> Response {
    let base = base_path(&req);
    let creds: Credentials = match parse_json(req) {
        Ok(c) => c,
        Err(r) => return r,
    };
    let name = creds.name.trim().to_lowercase();
    if name.is_empty() || name.len() > 32 || creds.password.len() < 6 {
        return error(
            StatusCode::BAD_REQUEST,
            "name 1-32 chars, password 6+ chars",
        );
    }
    let salt = new_token(ctx, name.as_bytes());
    let pass_hash = hash_password(&salt, &creds.password);
    let created = ctx.with_tx(|tx| {
        if tx.db.account().name().find(&name).is_some() {
            return false;
        }
        tx.db.account().insert(Account {
            name: name.clone(),
            salt: salt.clone(),
            pass_hash: pass_hash.clone(),
        });
        true
    });
    if !created {
        return error(StatusCode::CONFLICT, "name taken");
    }
    start_session(ctx, &base, &name, creds.password.as_bytes())
}

#[handler]
fn login(ctx: &mut HandlerContext, req: Request) -> Response {
    let base = base_path(&req);
    let creds: Credentials = match parse_json(req) {
        Ok(c) => c,
        Err(r) => return r,
    };
    let name = creds.name.trim().to_lowercase();
    let ok = ctx
        .with_tx(|tx| tx.db.account().name().find(&name))
        .is_some_and(|a| {
            constant_time_eq(
                hash_password(&a.salt, &creds.password).as_bytes(),
                a.pass_hash.as_bytes(),
            )
        });
    if !ok {
        return error(StatusCode::UNAUTHORIZED, "wrong name or password");
    }
    start_session(ctx, &base, &name, creds.password.as_bytes())
}

#[handler]
fn logout(ctx: &mut HandlerContext, req: Request) -> Response {
    let base = base_path(&req);
    if let Some(token) = cookie(&req, SESSION_COOKIE) {
        let token_hash = sha256_hex(&[token.as_bytes()]);
        ctx.with_tx(|tx| tx.db.session().token_hash().delete(&token_hash));
    }
    let mut res = json_response(StatusCode::OK, json!({ "ok": true }));
    res.headers_mut().insert(
        header::SET_COOKIE,
        session_cookie(&base, "", 0).parse().unwrap(),
    );
    res
}

#[handler]
fn me(ctx: &mut HandlerContext, req: Request) -> Response {
    match session_user(ctx, &req) {
        Some(name) => json_response(StatusCode::OK, json!({ "name": name })),
        None => error(StatusCode::UNAUTHORIZED, "not logged in"),
    }
}

// ---------- JSON API: posts ----------

#[derive(Deserialize)]
struct NewPost {
    body: String,
}

/// GET lists, POST creates, DELETE ?id= removes. One path, branching on method.
#[handler]
fn posts(ctx: &mut HandlerContext, req: Request) -> Response {
    match *req.method() {
        Method::GET => {
            let mut list: Vec<_> = ctx.with_tx(|tx| {
                tx.db
                    .post()
                    .iter()
                    .map(|p| {
                        json!({
                            "id": p.id, "author": p.author, "body": p.body,
                            "created_ms": p.created.to_micros_since_unix_epoch() / 1000,
                        })
                    })
                    .collect()
            });
            list.sort_by_key(|p| std::cmp::Reverse(p["created_ms"].as_i64()));
            list.truncate(50);
            json_response(StatusCode::OK, json!(list))
        }
        Method::POST => {
            let Some(author) = session_user(ctx, &req) else {
                return error(StatusCode::UNAUTHORIZED, "log in first");
            };
            let new: NewPost = match parse_json(req) {
                Ok(p) => p,
                Err(r) => return r,
            };
            let body = new.body.trim().to_string();
            if body.is_empty() || body.len() > 2000 {
                return error(StatusCode::BAD_REQUEST, "post must be 1-2000 chars");
            }
            let id = ctx.with_tx(|tx| {
                tx.db
                    .post()
                    .insert(Post {
                        id: 0,
                        author: author.clone(),
                        body: body.clone(),
                        created: tx.timestamp,
                    })
                    .id
            });
            json_response(StatusCode::CREATED, json!({ "id": id }))
        }
        Method::DELETE => {
            let Some(user) = session_user(ctx, &req) else {
                return error(StatusCode::UNAUTHORIZED, "log in first");
            };
            let Some(id) = query(&req, "id").and_then(|s| s.parse::<u64>().ok()) else {
                return error(StatusCode::BAD_REQUEST, "missing ?id=");
            };
            let deleted = ctx.with_tx(|tx| match tx.db.post().id().find(id) {
                Some(p) if p.author == user => tx.db.post().id().delete(id),
                _ => false,
            });
            if deleted {
                json_response(StatusCode::OK, json!({ "deleted": id }))
            } else {
                error(StatusCode::NOT_FOUND, "no such post of yours")
            }
        }
        _ => error(StatusCode::METHOD_NOT_ALLOWED, "GET, POST or DELETE"),
    }
}

// ---------- lab: probing the limits ----------

/// Echoes what the handler actually receives.
#[handler]
fn echo(ctx: &mut HandlerContext, req: Request) -> Response {
    let headers: serde_json::Map<String, serde_json::Value> = req
        .headers()
        .iter()
        .map(|(k, v)| (k.to_string(), json!(v.to_str().unwrap_or("<binary>"))))
        .collect();
    let method = req.method().to_string();
    let uri = req.uri().to_string();
    let version = format!("{:?}", req.version());
    let body = req.into_body().into_bytes();
    json_response(
        StatusCode::OK,
        json!({
            "method": method, "uri": uri, "version": version, "headers": headers,
            "body_len": body.len(), "body_preview": String::from_utf8_lossy(&body[..body.len().min(200)]),
            "handler_timestamp": ctx.timestamp.to_string(),
        }),
    )
}

/// /api/big?kb=N: an N KiB response body.
#[handler]
fn big(_ctx: &mut HandlerContext, req: Request) -> Response {
    let kb: usize = query(&req, "kb")
        .and_then(|s| s.parse().ok())
        .unwrap_or(1024);
    let line = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde\n"; // 64 bytes
    respond(StatusCode::OK, "text/plain", line.repeat(kb * 16))
}

/// POST /api/upload: reports how many bytes arrived.
#[handler]
fn upload(_ctx: &mut HandlerContext, req: Request) -> Response {
    let len = req.into_body().into_bytes().len();
    json_response(StatusCode::OK, json!({ "received_bytes": len }))
}

/// /api/spin?n=N: burns CPU for N million loop iterations (timeouts and serialization).
#[handler]
fn spin(_ctx: &mut HandlerContext, req: Request) -> Response {
    let n: u64 = query(&req, "n").and_then(|s| s.parse().ok()).unwrap_or(10);
    let mut x: u64 = 0;
    for i in 0..n * 1_000_000 {
        x = std::hint::black_box(x.wrapping_mul(6364136223846793005).wrapping_add(i));
    }
    json_response(StatusCode::OK, json!({ "iterations_millions": n, "x": x }))
}

/// Pre-compressed body with Content-Encoding: gzip.
#[handler]
fn gzip(_ctx: &mut HandlerContext, _req: Request) -> Response {
    use flate2::{Compression, write::GzEncoder};
    use std::io::Write;
    let html = "<p>This page was gzipped inside the module.</p>\n".repeat(500);
    let mut enc = GzEncoder::new(Vec::new(), Compression::default());
    enc.write_all(html.as_bytes()).unwrap();
    let gz = enc.finish().unwrap();
    Response::builder()
        .header(header::CONTENT_TYPE, "text/html; charset=utf-8")
        .header(header::CONTENT_ENCODING, "gzip")
        .header("x-uncompressed-bytes", html.len())
        .body(Body::from_bytes(gz))
        .unwrap()
}

#[handler]
fn redirect(_ctx: &mut HandlerContext, req: Request) -> Response {
    Response::builder()
        .status(StatusCode::SEE_OTHER)
        .header(header::LOCATION, format!("{}/", base_path(&req)))
        .body(Body::empty())
        .unwrap()
}

/// Outbound HTTP from inside a handler.
#[handler]
fn outbound(ctx: &mut HandlerContext, req: Request) -> Response {
    let url = query(&req, "url").unwrap_or_else(|| "https://example.com/".into());
    match ctx.http.get(url.as_str()) {
        Ok(res) => {
            let status = res.status().as_u16();
            let len = res.into_body().into_bytes().len();
            json_response(
                StatusCode::OK,
                json!({ "url": url, "status": status, "bytes": len }),
            )
        }
        Err(e) => json_response(
            StatusCode::BAD_GATEWAY,
            json!({ "url": url, "error": e.to_string() }),
        ),
    }
}

#[handler]
fn panic_route(_ctx: &mut HandlerContext, _req: Request) -> Response {
    panic!("deliberate panic from /api/panic");
}

/// Writes a row then panics: does the write survive?
#[handler]
fn write_then_panic(ctx: &mut HandlerContext, _req: Request) -> Response {
    count_hit(ctx, "write-then-panic");
    panic!("panicked after a committed with_tx");
}

#[handler]
fn stats(ctx: &mut HandlerContext, _req: Request) -> Response {
    let (hits, post_count, sessions, assets) = ctx.with_tx(|tx| {
        let hits: serde_json::Map<_, _> = tx
            .db
            .hit()
            .iter()
            .map(|h| (h.path, json!(h.count)))
            .collect();
        (
            hits,
            tx.db.post().count(),
            tx.db.session().count(),
            tx.db.asset().count(),
        )
    });
    json_response(
        StatusCode::OK,
        json!({ "hits": hits, "posts": post_count, "sessions": sessions, "assets": assets }),
    )
}

/// Explicit OPTIONS handler, to compare with whatever the host does for preflights.
#[handler]
fn preflight(_ctx: &mut HandlerContext, _req: Request) -> Response {
    Response::builder()
        .status(StatusCode::NO_CONTENT)
        .header("access-control-allow-origin", "https://example.org")
        .header("access-control-allow-methods", "GET, POST, DELETE")
        .header(
            "access-control-allow-headers",
            "content-type, authorization",
        )
        .header("x-from-handler", "yes")
        .body(Body::empty())
        .unwrap()
}

// ---------- routes ----------

#[router]
fn routes() -> Router {
    let api = Router::new()
        .any("/posts", posts)
        .post("/register", register)
        .post("/login", login)
        .post("/logout", logout)
        .get("/me", me)
        .any("/echo", echo)
        .get("/big", big)
        .post("/upload", upload)
        .get("/spin", spin)
        .get("/gzip", gzip)
        .get("/redirect", redirect)
        .get("/outbound", outbound)
        .get("/panic", panic_route)
        .get("/write-then-panic", write_then_panic)
        .get("/stats", stats)
        .options("/cors", preflight)
        .get("/cors", stats);

    Router::new()
        .get("", index)
        .get("/", index)
        .get("/app-js", app_js)
        .get("/style-css", style_css)
        .get("/logo-svg", logo_svg)
        .get("/icon-png", icon_png)
        .get("/post", post_page)
        .get("/asset", get_asset)
        .put("/asset", put_asset_route)
        .nest("/api", api)
}
