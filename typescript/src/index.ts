/**
 * A whole website (pages, assets, JSON API, cookie sessions, server-rendered pages) served
 * from a single SpacetimeDB module through HTTP handlers. The TypeScript twin of rust/src/lib.rs.
 *
 * Every route lives under /v1/database/<db>/route, or under / when a reverse proxy sends
 * X-Site-Base (see proxy/Caddyfile). Run `npm run gen` first: it embeds ../site into src/site.gen.ts.
 */
import { schema, table, t, Router, SyncResponse, type Request } from 'spacetimedb/server';
import type { HandlerContext } from 'spacetimedb/server';
import { sha256 } from '@noble/hashes/sha2';
import { gzipSync } from 'fflate';
import { INDEX_HTML, APP_JS, STYLE_CSS, LOGO_SVG, ICON_PNG } from './site.gen';

// ---------- tables ----------

const post = table(
  { name: 'post', public: true },
  { id: t.u64().primaryKey().autoInc(), author: t.string(), body: t.string(), created: t.timestamp() }
);
const account = table({ name: 'account' }, { name: t.string().primaryKey(), salt: t.string(), passHash: t.string() });
const session = table({ name: 'session' }, { tokenHash: t.string().primaryKey(), name: t.string(), created: t.timestamp() });
const asset = table(
  { name: 'asset' },
  { name: t.string().primaryKey(), contentType: t.string(), etag: t.string(), data: t.byteArray() }
);
const hit = table({ name: 'hit' }, { path: t.string().primaryKey(), count: t.u64() });

/**
 * Owner-set secrets (`SITE_SECRET=... ADMIN_TOKEN=... spacetime publish`). The handler RNG is
 * seeded from the request timestamp, so session tokens mix in SITE_SECRET to stay unguessable.
 */
const spacetimedb = schema(
  { post, account, session, asset, hit },
  { env: { SITE_SECRET: t.string(), ADMIN_TOKEN: t.string() } }
);
export default spacetimedb;

type Ctx = HandlerContext<typeof spacetimedb.schemaType>;
type Db = Parameters<Parameters<Ctx['withTx']>[0]>[0]['db'];

export const init = spacetimedb.init((ctx) =>
  putAsset(ctx.db, 'hello-txt', 'text/plain; charset=utf-8', utf8('Hello from a table row!\n'))
);

// ---------- helpers ----------

const SESSION_COOKIE = 'sid';
const SESSION_TTL_MICROS = 7n * 24n * 3600n * 1_000_000n;
const enc = new TextEncoder();
const utf8 = (s: string) => enc.encode(s);
const hex = (b: Uint8Array) => Array.from(b, (x) => x.toString(16).padStart(2, '0')).join('');

/** SHA-256 over length-prefixed parts (same construction as the Rust version). */
function sha256Hex(...parts: Uint8Array[]): string {
  const h = sha256.create();
  for (const p of parts) {
    const len = new Uint8Array(8);
    new DataView(len.buffer).setBigUint64(0, BigInt(p.length), true);
    h.update(len).update(p);
  }
  return hex(h.digest());
}

function putAsset(db: Db, name: string, contentType: string, data: Uint8Array) {
  const row = { name, contentType, etag: `"${sha256Hex(data).slice(0, 16)}"`, data };
  if (db.asset.name.find(name)) db.asset.name.update(row);
  else db.asset.insert(row);
}

/** The request path. The URI may be absolute (it usually is) or path-only. */
function pathOf(req: Request): string {
  const uri = req.uri;
  const scheme = uri.indexOf('://');
  const start = scheme < 0 ? 0 : uri.indexOf('/', scheme + 3);
  if (start < 0) return '/';
  const end = uri.slice(start).search(/[?#]/);
  return end < 0 ? uri.slice(start) : uri.slice(start, start + end);
}

/**
 * The path prefix the browser sees: /v1/database/<db>/route, or X-Site-Base from a proxy.
 * Only plain path characters are accepted, since the value lands in HTML.
 */
function basePath(req: Request): string {
  const b = req.headers.get('x-site-base');
  if (b && b.startsWith('/') && /^[A-Za-z0-9\-_~/]+$/.test(b)) return b.replace(/\/+$/, '');
  const path = pathOf(req);
  const i = path.indexOf('/route');
  return i < 0 ? '' : path.slice(0, i + '/route'.length);
}

function percentDecode(s: string): string {
  try {
    return decodeURIComponent(s.replace(/\+/g, ' '));
  } catch {
    return s;
  }
}

function query(req: Request, key: string): string | undefined {
  const q = req.uri.indexOf('?');
  if (q < 0) return undefined;
  for (const pair of req.uri.slice(q + 1).split('#')[0].split('&')) {
    const eq = pair.indexOf('=');
    const [k, v] = eq < 0 ? [pair, ''] : [pair.slice(0, eq), pair.slice(eq + 1)];
    if (percentDecode(k) === key) return percentDecode(v);
  }
  return undefined;
}

function cookie(req: Request, key: string): string | undefined {
  for (const c of (req.headers.get('cookie') ?? '').split(/[;,]/)) {
    const kv = c.trim();
    const eq = kv.indexOf('=');
    if (eq > 0 && kv.slice(0, eq) === key) return kv.slice(eq + 1);
  }
  return undefined;
}

type Headers = [string, string][];
const respond = (status: number, contentType: string, body: string | Uint8Array, extra: Headers = []) =>
  new SyncResponse(body, { status, headers: [['content-type', contentType], ...extra] });
const json = (status: number, value: unknown, extra: Headers = []) =>
  respond(status, 'application/json', JSON.stringify(value), extra);
const error = (status: number, msg: string) => json(status, { error: msg });

function countHit(ctx: Ctx, path: string) {
  ctx.withTx((tx) => {
    const h = tx.db.hit.path.find(path);
    if (h) tx.db.hit.path.update({ ...h, count: h.count + 1n });
    else tx.db.hit.insert({ path, count: 1n });
  });
}

/** Name of the logged-in user, from the session cookie. */
function sessionUser(ctx: Ctx, req: Request): string | undefined {
  const token = cookie(req, SESSION_COOKIE);
  if (!token) return undefined;
  const hash = sha256Hex(utf8(token));
  return ctx.withTx((tx) => {
    const s = tx.db.session.tokenHash.find(hash);
    if (!s) return undefined;
    const age = tx.timestamp.microsSinceUnixEpoch - s.created.microsSinceUnixEpoch;
    return age < SESSION_TTL_MICROS ? s.name : undefined;
  });
}

function sessionCookie(base: string, value: string, maxAge: bigint): [string, string] {
  // Path scopes the cookie to this database's routes only, not the whole host.
  const path = base === '' ? '/' : base;
  return ['set-cookie', `${SESSION_COOKIE}=${value}; Path=${path}; Max-Age=${maxAge}; HttpOnly; SameSite=Lax`];
}

// ---------- pages and static assets ----------

const htmlEscape = (s: string) =>
  s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');

export const index = spacetimedb.httpHandler((ctx, req) => {
  countHit(ctx, 'index');
  const page = INDEX_HTML.replaceAll('{{BASE}}', basePath(req))
    .replaceAll('{{RENDERED}}', ctx.timestamp.toISOString())
    .replaceAll('{{LANG}}', 'TypeScript');
  return respond(200, 'text/html; charset=utf-8', page, [
    ['cache-control', 'no-cache'],
    ['content-security-policy', "default-src 'self'; img-src 'self' data:; connect-src 'self'"],
  ]);
});

const staticAsset = (contentType: string, body: string | Uint8Array) =>
  respond(200, contentType, body, [['cache-control', 'public, max-age=300']]);

export const appJs = spacetimedb.httpHandler(() => staticAsset('text/javascript; charset=utf-8', APP_JS));
export const styleCss = spacetimedb.httpHandler(() => staticAsset('text/css; charset=utf-8', STYLE_CSS));
export const logoSvg = spacetimedb.httpHandler(() => staticAsset('image/svg+xml', LOGO_SVG));
export const iconPng = spacetimedb.httpHandler(() => staticAsset('image/png', ICON_PNG));

/** Server-rendered permalink page: /post?id=N (no path params, so a query string). */
export const postPage = spacetimedb.httpHandler((ctx, req) => {
  const base = basePath(req);
  const raw = query(req, 'id');
  if (!raw || !/^\d+$/.test(raw)) return respond(400, 'text/plain', 'missing ?id=');
  const id = BigInt(raw);
  const p = ctx.withTx((tx) => tx.db.post.id.find(id));
  if (!p) return respond(404, 'text/html; charset=utf-8', '<h1>No such post</h1>');
  const page = `<!doctype html><html><head><meta charset="utf-8"><base href="${base}/">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Post #${id}</title><link rel="stylesheet" href="style-css"><link rel="icon" href="icon-png"></head>
<body><main><section class="card"><h2>Post #${id} by ${htmlEscape(p.author)}</h2><p>${htmlEscape(p.body)}</p>
<p class="meta">${p.created.toISOString()}</p><a href="">← back</a></section></main></body></html>`;
  return respond(200, 'text/html; charset=utf-8', page);
});

/** Assets stored in a table, so they can change without republishing: /asset?name=... */
export const getAsset = spacetimedb.httpHandler((ctx, req) => {
  const name = query(req, 'name');
  if (name === undefined) return error(400, 'missing ?name=');
  const a = ctx.withTx((tx) => tx.db.asset.name.find(name));
  if (!a) return error(404, 'no such asset');
  const headers: Headers = [
    ['etag', a.etag],
    ['cache-control', 'public, max-age=0, must-revalidate'],
  ];
  if (req.headers.get('if-none-match') === a.etag) return new SyncResponse(null, { status: 304, headers });
  return new SyncResponse(a.data, { status: 200, headers: [...headers, ['content-type', a.contentType]] });
});

function constantTimeEq(a: Uint8Array, b: Uint8Array): boolean {
  if (a.length !== b.length) return false;
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a[i] ^ b[i];
  return diff === 0;
}

/** PUT /asset?name=... with `Authorization: Bearer $ADMIN_TOKEN`, raw body, Content-Type kept. */
export const putAssetRoute = spacetimedb.httpHandler((ctx, req) => {
  const auth = req.headers.get('authorization');
  if (!auth || !constantTimeEq(utf8(auth), utf8(`Bearer ${ctx.env.ADMIN_TOKEN}`))) {
    return error(401, 'admin token required');
  }
  const name = query(req, 'name');
  if (name === undefined) return error(400, 'missing ?name=');
  const contentType = req.headers.get('content-type') ?? 'application/octet-stream';
  const data = req.bytes();
  ctx.withTx((tx) => putAsset(tx.db, name, contentType, data));
  return json(200, { name, bytes: data.length, content_type: contentType });
});

// ---------- JSON API: accounts and sessions ----------

function parseJson(req: Request): Record<string, unknown> | undefined {
  try {
    const v = req.json();
    return v && typeof v === 'object' ? v : undefined;
  } catch {
    return undefined;
  }
}

/**
 * 32 bytes of token material: timestamp-seeded RNG output mixed with the owner's secret,
 * so knowing the request time alone isn't enough to guess it.
 */
function newToken(ctx: Ctx, extra: Uint8Array): string {
  const micros = new Uint8Array(8);
  new DataView(micros.buffer).setBigInt64(0, ctx.timestamp.microsSinceUnixEpoch, true);
  return sha256Hex(utf8(ctx.env.SITE_SECRET), utf8(ctx.newUuidV4().toString()), micros, extra);
}

// Demo only: a real site should use a slow KDF (argon2/scrypt).
const hashPassword = (salt: string, password: string) => sha256Hex(utf8(salt), utf8(password));

function startSession(ctx: Ctx, base: string, name: string, extra: Uint8Array) {
  const token = newToken(ctx, extra);
  const tokenHash = sha256Hex(utf8(token));
  ctx.withTx((tx) => {
    // Prune expired sessions on the way in, so the table can't grow forever.
    const now = tx.timestamp.microsSinceUnixEpoch;
    for (const s of [...tx.db.session.iter()]) {
      if (now - s.created.microsSinceUnixEpoch >= SESSION_TTL_MICROS) tx.db.session.tokenHash.delete(s.tokenHash);
    }
    tx.db.session.insert({ tokenHash, name, created: tx.timestamp });
  });
  return json(200, { name }, [sessionCookie(base, token, SESSION_TTL_MICROS / 1_000_000n)]);
}

function credentials(req: Request): { name: string; password: string } | undefined {
  const body = parseJson(req);
  if (!body || typeof body.name !== 'string' || typeof body.password !== 'string') return undefined;
  return { name: body.name, password: body.password };
}

export const register = spacetimedb.httpHandler((ctx, req) => {
  const base = basePath(req);
  const creds = credentials(req);
  if (!creds) return error(400, 'bad JSON: expected {name, password}');
  const name = creds.name.trim().toLowerCase();
  if (name.length === 0 || utf8(name).length > 32 || creds.password.length < 6) {
    return error(400, 'name 1-32 chars, password 6+ chars');
  }
  const salt = newToken(ctx, utf8(name));
  const passHash = hashPassword(salt, creds.password);
  const created = ctx.withTx((tx) => {
    if (tx.db.account.name.find(name)) return false;
    tx.db.account.insert({ name, salt, passHash });
    return true;
  });
  if (!created) return error(409, 'name taken');
  return startSession(ctx, base, name, utf8(creds.password));
});

export const login = spacetimedb.httpHandler((ctx, req) => {
  const base = basePath(req);
  const creds = credentials(req);
  if (!creds) return error(400, 'bad JSON: expected {name, password}');
  const name = creds.name.trim().toLowerCase();
  const a = ctx.withTx((tx) => tx.db.account.name.find(name));
  if (!a || !constantTimeEq(utf8(hashPassword(a.salt, creds.password)), utf8(a.passHash))) {
    return error(401, 'wrong name or password');
  }
  return startSession(ctx, base, name, utf8(creds.password));
});

export const logout = spacetimedb.httpHandler((ctx, req) => {
  const base = basePath(req);
  const token = cookie(req, SESSION_COOKIE);
  if (token) {
    const hash = sha256Hex(utf8(token));
    ctx.withTx((tx) => tx.db.session.tokenHash.delete(hash));
  }
  return json(200, { ok: true }, [sessionCookie(base, '', 0n)]);
});

export const me = spacetimedb.httpHandler((ctx, req) => {
  const name = sessionUser(ctx, req);
  return name ? json(200, { name }) : error(401, 'not logged in');
});

// ---------- JSON API: posts ----------

/** GET lists, POST creates, DELETE ?id= removes. One path, branching on method. */
export const posts = spacetimedb.httpHandler((ctx, req) => {
  switch (req.method) {
    case 'GET': {
      const list = ctx.withTx((tx) =>
        [...tx.db.post.iter()]
          .sort((a, b) => Number(b.created.microsSinceUnixEpoch - a.created.microsSinceUnixEpoch))
          .slice(0, 50)
          .map((p) => ({
            id: Number(p.id),
            author: p.author,
            body: p.body,
            created_ms: Number(p.created.microsSinceUnixEpoch / 1000n),
          }))
      );
      return json(200, list);
    }
    case 'POST': {
      const author = sessionUser(ctx, req);
      if (!author) return error(401, 'log in first');
      const body = parseJson(req);
      if (!body || typeof body.body !== 'string') return error(400, 'bad JSON: expected {body}');
      const text = body.body.trim();
      if (text.length === 0 || utf8(text).length > 2000) return error(400, 'post must be 1-2000 chars');
      const id = ctx.withTx((tx) => tx.db.post.insert({ id: 0n, author, body: text, created: tx.timestamp }).id);
      return json(201, { id: Number(id) });
    }
    case 'DELETE': {
      const user = sessionUser(ctx, req);
      if (!user) return error(401, 'log in first');
      const raw = query(req, 'id');
      if (!raw || !/^\d+$/.test(raw)) return error(400, 'missing ?id=');
      const id = BigInt(raw);
      const deleted = ctx.withTx((tx) => {
        const p = tx.db.post.id.find(id);
        return !!p && p.author === user && tx.db.post.id.delete(id);
      });
      return deleted ? json(200, { deleted: Number(id) }) : error(404, 'no such post of yours');
    }
    default:
      return error(405, 'GET, POST or DELETE');
  }
});

// ---------- lab: probing the limits ----------

/** Echoes what the handler actually receives. */
export const echo = spacetimedb.httpHandler((ctx, req) => {
  const headers: Record<string, string> = {};
  req.headers.forEach((v, k) => (headers[k] = v));
  const body = req.bytes();
  return json(200, {
    method: req.method,
    uri: req.uri,
    version: String(req.version.tag ?? req.version),
    headers,
    body_len: body.length,
    body_preview: new TextDecoder().decode(body.subarray(0, 200)),
    handler_timestamp: ctx.timestamp.toISOString(),
  });
});

/** /api/big?kb=N: an N KiB response body. */
export const big = spacetimedb.httpHandler((_ctx, req) => {
  const kb = Number(query(req, 'kb') ?? 1024) || 1024;
  const line = utf8('0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde\n'); // 64 bytes
  const body = new Uint8Array(kb * 1024);
  for (let i = 0; i < body.length; i += 64) body.set(line, i);
  return respond(200, 'text/plain', body);
});

/** POST /api/upload: reports how many bytes arrived. */
export const upload = spacetimedb.httpHandler((_ctx, req) => json(200, { received_bytes: req.bytes().length }));

/** /api/spin?n=N: burns CPU for N million loop iterations (timeouts and serialization). */
export const spin = spacetimedb.httpHandler((_ctx, req) => {
  const n = Number(query(req, 'n') ?? 10) || 10;
  let x = 0;
  for (let i = 0; i < n * 1_000_000; i++) x = (Math.imul(x, 1664525) + i) | 0;
  return json(200, { iterations_millions: n, x });
});

/** Pre-compressed body with Content-Encoding: gzip. */
export const gzip = spacetimedb.httpHandler(() => {
  const html = '<p>This page was gzipped inside the module.</p>\n'.repeat(500);
  return respond(200, 'text/html; charset=utf-8', gzipSync(utf8(html)), [
    ['content-encoding', 'gzip'],
    ['x-uncompressed-bytes', String(html.length)],
  ]);
});

export const redirect = spacetimedb.httpHandler(
  (_ctx, req) => new SyncResponse(null, { status: 303, headers: [['location', `${basePath(req)}/`]] })
);

/** Outbound HTTP from inside a handler. */
export const outbound = spacetimedb.httpHandler((ctx, req) => {
  const url = query(req, 'url') ?? 'https://example.com/';
  try {
    const res = ctx.http.fetch(url);
    return json(200, { url, status: res.status, bytes: res.bytes().length });
  } catch (e) {
    return json(502, { url, error: String((e as Error)?.message ?? e) });
  }
});

export const panic = spacetimedb.httpHandler(() => {
  throw new Error('deliberate panic from /api/panic');
});

/** Writes a row then throws: does the write survive? */
export const writeThenPanic = spacetimedb.httpHandler((ctx) => {
  countHit(ctx, 'write-then-panic');
  throw new Error('threw after a committed withTx');
});

export const stats = spacetimedb.httpHandler((ctx) =>
  ctx.withTx((tx) => {
    const hits: Record<string, number> = {};
    for (const h of tx.db.hit.iter()) hits[h.path] = Number(h.count);
    return json(200, {
      hits,
      posts: Number(tx.db.post.count()),
      sessions: Number(tx.db.session.count()),
      assets: Number(tx.db.asset.count()),
    });
  })
);

/** Explicit OPTIONS handler, to compare with whatever the host does for preflights. */
export const preflight = spacetimedb.httpHandler(
  () =>
    new SyncResponse(null, {
      status: 204,
      headers: [
        ['access-control-allow-origin', 'https://example.org'],
        ['access-control-allow-methods', 'GET, POST, DELETE'],
        ['access-control-allow-headers', 'content-type, authorization'],
        ['x-from-handler', 'yes'],
      ],
    })
);

// ---------- routes ----------

const api = new Router()
  .any('/posts', posts)
  .post('/register', register)
  .post('/login', login)
  .post('/logout', logout)
  .get('/me', me)
  .any('/echo', echo)
  .get('/big', big)
  .post('/upload', upload)
  .get('/spin', spin)
  .get('/gzip', gzip)
  .get('/redirect', redirect)
  .get('/outbound', outbound)
  .get('/panic', panic)
  .get('/write-then-panic', writeThenPanic)
  .get('/stats', stats)
  .options('/cors', preflight)
  .get('/cors', stats);

export const router = spacetimedb.httpRouter(
  new Router()
    .get('', index)
    .get('/', index)
    .get('/app-js', appJs)
    .get('/style-css', styleCss)
    .get('/logo-svg', logoSvg)
    .get('/icon-png', iconPng)
    .get('/post', postPage)
    .get('/asset', getAsset)
    .put('/asset', putAssetRoute)
    .nest('/api', api)
);
