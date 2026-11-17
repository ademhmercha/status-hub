// Failure test: cut the demo target and measure how long StatusHub takes to open the incident and send the alert.
//
//   local : node loadtest/failure-test.mjs --api http://localhost:5080 --mode local --email you@example.com [--mailhog http://localhost:8026]
//   AWS   : STATUSHUB_TOKEN=<Cognito ID token> node loadtest/failure-test.mjs --api https://xxx.execute-api.eu-north-1.amazonaws.com \
//           --mode aws --env dev [--profile teamchat]
//
// Timeline printed: t0 target cut -> incident opened -> alert sent -> target restored -> incident resolved -> recovery alert.
// An incident opens after 2 consecutive failures, so with 1-minute checks the theoretical best is ~1-2 minutes.
import { spawnSync } from 'node:child_process';

const arg = (name, def) => {
  const i = process.argv.indexOf(`--${name}`);
  return i > 0 ? process.argv[i + 1] : def;
};
const API = (arg('api') ?? '').replace(/\/$/, '');
const MODE = arg('mode', 'local');
const ENV = arg('env', 'dev');
const PROFILE = arg('profile', process.env.AWS_PROFILE ?? 'teamchat');
const MAILHOG = (arg('mailhog', 'http://localhost:8026')).replace(/\/$/, '');
const EMAIL = arg('email', 'failure-test@example.com');
const TIMEOUT_MS = 10 * 60 * 1000;
if (!API) { console.error('--api is required'); process.exit(2); }

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const fmt = (ms) => `${(ms / 1000).toFixed(1)} s`;

async function token() {
  if (process.env.STATUSHUB_TOKEN) return process.env.STATUSHUB_TOKEN;
  if (MODE !== 'local') throw new Error('set STATUSHUB_TOKEN to a Cognito ID token');
  const r = await fetch(`${API}/dev/token`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ email: EMAIL }) });
  return (await r.json()).token;
}

const TOKEN = await token();
const call = async (method, path, body) => {
  const r = await fetch(`${API}${path}`, { method, headers: { authorization: `Bearer ${TOKEN}`, 'content-type': 'application/json' }, body: body ? JSON.stringify(body) : undefined });
  if (!r.ok && r.status !== 404) throw new Error(`${method} ${path} -> ${r.status} ${await r.text()}`);
  return r.status === 204 ? null : r.json();
};

async function until(label, probe, interval = 2000) {
  const t = Date.now();
  while (Date.now() - t < TIMEOUT_MS) {
    const v = await probe();
    if (v) return v;
    await sleep(interval);
  }
  throw new Error(`timeout waiting for: ${label}`);
}

// --- alert detection ------------------------------------------------------------------------------------------------
function awsAlertTime(sinceMs, text) {
  const out = spawnSync('aws', [
    'logs', 'filter-log-events', '--profile', PROFILE, '--region', 'eu-north-1', '--output', 'json',
    '--log-group-name', `/aws/lambda/statushub-${ENV}-notifier`, '--start-time', String(sinceMs), '--filter-pattern', `"Alert sent" "${text}"`,
  ], { encoding: 'utf8' });
  if (out.status !== 0) return null;
  const ev = JSON.parse(out.stdout).events[0];
  return ev ? ev.timestamp : null;
}

async function localAlertTime(sinceMs, subjectPart) {
  const r = await (await fetch(`${MAILHOG}/api/v2/messages?limit=50`)).json();
  const hit = r.items.find((m) => new Date(m.Created).getTime() >= sinceMs - 1000 && (m.Content.Headers.Subject?.[0] ?? '').includes(subjectPart));
  return hit ? new Date(hit.Created).getTime() : null;
}

const alertTime = async (sinceMs, kind) =>
  MODE === 'local' ? localAlertTime(sinceMs, kind === 'opened' ? 'en panne' : 'retabli') : awsAlertTime(sinceMs, kind === 'opened' ? 'Opened' : 'Resolved');

// --- scenario ----------------------------------------------------------------------------------------------------------
const demo = await call('GET', '/api/v1/demo');
await call('PUT', '/api/v1/demo', { mode: 'ok' });

let monitor = (await call('GET', '/api/v1/monitors')).find((m) => m.url === demo.targetUrl);
if (!monitor) {
  monitor = await call('POST', '/api/v1/monitors', { name: 'failure-test', url: demo.targetUrl, intervalMinutes: 1, timeoutSeconds: 5 });
  console.log(`created monitor ${monitor.id}`);
}
console.log('waiting for the monitor to be Up...');
await until('monitor Up', async () => (await call('GET', `/api/v1/monitors/${monitor.id}`)).status === 'Up');

const t0 = Date.now();
await call('PUT', '/api/v1/demo', { mode: 'down' });
console.log(`t0 = ${new Date(t0).toISOString()}: demo target cut`);

const down = await until('incident opened', async () => {
  const m = await call('GET', `/api/v1/monitors/${monitor.id}`);
  return m.openIncidentId ? m : null;
});
const tIncident = Date.now();
const incident = (await call('GET', `/api/v1/monitors/${monitor.id}/incidents`))[0];
console.log(`incident visible after ${fmt(tIncident - t0)} (opened at ${incident.openedAt})`);

const tAlert = await until('alert sent', () => alertTime(t0, 'opened'));
console.log(`alert sent after ${fmt(tAlert - t0)}`);

const t1 = Date.now();
await call('PUT', '/api/v1/demo', { mode: 'ok' });
console.log(`t1 = ${new Date(t1).toISOString()}: demo target restored`);
await until('incident resolved', async () => !(await call('GET', `/api/v1/monitors/${monitor.id}`)).openIncidentId);
const tResolved = Date.now();
const tRecoveryAlert = await until('recovery alert', () => alertTime(t1, 'resolved'));

console.log('\n| Step | Duration |\n|---|---|');
console.log(`| Target cut -> incident opened (2 failed checks) | ${fmt(new Date(incident.openedAt).getTime() - t0)} |`);
console.log(`| Target cut -> incident visible through the API | ${fmt(tIncident - t0)} |`);
console.log(`| Target cut -> alert sent | ${fmt(tAlert - t0)} |`);
console.log(`| Incident opened -> alert sent | ${fmt(tAlert - new Date(incident.openedAt).getTime())} |`);
console.log(`| Target restored -> incident resolved (2 successes) | ${fmt(tResolved - t1)} |`);
console.log(`| Target restored -> recovery alert | ${fmt(tRecoveryAlert - t1)} |`);
void down;
