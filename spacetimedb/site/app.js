// All URLs are relative; <base href> (injected by the module) resolves them
// under /v1/database/<db>/route/.
const $ = (sel) => document.querySelector(sel);

async function api(path, opts = {}) {
  const res = await fetch(path, { credentials: "same-origin", ...opts });
  const text = await res.text();
  let data = text;
  try { data = JSON.parse(text); } catch {}
  return { status: res.status, ok: res.ok, headers: res.headers, data, text };
}

const esc = (s) => String(s).replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);

let me = null;

async function refreshMe() {
  const r = await api("api/me");
  me = r.ok ? r.data.name : null;
  $("#me").textContent = me ? `Logged in as ${me}` : "Not logged in.";
  $("#auth").hidden = !!me;
  $("#logout").hidden = !me;
  $("#new-post").hidden = !me;
}

async function refreshPosts() {
  const r = await api("api/posts");
  const ul = $("#posts");
  if (!r.ok) { ul.innerHTML = `<li>Error ${r.status}</li>`; return; }
  if (!r.data.length) { ul.innerHTML = `<li class="muted">No posts yet.</li>`; return; }
  ul.innerHTML = r.data.map((p) => `
    <li>
      <div>${esc(p.body)}</div>
      <div class="meta">${esc(p.author)} · ${new Date(p.created_ms).toLocaleString()} ·
        <a href="post?id=${p.id}">permalink (server-rendered)</a>
        ${p.author === me ? ` · <a href="#" data-del="${p.id}">delete</a>` : ""}</div>
    </li>`).join("");
}

$("#auth").addEventListener("submit", (e) => e.preventDefault());
$("#auth").addEventListener("click", async (e) => {
  const action = e.target.dataset.action;
  if (!action) return;
  e.preventDefault();
  const form = new FormData($("#auth"));
  const r = await api(`api/${action}`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ name: form.get("name"), password: form.get("password") }),
  });
  if (!r.ok) alert(r.data.error || r.text);
  await refreshMe(); await refreshPosts();
});

$("#logout").addEventListener("click", async () => {
  await api("api/logout", { method: "POST" });
  await refreshMe(); await refreshPosts();
});

$("#new-post").addEventListener("submit", async (e) => {
  e.preventDefault();
  const body = new FormData(e.target).get("body");
  const r = await api("api/posts", { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ body }) });
  if (!r.ok) alert(r.data.error || r.text);
  e.target.reset();
  await refreshPosts();
});

$("#posts").addEventListener("click", async (e) => {
  const id = e.target.dataset.del;
  if (!id) return;
  e.preventDefault();
  await api(`api/posts?id=${id}`, { method: "DELETE" });
  await refreshPosts();
});

const tests = {
  echo: () => api("api/echo?hello=world", { method: "POST", headers: { "x-custom": "abc" }, body: "ping" }),
  big: async () => { const t0 = performance.now(); const r = await api("api/big?kb=1024"); return { status: r.status, bytes: r.text.length, ms: Math.round(performance.now() - t0) }; },
  upload: async () => api("api/upload", { method: "POST", body: new Uint8Array(256 * 1024) }),
  gzip: async () => { const r = await api("api/gzip"); return { status: r.status, contentEncoding: r.headers.get("content-encoding"), decodedChars: r.text.length }; },
  etag: async () => {
    const a = await fetch("asset?name=hello-txt", { cache: "no-store" });
    const tag = a.headers.get("etag");
    const b = await fetch("asset?name=hello-txt", { cache: "no-store", headers: { "if-none-match": tag } });
    return { first: a.status, etag: tag, cacheControl: a.headers.get("cache-control"), second: b.status };
  },
  redirect: async () => { const r = await fetch("api/redirect"); return { finalStatus: r.status, redirected: r.redirected, finalUrl: r.url }; },
  outbound: () => api("api/outbound"),
  panic: () => api("api/panic"),
  stats: () => api("api/stats"),
};

document.querySelector(".lab").addEventListener("click", async (e) => {
  const t = e.target.dataset.test;
  if (!t) return;
  $("#lab-out").textContent = "…";
  try {
    const r = await tests[t]();
    const shown = r && r.headers instanceof Headers ? { status: r.status, body: r.data } : r;
    $("#lab-out").textContent = JSON.stringify(shown, null, 2);
  } catch (err) {
    $("#lab-out").textContent = String(err);
  }
});

refreshMe().then(refreshPosts);
setInterval(refreshPosts, 5000);
