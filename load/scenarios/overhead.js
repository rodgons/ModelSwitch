// Gateway overhead under constant arrival rate (docs/testing.md section 5, NFR-01). Run through load/run.sh, which brings up the
// stack, makes the Mock Provider answer after a fixed latency and collects the server-side numbers; see load/README.md.
//
// Environment:
//   BASE_URL        gateway URL (default http://localhost:8090)
//   API_KEY         an API Key with the `inference` Scope (required)
//   MODEL           Model Alias to call (default `balanced`, as seeded by scripts/seed-demo.sh)
//   RATES           comma-separated arrival rates in requests per second, run one after the other (default 50,100,200,400)
//   STEP_DURATION   how long each rate is held (default 5m); `30s` makes a smoke run
//   STEP_GAP        pause between steps, so server-side windows do not overlap (default 15s)
//   STREAM          `false` (default), `true`, or `both` (each rate non-streaming, then streaming)
//   MOCK_LATENCY_MS the latency the Mock Provider was told to add; subtracted for the `e2e_minus_mock` cross-check (default 200)
//   MAX_VUS         VU ceiling per step (default 1000)
//   SUMMARY_FILE    where handleSummary writes the JSON summary (default load/results/summary.json relative to the cwd)
import http from 'k6/http';
import { check } from 'k6';
import { Trend, Rate, Counter } from 'k6/metrics';

const baseUrl = (__ENV.BASE_URL || 'http://localhost:8090').replace(/\/$/, '');
const apiKey = __ENV.API_KEY;
const model = __ENV.MODEL || 'balanced';
const rates = (__ENV.RATES || '50,100,200,400').split(',').map((r) => parseInt(r.trim(), 10)).filter((r) => r > 0);
const stepDuration = __ENV.STEP_DURATION || '5m';
const stepGap = __ENV.STEP_GAP || '15s';
const streamMode = (__ENV.STREAM || 'false').toLowerCase();
const mockLatencyMs = parseFloat(__ENV.MOCK_LATENCY_MS || '200');
const maxVus = parseInt(__ENV.MAX_VUS || '1000', 10);
const summaryFile = __ENV.SUMMARY_FILE || 'load/results/summary.json';

if (!apiKey) {
  throw new Error('API_KEY is required (an API Key with the inference Scope)');
}

function seconds(text) {
  const match = /^(\d+(?:\.\d+)?)(ms|s|m|h)$/.exec(text);
  if (!match) throw new Error(`Unsupported duration '${text}' (use e.g. 500ms, 30s, 5m)`);
  const factor = { ms: 0.001, s: 1, m: 60, h: 3600 }[match[2]];
  return parseFloat(match[1]) * factor;
}

const variants = streamMode === 'both' ? [false, true] : [streamMode === 'true'];

// One constant-arrival-rate scenario per (variant, rate), started one after the other.
const scenarios = {};
const thresholds = {};
let offset = 0;
for (const stream of variants) {
  for (const rate of rates) {
    const name = `${stream ? 'stream' : 'nonstream'}_${rate}rps`;
    scenarios[name] = {
      executor: 'constant-arrival-rate',
      rate,
      timeUnit: '1s',
      duration: stepDuration,
      // Little's law at the mock's latency plus headroom; k6 adds VUs up to maxVUs if the gateway slows down.
      preAllocatedVUs: Math.min(maxVus, Math.max(20, Math.ceil(rate * (mockLatencyMs / 1000 + 0.3) * 2))),
      maxVUs: maxVus,
      startTime: `${offset}s`,
      gracefulStop: '30s',
      env: { STREAM: String(stream) },
    };
    // Thresholds on tagged sub-metrics make k6 report each step separately in the summary. They are not pass/fail gates:
    // NFR-01 is judged on the server-side metric by load/run.sh.
    thresholds[`http_req_duration{scenario:${name}}`] = ['max>=0'];
    thresholds[`e2e_minus_mock{scenario:${name}}`] = ['max>=0'];
    thresholds[`http_req_waiting{scenario:${name}}`] = ['max>=0'];
    thresholds[`request_failed{scenario:${name}}`] = ['rate>=0'];
    thresholds[`requests{scenario:${name}}`] = ['count>=0'];
    thresholds[`dropped_iterations{scenario:${name}}`] = ['count>=0'];
    offset += seconds(stepDuration) + 30 + seconds(stepGap);
  }
}

export const options = {
  scenarios,
  thresholds,
  discardResponseBodies: false,
  summaryTrendStats: ['avg', 'min', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
  // Requests go to one host; keep-alive connections are reused as a real client SDK would.
  noConnectionReuse: false,
};

const e2eMinusMock = new Trend('e2e_minus_mock', true);
const requestFailed = new Rate('request_failed');
const requests = new Counter('requests');

const params = {
  headers: { Authorization: `Bearer ${apiKey}`, 'Content-Type': 'application/json' },
  timeout: '30s',
};

export default function () {
  const stream = __ENV.STREAM === 'true';
  const body = JSON.stringify({
    model,
    stream,
    max_tokens: 16,
    messages: [{ role: 'user', content: 'load test: reply with this sentence' }],
  });
  const response = http.post(`${baseUrl}/v1/chat/completions`, body, params);
  const ok = check(response, {
    'status is 200': (r) => r.status === 200,
    'complete body': (r) =>
      typeof r.body === 'string' && (stream ? r.body.includes('data: [DONE]') : r.body.includes('"choices"')),
  });
  requests.add(1);
  requestFailed.add(!ok);
  if (ok) {
    e2eMinusMock.add(response.timings.duration - mockLatencyMs);
  }
}

export function handleSummary(data) {
  return {
    [summaryFile]: JSON.stringify(data, null, 2),
    stdout: textSummary(data),
  };
}

// A compact per-step table; the full data is in the JSON file.
function textSummary(data) {
  const metric = (name, step) => data.metrics[`${name}{scenario:${step}}`];
  const ms = (value) => (value === undefined ? 'n/a' : value.toFixed(1));
  const lines = ['', 'step                    reqs   fail%  dropped  e2e p50  e2e p95  e2e p99  (e2e-mock) p95'];
  for (const step of Object.keys(scenarios)) {
    const duration = metric('http_req_duration', step);
    const minus = metric('e2e_minus_mock', step);
    const failed = metric('request_failed', step);
    const count = metric('requests', step);
    const dropped = metric('dropped_iterations', step);
    if (!duration) continue;
    lines.push(
      [
        step.padEnd(22),
        String(count ? count.values.count : 0).padStart(6),
        (failed ? (failed.values.rate * 100).toFixed(2) : 'n/a').padStart(7),
        String(dropped ? dropped.values.count : 0).padStart(8),
        ms(duration.values.med).padStart(8),
        ms(duration.values['p(95)']).padStart(8),
        ms(duration.values['p(99)']).padStart(8),
        ms(minus ? minus.values['p(95)'] : undefined).padStart(15),
      ].join(' '),
    );
  }
  return lines.join('\n') + '\n';
}
