// k6 load test: 500 fake monitors pointing at the demo target, checked every minute for 30 minutes.
//
// k6 only creates the monitors (25 users x 20, the per-account limit) and keeps calling the API while the real pipeline runs
// (EventBridge Scheduler -> Scheduler -> SQS -> Checker -> SQS -> Evaluator -> DynamoDB). Throughput, p95 latency, late checks
// and losses come from the CloudWatch metrics of the same window: run loadtest/collect-results.mjs right after.
//
//   AWS dev  : k6 run -e API_URL=https://xxxx.execute-api.eu-north-1.amazonaws.com -e AUTH_MODE=cognito \
//                     -e COGNITO_CLIENT_ID=... -e USER_PASSWORD='...' loadtest/load.js     (users: loadtest/users.sh create dev 25)
//   local    : k6 run -e API_URL=http://localhost:5080 -e AUTH_MODE=dev -e DURATION=5m loadtest/load.js
import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter } from 'k6/metrics';

const API = (__ENV.API_URL || 'http://localhost:5080').replace(/\/$/, '');
const AUTH_MODE = __ENV.AUTH_MODE || 'dev';
const USERS = parseInt(__ENV.USERS || '25', 10);
const PER_USER = parseInt(__ENV.MONITORS_PER_USER || '20', 10);
const TARGET = __ENV.TARGET_URL || `${API}/demo/target`;
const REGION = __ENV.AWS_REGION || 'eu-north-1';
const PASSWORD = __ENV.USER_PASSWORD || '';

const monitorsCreated = new Counter('monitors_created');

export const options = {
  scenarios: {
    observe: { executor: 'constant-vus', vus: parseInt(__ENV.VUS || '5', 10), duration: __ENV.DURATION || '30m', exec: 'observe' },
  },
  setupTimeout: '10m',
  teardownTimeout: '10m',
  thresholds: {
    http_req_failed: ['rate<0.01'],
    'http_req_duration{endpoint:list}': ['p(95)<1000'],
    'http_req_duration{endpoint:public_status}': ['p(95)<500'],
  },
};

function login(n) {
  const email = `loadtest-${String(n).padStart(2, '0')}@example.com`;
  if (AUTH_MODE === 'dev') {
    const r = http.post(`${API}/dev/token`, JSON.stringify({ email }), { headers: { 'Content-Type': 'application/json' } });
    return { token: r.json('token'), email };
  }
  const r = http.post(`https://cognito-idp.${REGION}.amazonaws.com/`,
    JSON.stringify({ AuthFlow: 'USER_PASSWORD_AUTH', ClientId: __ENV.COGNITO_CLIENT_ID, AuthParameters: { USERNAME: email, PASSWORD: PASSWORD } }),
    { headers: { 'Content-Type': 'application/x-amz-json-1.1', 'X-Amz-Target': 'AWSCognitoIdentityProviderService.InitiateAuth' } });
  return { token: r.json('AuthenticationResult.IdToken'), email };
}

const auth = (token) => ({ headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' } });

export function setup() {
  const users = [];
  for (let n = 1; n <= USERS; n++) {
    const { token } = login(n);
    if (!token) throw new Error(`login failed for user ${n}`);
    const me = http.get(`${API}/api/v1/me`, auth(token)).json();
    const existing = http.get(`${API}/api/v1/monitors`, auth(token)).json();
    const ids = existing.map((m) => m.id);
    for (let i = ids.length; i < PER_USER; i++) {
      const res = http.post(`${API}/api/v1/monitors`, JSON.stringify({
        name: `load-${n}-${i}`, url: `${TARGET}?m=${n}-${i}`, method: 'GET', intervalMinutes: 1, expectedStatus: 200, timeoutSeconds: 5,
      }), auth(token));
      if (check(res, { 'monitor created': (r) => r.status === 201 })) {
        ids.push(res.json('id'));
        monitorsCreated.add(1);
      }
    }
    users.push({ n, slug: me.slug, ids });
  }
  console.log(`started at ${new Date().toISOString()} with ${users.reduce((a, u) => a + u.ids.length, 0)} monitors`);
  return { users, startedAt: new Date().toISOString() };
}

// While the pipeline runs, behave like dashboard users refreshing every few seconds.
export function observe(data) {
  const u = data.users[Math.floor(Math.random() * data.users.length)];
  const { token } = login(u.n);
  const params = auth(token);
  const list = http.get(`${API}/api/v1/monitors`, { ...params, tags: { endpoint: 'list' } });
  check(list, { 'list ok': (r) => r.status === 200 });
  const id = u.ids[Math.floor(Math.random() * u.ids.length)];
  const stats = http.get(`${API}/api/v1/monitors/${id}/stats`, { ...params, tags: { endpoint: 'stats' } });
  check(stats, { 'stats ok': (r) => r.status === 200 });
  const page = http.get(`${API}/api/v1/status/${u.slug}`, { tags: { endpoint: 'public_status' } });
  check(page, { 'public page ok': (r) => r.status === 200 });
  sleep(5);
}

export function teardown(data) {
  console.log(`finished at ${new Date().toISOString()} (started ${data.startedAt}): run collect-results.mjs for this window`);
  if (__ENV.KEEP_MONITORS === '1') return;
  for (const u of data.users) {
    const { token } = login(u.n);
    for (const id of u.ids) http.del(`${API}/api/v1/monitors/${id}`, null, auth(token));
  }
}
