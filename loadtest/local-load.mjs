// Local scale test (no AWS): N monitors on the demo target, checked every minute by the in-process worker (DynamoDB Local).
// It exercises the same Scheduler / Checker / Evaluator code as AWS and reads the in-memory metrics from /dev/metrics.
//
//   docker compose up -d dynamodb mailhog
//   cd backend && ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/StatusHub.Api --no-launch-profile
//   node loadtest/local-load.mjs --users 25 --per-user 20 --minutes 5
//
// What it measures is the application logic and the single machine, NOT the AWS network: use loadtest/load.js on AWS for that.
const arg = (name, def) => {
  const i = process.argv.indexOf(`--${name}`);
  return i > 0 ? process.argv[i + 1] : def;
};
const API = arg('api', 'http://localhost:5080').replace(/\/$/, '');
const USERS = parseInt(arg('users', '25'), 10);
const PER_USER = parseInt(arg('per-user', '20'), 10);
const MINUTES = parseFloat(arg('minutes', '5'));
const run = Date.now().toString(36);

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const json = (r) => r.json();

async function token(email) {
  return (await json(await fetch(`${API}/dev/token`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ email }) }))).token;
}

await fetch(`${API}/dev/metrics/reset`, { method: 'POST' });

const accounts = [];
const t0 = Date.now();
for (let n = 1; n <= USERS; n++) {
  const tk = await token(`local-load-${run}-${n}@example.com`);
  const h = { authorization: `Bearer ${tk}`, 'content-type': 'application/json' };
  const ids = [];
  await Promise.all(Array.from({ length: PER_USER }, async (_, i) => {
    const r = await fetch(`${API}/api/v1/monitors`, { method: 'POST', headers: h, body: JSON.stringify({
      name: `load-${n}-${i}`, url: `${API}/demo/target?m=${run}-${n}-${i}`, method: 'GET', intervalMinutes: 1, expectedStatus: 200, timeoutSeconds: 5 }) });
    if (r.status === 201) ids.push((await r.json()).id);
  }));
  accounts.push({ n, h, ids });
}
const total = accounts.reduce((a, x) => a + x.ids.length, 0);
console.log(`created ${total} monitors in ${((Date.now() - t0) / 1000).toFixed(1)} s; running for ${MINUTES} min...`);

// Behave like dashboard users while the pipeline runs and record the API latency.
const apiMs = [];
const end = Date.now() + MINUTES * 60_000;
while (Date.now() < end) {
  const a = accounts[Math.floor(Math.random() * accounts.length)];
  const s = performance.now();
  const r = await fetch(`${API}/api/v1/monitors`, { headers: a.h });
  await r.arrayBuffer();
  apiMs.push(performance.now() - s);
  await sleep(500);
}

const m = await json(await fetch(`${API}/dev/metrics`));
const q = (xs, p) => { const s = [...xs].sort((a, b) => a - b); return s[Math.max(0, Math.ceil(p * s.length) - 1)]; };

let executedPerMonitor = 0;
for (const a of accounts) for (const id of a.ids) {
  const st = await json(await fetch(`${API}/api/v1/monitors/${id}/stats`, { headers: a.h }));
  executedPerMonitor += st.checks24h ?? 0;
}

const g = (name, f) => m[name]?.[f] ?? 0;
const scheduled = g('ChecksScheduled', 'sum');
const executed = g('ChecksExecuted', 'samples');
console.log('\n| Measure | Value |\n|---|---|');
console.log(`| Monitors | ${total} |`);
console.log(`| Duration | ${MINUTES} min |`);
console.log(`| Checks scheduled | ${scheduled} |`);
console.log(`| Checks executed | ${executed} |`);
console.log(`| Results stored (sum of checks24h over all monitors) | ${executedPerMonitor} |`);
console.log(`| Throughput | ${(executed / (MINUTES * 60)).toFixed(2)} checks/s (${(executed / MINUTES).toFixed(0)} / min) |`);
console.log(`| Check latency p50 / p95 / p99 (ms) | ${g('CheckLatencyMs', 'p50').toFixed(0)} / ${g('CheckLatencyMs', 'p95').toFixed(0)} / ${g('CheckLatencyMs', 'p99').toFixed(0)} |`);
console.log(`| Delay after scheduled minute p50 / p95 / p99 / max (s) | ${g('CheckDelaySeconds', 'p50').toFixed(2)} / ${g('CheckDelaySeconds', 'p95').toFixed(2)} / ${g('CheckDelaySeconds', 'p99').toFixed(2)} / ${g('CheckDelaySeconds', 'max').toFixed(2)} |`);
console.log(`| Late checks (> 60 s) | ${g('ChecksLate', 'sum')} |`);
console.log(`| On-time share | ${scheduled ? ((100 * g('ChecksOnTime', 'sum')) / scheduled).toFixed(3) : 'n/a'} % |`);
console.log(`| Lost checks (scheduled - executed) | ${Math.max(0, scheduled - executed)} |`);
console.log(`| Targets reported down | ${g('ChecksFailed', 'sum')} |`);
console.log(`| API GET /monitors (20 monitors) p50 / p95 (ms) | ${q(apiMs, 0.5).toFixed(0)} / ${q(apiMs, 0.95).toFixed(0)} (${apiMs.length} calls) |`);

if (arg('keep') !== '1') {
  for (const a of accounts) await Promise.all(a.ids.map((id) => fetch(`${API}/api/v1/monitors/${id}`, { method: 'DELETE', headers: a.h })));
  console.log('\nmonitors deleted.');
}
