// Administrative dashboard read queries (NFR-01: p95 < 500 ms on the benchmark dataset; docs/testing.md section 5). Signs in as
// the Owner like scripts/seed-demo.sh, then calls the dashboard's read endpoints in turn at a constant arrival rate, one endpoint
// per iteration, so each query gets the same share of the load. Run through `DASHBOARD=1 load/run.sh` (load/README.md), which
// fills the database with inference traffic first.
//
// Environment:
//   BASE_URL        gateway URL (default http://localhost:8090)
//   OWNER_EMAIL     Owner e-mail (required)
//   OWNER_PASSWORD  Owner password (required)
//   RATE            iterations (queries) per second over all queries (default 9, about 1 per second per query)
//   DURATION        how long to run (default 3m)
//   SUMMARY_FILE    where handleSummary writes the JSON summary (default load/results/dashboard.json)
import http from 'k6/http';
import exec from 'k6/execution';
import { check, fail } from 'k6';
import { Rate } from 'k6/metrics';

const baseUrl = (__ENV.BASE_URL || 'http://localhost:8090').replace(/\/$/, '');
const rate = parseInt(__ENV.RATE || '9', 10);
const duration = __ENV.DURATION || '3m';
const summaryFile = __ENV.SUMMARY_FILE || 'load/results/dashboard.json';

// The routes and parameters the dashboard sends (web/src/features/*/queries.ts, docs/api.md "Requests and usage").
const queries = [
  'overview_24h',
  'overview_7d',
  'requests_list',
  'requests_filtered',
  'requests_failed',
  'request_detail',
  'usage_24h_by_alias',
  'usage_tenant_month',
  'provider_health',
];

const thresholds = {};
for (const name of queries) {
  // Tagged sub-metrics, so the summary reports each query; the < 500 ms target is judged per query in the report.
  thresholds[`http_req_duration{query:${name}}`] = ['max>=0'];
  thresholds[`query_failed{query:${name}}`] = ['rate>=0'];
}

export const options = {
  scenarios: {
    dashboard: {
      executor: 'constant-arrival-rate',
      rate,
      timeUnit: '1s',
      duration,
      preAllocatedVUs: Math.max(10, rate * 2),
      maxVUs: Math.max(50, rate * 10),
      gracefulStop: '30s',
    },
  },
  thresholds,
  summaryTrendStats: ['avg', 'min', 'med', 'p(90)', 'p(95)', 'p(99)', 'max', 'count'],
};

const queryFailed = new Rate('query_failed');

function cookiesOf(response, into) {
  for (const [name, values] of Object.entries(response.cookies)) {
    if (values.length > 0) into[name] = values[0].value;
  }
  return into;
}

function cookieHeader(cookies) {
  return Object.entries(cookies)
    .map(([name, value]) => `${name}=${value}`)
    .join('; ');
}

export function setup() {
  const email = __ENV.OWNER_EMAIL;
  const password = __ENV.OWNER_PASSWORD;
  if (!email || !password) fail('OWNER_EMAIL and OWNER_PASSWORD are required');

  // ADR-0011: the anonymous antiforgery token, then the session. The cookies are Secure and __Host- prefixed, so they are carried
  // by hand: k6's jar would not send a Secure cookie back over http://localhost.
  const cookies = {};
  const anonymous = http.get(`${baseUrl}/admin/antiforgery-token`);
  if (anonymous.status !== 200) fail(`antiforgery-token answered ${anonymous.status}`);
  cookiesOf(anonymous, cookies);
  const session = http.post(`${baseUrl}/admin/session`, JSON.stringify({ email, password }), {
    headers: { 'Content-Type': 'application/json', 'X-XSRF-TOKEN': anonymous.json('token'), Cookie: cookieHeader(cookies) },
  });
  if (session.status !== 200) fail(`sign-in answered ${session.status}`);
  cookiesOf(session, cookies);
  const headers = { Cookie: cookieHeader(cookies) };

  const recent = http.get(`${baseUrl}/admin/requests?limit=200`, { headers });
  const requestIds = (recent.json('items') || []).map((item) => item.id);
  if (requestIds.length === 0) fail('no requests to open; run inference load first');
  const tenants = http.get(`${baseUrl}/admin/tenants?limit=200`, { headers }).json('items') || [];
  const tenant = tenants.find((t) => t.name === 'demo') || tenants[0];
  if (!tenant) fail('no Tenant found');
  return { headers, requestIds, tenantId: tenant.id };
}

function url(name, data) {
  const now = new Date();
  const iso = (date) => date.toISOString();
  const ago = (ms) => new Date(now.getTime() - ms);
  const monthStart = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), 1));
  switch (name) {
    case 'overview_24h':
      return `/admin/overview?from=${iso(ago(24 * 3600e3))}&to=${iso(now)}`;
    case 'overview_7d':
      return `/admin/overview?from=${iso(ago(7 * 24 * 3600e3))}&to=${iso(now)}`;
    case 'requests_list':
      return '/admin/requests?limit=50';
    case 'requests_filtered':
      return `/admin/requests?limit=50&status=succeeded&alias=balanced&from=${iso(ago(3600e3))}&to=${iso(now)}&sort=durationMs&order=desc`;
    case 'requests_failed':
      return '/admin/requests?limit=50&status=failed';
    case 'request_detail':
      return `/admin/requests/${data.requestIds[Math.floor(Math.random() * data.requestIds.length)]}`;
    case 'usage_24h_by_alias':
      return '/admin/usage?groupBy=alias&interval=hour';
    case 'usage_tenant_month':
      return `/admin/usage?tenantId=${data.tenantId}&from=${iso(monthStart)}&interval=month&groupBy=alias`;
    case 'provider_health':
      return '/admin/provider-health';
    default:
      throw new Error(`unknown query ${name}`);
  }
}

export default function (data) {
  const name = queries[exec.scenario.iterationInTest % queries.length];
  const response = http.get(`${baseUrl}${url(name, data)}`, { headers: data.headers, tags: { query: name }, timeout: '30s' });
  const ok = check(response, { 'status is 200': (r) => r.status === 200 }, { query: name });
  queryFailed.add(!ok, { query: name });
}

export function handleSummary(data) {
  const lines = ['', 'query                  count   fail%     p50      p95      p99      max'];
  for (const name of queries) {
    const duration = data.metrics[`http_req_duration{query:${name}}`];
    const failed = data.metrics[`query_failed{query:${name}}`];
    if (!duration) continue;
    const v = duration.values;
    lines.push(
      [
        name.padEnd(22),
        String(v.count).padStart(5),
        (failed ? (failed.values.rate * 100).toFixed(2) : 'n/a').padStart(7),
        v.med.toFixed(1).padStart(8),
        v['p(95)'].toFixed(1).padStart(8),
        v['p(99)'].toFixed(1).padStart(8),
        v.max.toFixed(1).padStart(8),
      ].join(' '),
    );
  }
  return { [summaryFile]: JSON.stringify(data, null, 2), stdout: lines.join('\n') + '\n' };
}
