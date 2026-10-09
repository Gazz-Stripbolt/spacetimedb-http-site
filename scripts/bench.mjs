// Small HTTP load test: N keep-alive clients hammering one URL for a few seconds.
//
//   node scripts/bench.mjs [db] [secs] [concurrency]
//
// Hits the ping baseline, a static asset, JSON read from tables, the home page (which
// writes a hit counter in a transaction) and 1 MB bodies. Prints req/s and latency.
import http from 'node:http';

const [db = 'stdb-site', secs = '5', conc = '16'] = process.argv.slice(2);
const base = process.env.STDB_URL ?? 'http://127.0.0.1:3000';
const agent = new http.Agent({ keepAlive: true, maxSockets: Number(conc) });
const targets = [
  ['ping baseline', '/v1/ping'],
  ['static asset', `/v1/database/${db}/route/style-css`],
  ['JSON from a table', `/v1/database/${db}/route/api/stats`],
  ['page + write tx', `/v1/database/${db}/route/`],
  ['1 MB body', `/v1/database/${db}/route/api/big?kb=1024`],
];

function get(url) {
  return new Promise((resolve, reject) => {
    const t0 = performance.now();
    http
      .get(url, { agent }, (res) => {
        res.on('data', () => {});
        res.on('end', () => (res.statusCode === 200 ? resolve(performance.now() - t0) : reject(new Error(`${res.statusCode}`))));
      })
      .on('error', reject);
  });
}

for (const [name, path] of targets) {
  const url = base + path;
  const lat = [];
  const end = performance.now() + Number(secs) * 1000;
  await Promise.all(
    Array.from({ length: Number(conc) }, async () => {
      while (performance.now() < end) lat.push(await get(url));
    })
  );
  lat.sort((a, b) => a - b);
  const q = (p) => lat[Math.floor(p * (lat.length - 1))].toFixed(1);
  console.log(`${name.padEnd(18)} ${String(Math.round(lat.length / Number(secs))).padStart(6)} req/s   p50 ${q(0.5)} ms   p99 ${q(0.99)} ms`);
}
agent.destroy();
