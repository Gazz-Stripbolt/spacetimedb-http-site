// A whole website (pages, assets, JSON API, cookie sessions, server-rendered pages)
// served from a single SpacetimeDB module through HTTP handlers. The C# twin of rust/src/lib.rs.
//
// Every route lives under /v1/database/<db>/route, or under / when a reverse proxy
// sends X-Site-Base (see proxy/Caddyfile).

// HTTP handlers are unstable in 2.11 (like the Rust `unstable` feature).
#pragma warning disable STDB_UNSTABLE

using System.Text;
using System.Text.Json.Nodes;
using SpacetimeDB;

public static partial class Module
{
    // ---------- configuration ----------

    /// <summary>
    /// Owner-set secrets (`SITE_SECRET=... ADMIN_TOKEN=... spacetime publish`). The handler RNG is
    /// seeded from the request timestamp, so session tokens mix in SITE_SECRET to stay unguessable.
    /// </summary>
    [SpacetimeDB.Env]
    public partial struct SiteEnvironment
    {
        public string SITE_SECRET;
        public string ADMIN_TOKEN;
    }

    static ModuleEnvironment SiteEnv => default;

    // ---------- tables ----------

    [SpacetimeDB.Table(Accessor = "Post", Public = true)]
    public partial struct Post
    {
        [SpacetimeDB.PrimaryKey, SpacetimeDB.AutoInc]
        public ulong Id;
        public string Author;
        public string Body;
        public Timestamp Created;
    }

    [SpacetimeDB.Table(Accessor = "Account")]
    public partial struct Account
    {
        [SpacetimeDB.PrimaryKey]
        public string Name;
        public string Salt;
        public string PassHash;
    }

    [SpacetimeDB.Table(Accessor = "Session")]
    public partial struct Session
    {
        [SpacetimeDB.PrimaryKey]
        public string TokenHash;
        public string Name;
        public Timestamp Created;
    }

    [SpacetimeDB.Table(Accessor = "Asset")]
    public partial struct Asset
    {
        [SpacetimeDB.PrimaryKey]
        public string Name;
        public string ContentType;
        public string Etag;
        public byte[] Data;
    }

    [SpacetimeDB.Table(Accessor = "Hit")]
    public partial struct Hit
    {
        [SpacetimeDB.PrimaryKey]
        public string Path;
        public ulong Count;
    }

    [SpacetimeDB.Reducer(ReducerKind.Init)]
    public static void Init(ReducerContext ctx) =>
        PutAsset(ctx.Db, "hello-txt", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Hello from a table row!\n"));

    // ---------- helpers ----------

    const string SessionCookie = "sid";
    const long SessionTtlMicros = 7L * 24 * 3600 * 1_000_000;

    static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    /// <summary>SHA-256 over length-prefixed parts (same construction as the Rust version).</summary>
    static string Sha256Hex(params byte[][] parts)
    {
        using var m = new MemoryStream();
        foreach (var p in parts)
        {
            m.Write(BitConverter.GetBytes((ulong)p.Length));
            m.Write(p);
        }
        return Hex(Sha256.Hash(m.ToArray()));
    }

    static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    static void PutAsset(Local db, string name, string contentType, byte[] data)
    {
        var row = new Asset { Name = name, ContentType = contentType, Etag = $"\"{Sha256Hex(data)[..16]}\"", Data = data };
        if (db.Asset.Name.Find(name) is not null) db.Asset.Name.Update(row);
        else db.Asset.Insert(row);
    }

    static string? Header(HttpRequest req, string name) =>
        req.Headers.Where(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase))
            .Select(h => Encoding.UTF8.GetString(h.Value))
            .FirstOrDefault();

    /// <summary>The request path. The URI may be absolute (it usually is) or path-only.</summary>
    static string PathOf(HttpRequest req)
    {
        var uri = req.Uri;
        var scheme = uri.IndexOf("://", StringComparison.Ordinal);
        var start = scheme < 0 ? 0 : uri.IndexOf('/', scheme + 3);
        if (start < 0) return "/";
        var end = uri.IndexOfAny(['?', '#'], start);
        return end < 0 ? uri[start..] : uri[start..end];
    }

    /// <summary>
    /// The path prefix the browser sees: /v1/database/&lt;db&gt;/route, or X-Site-Base from a proxy.
    /// Only plain path characters are accepted, since the value lands in HTML.
    /// </summary>
    static string BasePath(HttpRequest req)
    {
        var b = Header(req, "x-site-base");
        if (b is not null && b.StartsWith('/') && b.All(c => char.IsAsciiLetterOrDigit(c) || "-_~/".Contains(c)))
        {
            return b.TrimEnd('/');
        }
        var path = PathOf(req);
        var i = path.IndexOf("/route", StringComparison.Ordinal);
        return i < 0 ? "" : path[..(i + "/route".Length)];
    }

    static string PercentDecode(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));

    static string? Query(HttpRequest req, string key)
    {
        var q = req.Uri.IndexOf('?');
        if (q < 0) return null;
        var query = req.Uri[(q + 1)..];
        var hash = query.IndexOf('#');
        if (hash >= 0) query = query[..hash];
        foreach (var pair in query.Split('&'))
        {
            var eq = pair.IndexOf('=');
            var (k, v) = eq < 0 ? (pair, "") : (pair[..eq], pair[(eq + 1)..]);
            if (PercentDecode(k) == key) return PercentDecode(v);
        }
        return null;
    }

    static string? Cookie(HttpRequest req, string key)
    {
        foreach (var h in req.Headers.Where(h => string.Equals(h.Name, "cookie", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var c in Encoding.UTF8.GetString(h.Value).Split(';'))
            {
                var kv = c.Trim();
                var eq = kv.IndexOf('=');
                if (eq > 0 && kv[..eq] == key) return kv[(eq + 1)..];
            }
        }
        return null;
    }

    static HttpResponse Respond(ushort status, string contentType, byte[] body, params HttpHeader[] extra)
    {
        var headers = new List<HttpHeader> { new("content-type", contentType) };
        headers.AddRange(extra);
        return new(status, HttpVersion.Http11, headers, new HttpBody(body));
    }

    static HttpResponse Respond(ushort status, string contentType, string body, params HttpHeader[] extra) =>
        Respond(status, contentType, Utf8(body), extra);

    static HttpResponse Json(ushort status, JsonNode value, params HttpHeader[] extra) =>
        Respond(status, "application/json", value.ToJsonString(), extra);

    static HttpResponse Error(ushort status, string msg) => Json(status, new JsonObject { ["error"] = msg });

    static void CountHit(HandlerContext ctx, string path) =>
        ctx.WithTx(tx =>
        {
            if (tx.Db.Hit.Path.Find(path) is { } h) tx.Db.Hit.Path.Update(h with { Count = h.Count + 1 });
            else tx.Db.Hit.Insert(new Hit { Path = path, Count = 1 });
            return 0;
        });

    /// <summary>Name of the logged-in user, from the session cookie.</summary>
    static string? SessionUser(HandlerContext ctx, HttpRequest req)
    {
        var token = Cookie(req, SessionCookie);
        if (token is null) return null;
        var hash = Sha256Hex(Utf8(token));
        return ctx.WithTx(tx =>
        {
            if (tx.Db.Session.TokenHash.Find(hash) is not { } s) return null;
            var age = tx.Timestamp.MicrosecondsSinceUnixEpoch - s.Created.MicrosecondsSinceUnixEpoch;
            return age < SessionTtlMicros ? s.Name : null;
        });
    }

    static HttpHeader SessionCookieHeader(string @base, string value, long maxAge)
    {
        // Path scopes the cookie to this database's routes only, not the whole host.
        var path = @base == "" ? "/" : @base;
        return new("set-cookie", $"{SessionCookie}={value}; Path={path}; Max-Age={maxAge}; HttpOnly; SameSite=Lax");
    }

    // ---------- pages and static assets ----------

    static byte[] Resource(string name)
    {
        using var s = typeof(Module).Assembly.GetManifestResourceStream(name)!;
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }

    static readonly Lazy<string> IndexHtml = new(() => Encoding.UTF8.GetString(Resource("index.html")));
    static readonly Lazy<byte[]> AppJs = new(() => Resource("app.js"));
    static readonly Lazy<byte[]> StyleCss = new(() => Resource("style.css"));
    static readonly Lazy<byte[]> LogoSvg = new(() => Resource("logo.svg"));
    static readonly Lazy<byte[]> IconPng = new(() => Resource("icon.png"));

    static string HtmlEscape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    [SpacetimeDB.HttpHandler]
    public static HttpResponse Index(HandlerContext ctx, HttpRequest req)
    {
        CountHit(ctx, "index");
        var page = IndexHtml.Value
            .Replace("{{BASE}}", BasePath(req))
            .Replace("{{RENDERED}}", ctx.Timestamp.ToString())
            .Replace("{{LANG}}", "C#");
        return Respond(200, "text/html; charset=utf-8", page,
            new("cache-control", "no-cache"),
            new("content-security-policy", "default-src 'self'; img-src 'self' data:; connect-src 'self'"));
    }

    static HttpResponse StaticAsset(string contentType, byte[] body) =>
        Respond(200, contentType, body, new HttpHeader("cache-control", "public, max-age=300"));

    [SpacetimeDB.HttpHandler]
    public static HttpResponse AppJsRoute(HandlerContext ctx, HttpRequest req) => StaticAsset("text/javascript; charset=utf-8", AppJs.Value);

    [SpacetimeDB.HttpHandler]
    public static HttpResponse StyleCssRoute(HandlerContext ctx, HttpRequest req) => StaticAsset("text/css; charset=utf-8", StyleCss.Value);

    [SpacetimeDB.HttpHandler]
    public static HttpResponse LogoSvgRoute(HandlerContext ctx, HttpRequest req) => StaticAsset("image/svg+xml", LogoSvg.Value);

    [SpacetimeDB.HttpHandler]
    public static HttpResponse IconPngRoute(HandlerContext ctx, HttpRequest req) => StaticAsset("image/png", IconPng.Value);

    /// <summary>Server-rendered permalink page: /post?id=N (no path params, so a query string).</summary>
    [SpacetimeDB.HttpHandler]
    public static HttpResponse PostPage(HandlerContext ctx, HttpRequest req)
    {
        var @base = BasePath(req);
        if (!ulong.TryParse(Query(req, "id"), out var id)) return Respond(400, "text/plain", "missing ?id=");
        var found = ctx.WithTx(tx => tx.Db.Post.Id.Find(id));
        if (found is not { } p) return Respond(404, "text/html; charset=utf-8", "<h1>No such post</h1>");
        var page = $"""
            <!doctype html><html><head><meta charset="utf-8"><base href="{@base}/">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Post #{id}</title><link rel="stylesheet" href="style-css"><link rel="icon" href="icon-png"></head>
            <body><main><section class="card"><h2>Post #{id} by {HtmlEscape(p.Author)}</h2><p>{HtmlEscape(p.Body)}</p>
            <p class="meta">{p.Created}</p><a href="">← back</a></section></main></body></html>
            """;
        return Respond(200, "text/html; charset=utf-8", page);
    }

    /// <summary>Assets stored in a table, so they can change without republishing: /asset?name=...</summary>
    [SpacetimeDB.HttpHandler]
    public static HttpResponse GetAsset(HandlerContext ctx, HttpRequest req)
    {
        if (Query(req, "name") is not { } name) return Error(400, "missing ?name=");
        if (ctx.WithTx(tx => tx.Db.Asset.Name.Find(name)) is not { } a) return Error(404, "no such asset");
        var headers = new List<HttpHeader> { new("etag", a.Etag), new("cache-control", "public, max-age=0, must-revalidate") };
        if (Header(req, "if-none-match") == a.Etag) return new(304, HttpVersion.Http11, headers, HttpBody.Empty);
        headers.Add(new("content-type", a.ContentType));
        return new(200, HttpVersion.Http11, headers, new HttpBody(a.Data));
    }

    /// <summary>PUT /asset?name=... with `Authorization: Bearer $ADMIN_TOKEN`, raw body, Content-Type kept.</summary>
    [SpacetimeDB.HttpHandler]
    public static HttpResponse PutAssetRoute(HandlerContext ctx, HttpRequest req)
    {
        var expected = Utf8($"Bearer {SiteEnv.ADMIN_TOKEN}");
        var auth = req.Headers.FirstOrDefault(h => string.Equals(h.Name, "authorization", StringComparison.OrdinalIgnoreCase));
        if (auth.Value is null || !ConstantTimeEq(auth.Value, expected)) return Error(401, "admin token required");
        if (Query(req, "name") is not { } name) return Error(400, "missing ?name=");
        var contentType = Header(req, "content-type") ?? "application/octet-stream";
        var data = req.Body.ToBytes();
        ctx.WithTx(tx =>
        {
            PutAsset(tx.Db, name, contentType, data);
            return 0;
        });
        return Json(200, new JsonObject { ["name"] = name, ["bytes"] = data.Length, ["content_type"] = contentType });
    }

    static bool ConstantTimeEq(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        var diff = 0;
        for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }

    // ---------- JSON API: accounts and sessions ----------

    static JsonObject? ParseJson(HttpRequest req)
    {
        try
        {
            return JsonNode.Parse(req.Body.ToBytes()) as JsonObject;
        }
        catch (Exception)
        {
            return null;
        }
    }

    static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>
    /// 32 bytes of token material: timestamp-seeded RNG output mixed with the owner's secret,
    /// so knowing the request time alone isn't enough to guess it.
    /// </summary>
    static string NewToken(HandlerContext ctx, byte[] extra)
    {
        var rand = new byte[16];
        ctx.Rng.NextBytes(rand);
        return Sha256Hex(Utf8(SiteEnv.SITE_SECRET), rand, BitConverter.GetBytes(ctx.Timestamp.MicrosecondsSinceUnixEpoch), extra);
    }

    // Demo only: a real site should use a slow KDF (argon2/scrypt).
    static string HashPassword(string salt, string password) => Sha256Hex(Utf8(salt), Utf8(password));

    static HttpResponse StartSession(HandlerContext ctx, string @base, string name, byte[] extra)
    {
        var token = NewToken(ctx, extra);
        var tokenHash = Sha256Hex(Utf8(token));
        ctx.WithTx(tx =>
        {
            // Prune expired sessions on the way in, so the table can't grow forever.
            var now = tx.Timestamp.MicrosecondsSinceUnixEpoch;
            foreach (var s in tx.Db.Session.Iter().Where(s => now - s.Created.MicrosecondsSinceUnixEpoch >= SessionTtlMicros).ToList())
            {
                tx.Db.Session.TokenHash.Delete(s.TokenHash);
            }
            tx.Db.Session.Insert(new Session { TokenHash = tokenHash, Name = name, Created = tx.Timestamp });
            return 0;
        });
        return Json(200, new JsonObject { ["name"] = name }, SessionCookieHeader(@base, token, SessionTtlMicros / 1_000_000));
    }

    [SpacetimeDB.HttpHandler]
    public static HttpResponse Register(HandlerContext ctx, HttpRequest req)
    {
        var @base = BasePath(req);
        if (ParseJson(req) is not { } body || Str(body, "name") is not { } rawName || Str(body, "password") is not { } password)
        {
            return Error(400, "bad JSON: expected {name, password}");
        }
        var name = rawName.Trim().ToLowerInvariant();
        if (name.Length == 0 || Utf8(name).Length > 32 || password.Length < 6)
        {
            return Error(400, "name 1-32 chars, password 6+ chars");
        }
        var salt = NewToken(ctx, Utf8(name));
        var passHash = HashPassword(salt, password);
        var created = ctx.WithTx(tx =>
        {
            if (tx.Db.Account.Name.Find(name) is not null) return false;
            tx.Db.Account.Insert(new Account { Name = name, Salt = salt, PassHash = passHash });
            return true;
        });
        if (!created) return Error(409, "name taken");
        return StartSession(ctx, @base, name, Utf8(password));
    }

    [SpacetimeDB.HttpHandler]
    public static HttpResponse Login(HandlerContext ctx, HttpRequest req)
    {
        var @base = BasePath(req);
        if (ParseJson(req) is not { } body || Str(body, "name") is not { } rawName || Str(body, "password") is not { } password)
        {
            return Error(400, "bad JSON: expected {name, password}");
        }
        var name = rawName.Trim().ToLowerInvariant();
        var account = ctx.WithTx(tx => tx.Db.Account.Name.Find(name));
        var ok = account is { } a && ConstantTimeEq(Utf8(HashPassword(a.Salt, password)), Utf8(a.PassHash));
        if (!ok) return Error(401, "wrong name or password");
        return StartSession(ctx, @base, name, Utf8(password));
    }

    [SpacetimeDB.HttpHandler]
    public static HttpResponse Logout(HandlerContext ctx, HttpRequest req)
    {
        var @base = BasePath(req);
        if (Cookie(req, SessionCookie) is { } token)
        {
            var hash = Sha256Hex(Utf8(token));
            ctx.WithTx(tx => tx.Db.Session.TokenHash.Delete(hash));
        }
        return Json(200, new JsonObject { ["ok"] = true }, SessionCookieHeader(@base, "", 0));
    }

    [SpacetimeDB.HttpHandler]
    public static HttpResponse Me(HandlerContext ctx, HttpRequest req) =>
        SessionUser(ctx, req) is { } name ? Json(200, new JsonObject { ["name"] = name }) : Error(401, "not logged in");

    // ---------- JSON API: posts ----------

    /// <summary>GET lists, POST creates, DELETE ?id= removes. One path, branching on method.</summary>
    [SpacetimeDB.HttpHandler]
    public static HttpResponse Posts(HandlerContext ctx, HttpRequest req)
    {
        switch (req.Method.Value)
        {
            case "GET":
            {
                var list = ctx.WithTx(tx => tx.Db.Post.Iter()
                    .OrderByDescending(p => p.Created.MicrosecondsSinceUnixEpoch)
                    .Take(50)
                    .Select(p => (JsonNode)new JsonObject
                    {
                        ["id"] = p.Id,
                        ["author"] = p.Author,
                        ["body"] = p.Body,
                        ["created_ms"] = p.Created.MicrosecondsSinceUnixEpoch / 1000,
                    })
                    .ToArray());
                return Json(200, new JsonArray(list));
            }
            case "POST":
            {
                if (SessionUser(ctx, req) is not { } author) return Error(401, "log in first");
                if (ParseJson(req) is not { } json || Str(json, "body") is not { } raw) return Error(400, "bad JSON: expected {body}");
                var body = raw.Trim();
                if (body.Length == 0 || Utf8(body).Length > 2000) return Error(400, "post must be 1-2000 chars");
                var id = ctx.WithTx(tx => tx.Db.Post.Insert(new Post { Id = 0, Author = author, Body = body, Created = tx.Timestamp }).Id);
                return Json(201, new JsonObject { ["id"] = id });
            }
            case "DELETE":
            {
                if (SessionUser(ctx, req) is not { } user) return Error(401, "log in first");
                if (!ulong.TryParse(Query(req, "id"), out var id)) return Error(400, "missing ?id=");
                var deleted = ctx.WithTx(tx => tx.Db.Post.Id.Find(id) is { } p && p.Author == user && tx.Db.Post.Id.Delete(id));
                return deleted ? Json(200, new JsonObject { ["deleted"] = id }) : Error(404, "no such post of yours");
            }
            default:
                return Error(405, "GET, POST or DELETE");
        }
    }

    // ---------- lab: probing the limits ----------

    /// <summary>Echoes what the handler actually receives.</summary>
    [SpacetimeDB.HttpHandler]
    public static HttpResponse Echo(HandlerContext ctx, HttpRequest req)
    {
        var headers = new JsonObject();
        foreach (var h in req.Headers) headers[h.Name] = Encoding.UTF8.GetString(h.Value);
        var body = req.Body.ToBytes();
        return Json(200, new JsonObject
        {
            ["method"] = req.Method.Value,
            ["uri"] = req.Uri,
            ["version"] = req.Version.ToString(),
            ["headers"] = headers,
            ["body_len"] = body.Length,
            ["body_preview"] = Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, 200)),
            ["handler_timestamp"] = ctx.Timestamp.ToString(),
        });
    }

    /// <summary>/api/big?kb=N: an N KiB response body.</summary>
    [SpacetimeDB.HttpHandler]
    public static HttpResponse Big(HandlerContext ctx, HttpRequest req)
    {
        var kb = int.TryParse(Query(req, "kb"), out var n) ? n : 1024;
        var line = Utf8("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde\n"); // 64 bytes
        var body = new byte[kb * 1024];
        for (var i = 0; i < body.Length; i += 64) line.CopyTo(body, i);
        return Respond(200, "text/plain", body);
    }

    /// <summary>POST /api/upload: reports how many bytes arrived.</summary>
    [SpacetimeDB.HttpHandler]
    public static HttpResponse Upload(HandlerContext ctx, HttpRequest req) =>
        Json(200, new JsonObject { ["received_bytes"] = req.Body.ToBytes().Length });

    /// <summary>/api/spin?n=N: burns CPU for N million loop iterations (timeouts and serialization).</summary>
    [SpacetimeDB.HttpHandler]
    public static HttpResponse Spin(HandlerContext ctx, HttpRequest req)
    {
        var n = ulong.TryParse(Query(req, "n"), out var v) ? v : 10;
        ulong x = 0;
        for (ulong i = 0; i < n * 1_000_000; i++) x = unchecked(x * 6364136223846793005UL + i);
        return Json(200, new JsonObject { ["iterations_millions"] = n, ["x"] = x });
    }

    /// <summary>Pre-compressed body with Content-Encoding: gzip.</summary>
    [SpacetimeDB.HttpHandler]
    public static HttpResponse Gzip(HandlerContext ctx, HttpRequest req)
    {
        var html = Utf8(string.Concat(Enumerable.Repeat("<p>This page was gzipped inside the module.</p>\n", 500)));
        // System.IO.Compression needs native zlib, which NativeAOT on wasi doesn't ship (GZipStream aborts),
        // so this uses the small managed encoder at the bottom of the file.
        return Respond(200, "text/html; charset=utf-8", MiniGzip.Compress(html),
            new HttpHeader("content-encoding", "gzip"), new HttpHeader("x-uncompressed-bytes", html.Length.ToString()));
    }

    [SpacetimeDB.HttpHandler]
    public static HttpResponse Redirect(HandlerContext ctx, HttpRequest req) =>
        new(303, HttpVersion.Http11, new List<HttpHeader> { new("location", $"{BasePath(req)}/") }, HttpBody.Empty);

    /// <summary>Outbound HTTP from inside a handler.</summary>
    [SpacetimeDB.HttpHandler]
    public static HttpResponse Outbound(HandlerContext ctx, HttpRequest req)
    {
        var url = Query(req, "url") ?? "https://example.com/";
        return ctx.Http.Get(url).Match(
            res => Json(200, new JsonObject { ["url"] = url, ["status"] = res.StatusCode, ["bytes"] = res.Body.ToBytes().Length }),
            err => Json(502, new JsonObject { ["url"] = url, ["error"] = err.Message }));
    }

    [SpacetimeDB.HttpHandler]
    public static HttpResponse Panic(HandlerContext ctx, HttpRequest req) =>
        throw new Exception("deliberate panic from /api/panic");

    /// <summary>Writes a row then throws: does the write survive?</summary>
    [SpacetimeDB.HttpHandler]
    public static HttpResponse WriteThenPanic(HandlerContext ctx, HttpRequest req)
    {
        CountHit(ctx, "write-then-panic");
        throw new Exception("threw after a committed WithTx");
    }

    [SpacetimeDB.HttpHandler]
    public static HttpResponse Stats(HandlerContext ctx, HttpRequest req) =>
        ctx.WithTx(tx =>
        {
            var hits = new JsonObject();
            foreach (var h in tx.Db.Hit.Iter()) hits[h.Path] = h.Count;
            return Json(200, new JsonObject
            {
                ["hits"] = hits,
                ["posts"] = tx.Db.Post.Count,
                ["sessions"] = tx.Db.Session.Count,
                ["assets"] = tx.Db.Asset.Count,
            });
        });

    /// <summary>Explicit OPTIONS handler, to compare with whatever the host does for preflights.</summary>
    [SpacetimeDB.HttpHandler]
    public static HttpResponse Preflight(HandlerContext ctx, HttpRequest req) =>
        new(204, HttpVersion.Http11, new List<HttpHeader>
        {
            new("access-control-allow-origin", "https://example.org"),
            new("access-control-allow-methods", "GET, POST, DELETE"),
            new("access-control-allow-headers", "content-type, authorization"),
            new("x-from-handler", "yes"),
        }, HttpBody.Empty);

    // ---------- routes ----------

    [SpacetimeDB.HttpRouter]
    public static Router Routes()
    {
        var api = Router.New()
            .Any("/posts", Handlers.Posts)
            .Post("/register", Handlers.Register)
            .Post("/login", Handlers.Login)
            .Post("/logout", Handlers.Logout)
            .Get("/me", Handlers.Me)
            .Any("/echo", Handlers.Echo)
            .Get("/big", Handlers.Big)
            .Post("/upload", Handlers.Upload)
            .Get("/spin", Handlers.Spin)
            .Get("/gzip", Handlers.Gzip)
            .Get("/redirect", Handlers.Redirect)
            .Get("/outbound", Handlers.Outbound)
            .Get("/panic", Handlers.Panic)
            .Get("/write-then-panic", Handlers.WriteThenPanic)
            .Get("/stats", Handlers.Stats)
            .Options("/cors", Handlers.Preflight)
            .Get("/cors", Handlers.Stats);

        return Router.New()
            .Get("", Handlers.Index)
            .Get("/", Handlers.Index)
            .Get("/app-js", Handlers.AppJsRoute)
            .Get("/style-css", Handlers.StyleCssRoute)
            .Get("/logo-svg", Handlers.LogoSvgRoute)
            .Get("/icon-png", Handlers.IconPngRoute)
            .Get("/post", Handlers.PostPage)
            .Get("/asset", Handlers.GetAsset)
            .Put("/asset", Handlers.PutAssetRoute)
            .Nest("/api", api);
    }
}

/// <summary>Managed SHA-256: System.Security.Cryptography isn't available on wasi.</summary>
static class Sha256
{
    static readonly uint[] K =
    {
        0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
        0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
        0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
        0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
        0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
        0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
        0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
        0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
    };

    static uint Rotr(uint x, int n) => (x >> n) | (x << (32 - n));

    public static byte[] Hash(byte[] data)
    {
        uint[] h = { 0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19 };
        var bitLen = (ulong)data.LongLength * 8;
        var padded = new byte[((data.Length + 9 + 63) / 64) * 64];
        data.CopyTo(padded, 0);
        padded[data.Length] = 0x80;
        for (var i = 0; i < 8; i++) padded[padded.Length - 1 - i] = (byte)(bitLen >> (8 * i));
        var w = new uint[64];
        for (var chunk = 0; chunk < padded.Length; chunk += 64)
        {
            for (var i = 0; i < 16; i++)
                w[i] = (uint)(padded[chunk + 4 * i] << 24 | padded[chunk + 4 * i + 1] << 16 | padded[chunk + 4 * i + 2] << 8 | padded[chunk + 4 * i + 3]);
            for (var i = 16; i < 64; i++)
            {
                var s0 = Rotr(w[i - 15], 7) ^ Rotr(w[i - 15], 18) ^ (w[i - 15] >> 3);
                var s1 = Rotr(w[i - 2], 17) ^ Rotr(w[i - 2], 19) ^ (w[i - 2] >> 10);
                w[i] = w[i - 16] + s0 + w[i - 7] + s1;
            }
            uint a = h[0], b = h[1], c = h[2], d = h[3], e = h[4], f = h[5], g = h[6], hh = h[7];
            for (var i = 0; i < 64; i++)
            {
                var t1 = hh + (Rotr(e, 6) ^ Rotr(e, 11) ^ Rotr(e, 25)) + ((e & f) ^ (~e & g)) + K[i] + w[i];
                var t2 = (Rotr(a, 2) ^ Rotr(a, 13) ^ Rotr(a, 22)) + ((a & b) ^ (a & c) ^ (b & c));
                hh = g; g = f; f = e; e = d + t1; d = c; c = b; b = a; a = t1 + t2;
            }
            h[0] += a; h[1] += b; h[2] += c; h[3] += d; h[4] += e; h[5] += f; h[6] += g; h[7] += hh;
        }
        var result = new byte[32];
        for (var i = 0; i < 8; i++)
        {
            result[4 * i] = (byte)(h[i] >> 24); result[4 * i + 1] = (byte)(h[i] >> 16);
            result[4 * i + 2] = (byte)(h[i] >> 8); result[4 * i + 3] = (byte)h[i];
        }
        return result;
    }
}

/// <summary>
/// A minimal gzip encoder: greedy LZ77 over a 32 KiB window, one fixed-Huffman DEFLATE block.
/// Good enough for text; it exists because System.IO.Compression isn't usable on wasi.
/// </summary>
static class MiniGzip
{
    static readonly int[] LenBase = { 3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258 };
    static readonly int[] LenExtra = { 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0 };
    static readonly int[] DistBase = { 1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577 };
    static readonly int[] DistExtra = { 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13 };
    static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    sealed class Bits
    {
        public readonly List<byte> Out = new();
        uint acc;
        int n;

        public void Write(uint value, int count)
        {
            acc |= value << n;
            n += count;
            while (n >= 8) { Out.Add((byte)acc); acc >>= 8; n -= 8; }
        }

        /// <summary>Huffman codes go out most-significant bit first.</summary>
        public void Code(uint code, int len)
        {
            uint rev = 0;
            for (var i = 0; i < len; i++) rev |= ((code >> i) & 1) << (len - 1 - i);
            Write(rev, len);
        }

        public void Flush() { if (n > 0) Out.Add((byte)acc); acc = 0; n = 0; }
    }

    static void Literal(Bits b, int v)
    {
        if (v < 144) b.Code((uint)(0x30 + v), 8);
        else if (v < 256) b.Code((uint)(0x190 + v - 144), 9);
        else if (v < 280) b.Code((uint)(v - 256), 7);
        else b.Code((uint)(0xC0 + v - 280), 8);
    }

    public static byte[] Compress(byte[] data)
    {
        var b = new Bits();
        b.Write(1, 1); // BFINAL
        b.Write(1, 2); // BTYPE = fixed Huffman
        var head = new Dictionary<int, int>();
        var i = 0;
        while (i < data.Length)
        {
            int bestLen = 0, bestDist = 0;
            if (i + 2 < data.Length)
            {
                var key = data[i] << 16 | data[i + 1] << 8 | data[i + 2];
                if (head.TryGetValue(key, out var j) && i - j <= 32768)
                {
                    var len = 0;
                    while (len < 258 && i + len < data.Length && data[j + len] == data[i + len]) len++;
                    if (len >= 3) { bestLen = len; bestDist = i - j; }
                }
                head[key] = i;
            }
            if (bestLen == 0)
            {
                Literal(b, data[i]);
                i++;
                continue;
            }
            var li = Array.FindLastIndex(LenBase, x => x <= bestLen);
            Literal(b, 257 + li);
            b.Write((uint)(bestLen - LenBase[li]), LenExtra[li]);
            var di = Array.FindLastIndex(DistBase, x => x <= bestDist);
            b.Code((uint)di, 5);
            b.Write((uint)(bestDist - DistBase[di]), DistExtra[di]);
            for (var k = 1; k < bestLen && i + k + 2 < data.Length; k++) head[data[i + k] << 16 | data[i + k + 1] << 8 | data[i + k + 2]] = i + k;
            i += bestLen;
        }
        Literal(b, 256); // end of block
        b.Flush();

        var crc = 0xFFFFFFFFu;
        foreach (var x in data) crc = CrcTable[(crc ^ x) & 0xFF] ^ (crc >> 8);
        crc ^= 0xFFFFFFFFu;
        var gz = new List<byte> { 0x1f, 0x8b, 8, 0, 0, 0, 0, 0, 0, 0xff };
        gz.AddRange(b.Out);
        gz.AddRange(BitConverter.GetBytes(crc));
        gz.AddRange(BitConverter.GetBytes((uint)data.Length));
        return gz.ToArray();
    }
}
