// Reads the CloudWatch metrics of a load-test window and prints the numbers for docs/results.md.
//
//   node loadtest/collect-results.mjs --env dev --start 2026-11-20T10:00:00Z --end 2026-11-20T10:30:00Z [--profile teamchat]
//
// Throughput, p95 latency, late checks, losses (scheduled but never executed), errors, throttles and DLQ depth.
import { spawnSync } from 'node:child_process';

const arg = (name, def) => {
  const i = process.argv.indexOf(`--${name}`);
  return i > 0 ? process.argv[i + 1] : def;
};
const env = arg('env', 'dev');
const profile = arg('profile', process.env.AWS_PROFILE ?? 'teamchat');
const region = arg('region', 'eu-north-1');
const start = new Date(arg('start'));
const end = new Date(arg('end'));
if (!arg('start') || !arg('end') || Number.isNaN(start.getTime()) || Number.isNaN(end.getTime())) {
  console.error('usage: collect-results.mjs --env dev --start <ISO> --end <ISO>');
  process.exit(2);
}
// Period = a multiple of 60 s covering the whole window: one datapoint = the window.
const periodS = Math.max(60, Math.ceil((end - start) / 1000 / 60) * 60);
const ns = `StatusHub/${env}`;
const prefix = `statushub-${env}`;

const metric = (id, namespace, name, dims, stat) => ({
  Id: id,
  MetricStat: { Metric: { Namespace: namespace, MetricName: name, Dimensions: Object.entries(dims).map(([Name, Value]) => ({ Name, Value })) }, Period: periodS, Stat: stat },
  ReturnData: true,
});

const queries = [
  metric('scheduled', ns, 'ChecksScheduled', { Service: 'scheduler' }, 'Sum'),
  metric('executed', ns, 'ChecksExecuted', { Service: 'checker' }, 'Sum'),
  metric('ontime', ns, 'ChecksOnTime', { Service: 'checker' }, 'Sum'),
  metric('late', ns, 'ChecksLate', { Service: 'checker' }, 'Sum'),
  metric('failed', ns, 'ChecksFailed', { Service: 'checker' }, 'Sum'),
  metric('lat_p50', ns, 'CheckLatencyMs', { Service: 'checker' }, 'p50'),
  metric('lat_p95', ns, 'CheckLatencyMs', { Service: 'checker' }, 'p95'),
  metric('lat_p99', ns, 'CheckLatencyMs', { Service: 'checker' }, 'p99'),
  metric('delay_p95', ns, 'CheckDelaySeconds', { Service: 'checker' }, 'p95'),
  metric('delay_p99', ns, 'CheckDelaySeconds', { Service: 'checker' }, 'p99'),
  metric('delay_max', ns, 'CheckDelaySeconds', { Service: 'checker' }, 'Maximum'),
  metric('processed', ns, 'ResultsProcessed', { Service: 'evaluator' }, 'Sum'),
  ...['scheduler', 'checker', 'evaluator', 'notifier', 'api'].flatMap((f) => [
    metric(`err_${f}`, 'AWS/Lambda', 'Errors', { FunctionName: `${prefix}-${f}` }, 'Sum'),
    metric(`thr_${f}`, 'AWS/Lambda', 'Throttles', { FunctionName: `${prefix}-${f}` }, 'Sum'),
  ]),
  ...['checks', 'results', 'notifier'].map((q) => metric(`dlq_${q}`, 'AWS/SQS', 'ApproximateNumberOfMessagesVisible', { QueueName: `${prefix}-${q}-dlq` }, 'Maximum')),
  ...['checks', 'results'].map((q) => metric(`depth_${q}`, 'AWS/SQS', 'ApproximateNumberOfMessagesVisible', { QueueName: `${prefix}-${q}` }, 'Maximum')),
];

const res = spawnSync('aws', [
  'cloudwatch', 'get-metric-data', '--profile', profile, '--region', region, '--output', 'json',
  '--start-time', start.toISOString(), '--end-time', end.toISOString(), '--metric-data-queries', JSON.stringify(queries),
], { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
if (res.status !== 0) {
  console.error(res.stderr || 'aws cli failed');
  process.exit(1);
}

const values = {};
for (const r of JSON.parse(res.stdout).MetricDataResults) values[r.Id] = r.Values.length ? r.Values.reduce((a, b) => a + b, 0) / (r.Id.match(/^(lat|delay)/) ? r.Values.length : 1) : 0;
// Sums add up over datapoints; percentiles/maximums are averaged/maxed over (normally) one datapoint.
const v = (id) => values[id] ?? 0;

const windowS = (end - start) / 1000;
const lost = Math.max(0, v('scheduled') - v('executed'));
const pctOf = (a, b) => (b > 0 ? `${((100 * a) / b).toFixed(3)} %` : 'n/a');
const sum = (prefixKey) => ['scheduler', 'checker', 'evaluator', 'notifier', 'api'].reduce((a, f) => a + v(`${prefixKey}_${f}`), 0);

const rows = [
  ['Window', `${start.toISOString()} -> ${end.toISOString()} (${Math.round(windowS / 60)} min)`],
  ['Checks scheduled', v('scheduled')],
  ['Checks executed', v('executed')],
  ['Throughput (executed / s)', (v('executed') / windowS).toFixed(2)],
  ['Throughput (executed / min)', (v('executed') / (windowS / 60)).toFixed(0)],
  ['Check latency p50 / p95 / p99 (ms)', `${v('lat_p50').toFixed(0)} / ${v('lat_p95').toFixed(0)} / ${v('lat_p99').toFixed(0)}`],
  ['Delay after scheduled time p95 / p99 / max (s)', `${v('delay_p95').toFixed(1)} / ${v('delay_p99').toFixed(1)} / ${v('delay_max').toFixed(1)}`],
  ['Late checks (> 60 s)', `${v('late')} (${pctOf(v('late'), v('executed'))})`],
  ['On-time share (SLO >= 99.5 %)', pctOf(v('ontime'), v('scheduled'))],
  ['Lost checks (scheduled - executed)', `${lost} (${pctOf(lost, v('scheduled'))})`],
  ['Results stored by the Evaluator', v('processed')],
  ['Targets reported down', v('failed')],
  ['Lambda errors / throttles (all functions)', `${sum('err')} / ${sum('thr')}`],
  ['DLQ max depth checks / results / notifier', `${v('dlq_checks')} / ${v('dlq_results')} / ${v('dlq_notifier')}`],
  ['Queue max depth checks / results', `${v('depth_checks')} / ${v('depth_results')}`],
];

console.log('| Measure | Value |\n|---|---|');
for (const [k, val] of rows) console.log(`| ${k} | ${val} |`);
