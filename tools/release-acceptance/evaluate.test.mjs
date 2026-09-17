import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdirSync, rmSync, truncateSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { test } from 'node:test';
import { evaluate, main, MAX_BYTES, OPERATIONS } from './evaluate.mjs';

// Generated records test arithmetic only; never export this fixture as release evidence.
const artifact = Buffer.from('unit-test-only, not measured release evidence');
const sha256 = createHash('sha256').update(artifact).digest('hex');
function fixture() {
  const mix = [0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 4, 5, 5];
  return {
    schemaVersion: 1,
    run: {
      id: 'test-only', commit: 'a'.repeat(40), startedUtc: '2026-09-16T10:00:00.000Z',
      durationMs: 1_800_000, environment: 'approved-representative',
      authentication: 'entra-distinct-users', transport: 'blazor-interactive',
      users: 300, databaseBytes: 1_000_000, seed: 'test-seed', hostProfile: 'test-host'
    },
    artifacts: ['environment', 'generator', 'application', 'circuits', 'delivery', 'recovery']
      .map(role => ({ role, file: `${role}.log`, sha256, bytes: artifact.length })),
    sessions: Array.from({ length: 300 }, (_, i) => ({ user: `user-${i}`, circuit: `circuit-${i}`, fromMs: 0, toMs: 1_800_000 })),
    operations: Array.from({ length: 18_000 }, (_, i) => ({
      id: `op-${i}`, user: `user-${i % 300}`, operation: OPERATIONS[mix[Math.floor(i / 300) % 20]],
      atMs: Math.floor(i / 600) * 60_000 + i % 600, appMs: 1_000, ok: true
    })),
    isolation: Array.from({ length: 300 }, (_, i) => ({
      user: `user-${i}`, otherUser: `user-${(i + 1) % 300}`, ownRead: true, foreignReadDenied: true, foreignMutationDenied: true
    })),
    notifications: Array.from({ length: 100 }, (_, i) => ({ id: `notification-${i}`, dueMs: 0, submittedMs: 120_000, providerHealthy: true })),
    reminders: Array.from({ length: 100 }, (_, i) => ({ id: `reminder-${i}`, dueMs: 0, submittedMs: 120_000, providerHealthy: true })),
    recovery: {
      incidentUtc: '2026-09-16T12:00:00.000Z', usableUtc: '2026-09-16T16:00:00.000Z',
      sqlRecoveredThroughUtc: '2026-09-16T11:00:00.000Z', blobRecoveredThroughUtc: '2026-09-16T11:00:00.000Z',
      checks: { sqlIntegrity: true, blobContent: true, crossStoreConsistency: true, authorization: true,
        keyRing: true, queueReconciliation: true, externalEffectsRecorded: true }
    }
  };
}
const run = data => evaluate(data, () => artifact);

test('numeric conformity never grants release acceptance or validates invented provenance', () => {
  const result = run(fixture());
  assert.equal(result.status, 'review-required');
  assert.equal(result.releaseAccepted, false);
  assert.equal(result.provenance, 'unverified-local-records-and-digests');
  assert.equal(result.openGates.length, 7);
  assert.ok(result.openGates.includes('actual-restore'));
  assert.deepEqual(result.failures, []);
  assert.equal(result.rpoMs, 3_600_000);
  assert.equal(result.rtoMs, 14_400_000);
});

for (const [ms, failed] of [[1999, false], [2000, true], [2001, true]]) {
  test(`p95 strict two-second boundary ${ms}`, () => {
    const data = fixture();
    for (const op of data.operations) op.appMs = ms;
    const result = run(data);
    assert.equal(result.p95Ms.all, ms);
    assert.equal(result.status, failed ? 'targets-not-met' : 'review-required');
    assert.equal(result.failures.includes('p95:all'), failed);
  });
}

test('nearest-rank p95 includes the exact 95th percentile and keeps slow classes visible', () => {
  const data = fixture();
  const edits = data.operations.filter(o => o.operation === 'edit');
  edits.forEach((o, i) => { o.appMs = i < Math.ceil(edits.length * .95) - 1 ? 1000 : 2000; });
  const result = run(data);
  assert.equal(result.p95Ms.all, 1000);
  assert.equal(result.p95Ms.edit, 2000);
  assert.deepEqual(result.failures, ['p95:edit']);
});

for (const [late, expected] of [[4, 'review-required'], [5, 'review-required'], [6, 'targets-not-met']]) {
  test(`notification 95-percent boundary with ${late} late submissions`, () => {
    const data = fixture();
    data.notifications.slice(0, late).forEach(n => { n.submittedMs = 120_001; });
    const result = run(data);
    assert.equal(result.status, expected);
    assert.deepEqual(result.delivery.notifications, { onTime: 100 - late, total: 100 });
  });
}

for (const [ms, expected] of [[119999, 'review-required'], [120000, 'review-required'], [120001, 'targets-not-met'], [null, 'targets-not-met']]) {
  test(`every reminder must meet its own two-minute deadline: ${ms}`, () => {
    const data = fixture();
    data.reminders[0].submittedMs = ms;
    const result = run(data);
    assert.equal(result.status, expected);
    assert.equal(result.delivery.reminders.onTime, expected === 'review-required' ? 100 : 99);
  });
}

test('missing submissions remain in the notification denominator', () => {
  const data = fixture();
  data.notifications.slice(0, 6).forEach(n => { n.submittedMs = null; });
  assert.deepEqual(run(data).delivery.notifications, { onTime: 94, total: 100 });
  assert.ok(run(data).failures.includes('notifications:deadline'));
});

for (const [field, value, failure] of [
  ['sqlRecoveredThroughUtc', '2026-09-16T10:59:59.999Z', 'recovery:rpo'],
  ['blobRecoveredThroughUtc', '2026-09-16T10:59:59.999Z', 'recovery:rpo'],
  ['usableUtc', '2026-09-16T16:00:00.001Z', 'recovery:rto']
]) {
  test(`recovery boundary fails one millisecond outside ${field}`, () => {
    const data = fixture();
    data.recovery[field] = value;
    assert.deepEqual(run(data).failures, [failure]);
  });
}

test('recovery immediately inside both targets remains review-only', () => {
  const data = fixture();
  data.recovery.sqlRecoveredThroughUtc = data.recovery.blobRecoveredThroughUtc = '2026-09-16T11:00:00.001Z';
  data.recovery.usableUtc = '2026-09-16T15:59:59.999Z';
  assert.equal(run(data).rpoMs, 3_599_999);
  assert.equal(run(data).rtoMs, 14_399_999);
  assert.equal(run(data).releaseAccepted, false);
});

const invalid = [
  ['summary-only dossier', d => { delete d.operations; }],
  ['unknown top-level field', d => { d.passed = true; }],
  ['unknown nested field', d => { d.run.p95 = 1; }],
  ['schema version', d => { d.schemaVersion = 2; }],
  ['short commit', d => { d.run.commit = '613e795'; }],
  ['synthetic authentication', d => { d.run.authentication = 'synthetic'; }],
  ['HTTP-only transport', d => { d.run.transport = 'http'; }],
  ['laptop environment', d => { d.run.environment = 'laptop'; }],
  ['299 users', d => { d.run.users = 299; }],
  ['short duration', d => { d.run.durationMs--; }],
  ['overlong duration', d => { d.run.durationMs = 3_600_001; }],
  ['missing database size', d => { d.run.databaseBytes = 0; }],
  ['too few operations', d => { d.operations.pop(); }],
  ['too many operations', d => { d.operations = Array(100_001).fill(d.operations[0]); }],
  ['unknown operation', d => { d.operations[0].operation = 'http-get'; }],
  ['fractional timing', d => { d.operations[0].appMs = .1; }],
  ['negative timing', d => { d.operations[0].appMs = -1; }],
  ['non-finite timing', d => { d.operations[0].appMs = Infinity; }],
  ['outside window', d => { d.operations[0].atMs = d.run.durationMs; }],
  ['completion outside window', d => { d.operations[0].appMs = d.run.durationMs; d.operations[0].atMs = 1; }],
  ['unknown user', d => { d.operations[0].user = 'unknown'; }],
  ['duplicate operation', d => { d.operations[0].id = d.operations[1].id; }],
  ['unknown result', d => { d.operations[0].ok = 'true'; }],
  ['missing class', d => { d.operations.forEach(o => { if (o.operation === 'edit') o.operation = 'discover'; }); }],
  ['skewed mix', d => { d.operations.forEach(o => { if (o.operation === 'quest-detail') o.operation = 'discover'; }); }],
  ['idle measurement minutes', d => { d.operations.forEach(o => { o.atMs = 0; }); }],
  ['idle signed-in user', d => { d.operations.forEach(o => { if (o.user === 'user-0') o.user = 'user-1'; }); }],
  ['missing circuit', d => { d.sessions.pop(); }],
  ['duplicate user', d => { d.sessions[0].user = d.sessions[1].user; }],
  ['duplicate circuit', d => { d.sessions[0].circuit = d.sessions[1].circuit; }],
  ['disconnected circuit', d => { d.sessions[0].toMs--; }],
  ['late circuit', d => { d.sessions[0].fromMs = 1; }],
  ['missing isolation user', d => { d.isolation.pop(); }],
  ['same-user isolation', d => { d.isolation[0].otherUser = d.isolation[0].user; }],
  ['duplicate isolation', d => { d.isolation[0] = d.isolation[1]; }],
  ['missing delivery cohort', d => { d.notifications = []; }],
  ['insufficient reminders', d => { d.reminders.pop(); }],
  ['duplicate delivery', d => { d.notifications[0].id = d.notifications[1].id; }],
  ['late-eligible cohort', d => { d.notifications[0].dueMs = d.run.durationMs - 119_999; }],
  ['submission before due', d => { d.notifications[0].submittedMs = -1; }],
  ['submission outside window', d => { d.reminders[0].submittedMs = d.run.durationMs + 1; }],
  ['unhealthy provider', d => { d.notifications[0].providerHealthy = false; }],
  ['impossible date', d => { d.run.startedUtc = '2026-02-30T10:00:00.000Z'; }],
  ['recovery before load ends', d => { d.recovery.incidentUtc = d.run.startedUtc; }],
  ['restore before incident', d => { d.recovery.usableUtc = '2026-09-16T11:59:59.999Z'; }],
  ['future recovery point', d => { d.recovery.sqlRecoveredThroughUtc = '2026-09-16T12:00:00.001Z'; }],
  ['missing recovery check', d => { delete d.recovery.checks.keyRing; }],
  ['unknown recovery result', d => { d.recovery.checks.keyRing = 'yes'; }],
  ['missing artifact', d => { d.artifacts.pop(); }],
  ['duplicate artifact role', d => { d.artifacts[0].role = d.artifacts[1].role; }],
  ['duplicate artifact path', d => { d.artifacts[0].file = d.artifacts[1].file; }],
  ['traversal artifact', d => { d.artifacts[0].file = '..\\secret'; }],
  ['absolute artifact', d => { d.artifacts[0].file = 'C:\\secret'; }],
  ['device artifact', d => { d.artifacts[0].file = 'con.txt'; }],
  ['digest mismatch', d => { d.artifacts[0].sha256 = '0'.repeat(64); }],
  ['size mismatch', d => { d.artifacts[0].bytes++; }],
  ['oversize artifact', d => { d.artifacts[0].bytes = MAX_BYTES + 1; }]
];
for (const [name, mutate] of invalid) {
  test(`rejects ${name}`, () => {
    const data = fixture();
    mutate(data);
    assert.throws(() => run(data));
  });
}

for (const [name, mutate, failure] of [
  ['application error', d => { d.operations[0].ok = false; }, 'operation-failed:op-0'],
  ['foreign read leak', d => { d.isolation[0].foreignReadDenied = false; }, 'isolation:user-0:foreignReadDenied'],
  ['foreign mutation', d => { d.isolation[0].foreignMutationDenied = false; }, 'isolation:user-0:foreignMutationDenied'],
  ['own data unavailable', d => { d.isolation[0].ownRead = false; }, 'isolation:user-0:ownRead']
]) {
  test(`fails ${name} even with fast timings`, () => {
    const data = fixture();
    mutate(data);
    assert.deepEqual(run(data).failures, [failure]);
    assert.equal(run(data).status, 'targets-not-met');
  });
}
for (const key of Object.keys(fixture().recovery.checks)) {
  test(`failed actual-restore check ${key} cannot be hidden by timestamps`, () => {
    const data = fixture();
    data.recovery.checks[key] = false;
    assert.deepEqual(run(data).failures, [`recovery:${key}`]);
  });
}

test('missing artifact bytes fail closed', () => {
  assert.throws(() => evaluate(fixture(), () => undefined), /bytes/);
});

test('CLI missing input returns invalid without printing supplied content', t => {
  const log = t.mock.method(console, 'error', () => {});
  assert.equal(main([]), 2);
  assert.equal(log.mock.callCount(), 1);
  assert.match(log.mock.calls[0].arguments[0], /^Invalid or insufficient dossier/);
});

let directorySequence = 0;
function withBundle(action) {
  const directory = join(process.cwd(), 'tools', 'release-acceptance', `.test-${process.pid}-${directorySequence++}`);
  mkdirSync(directory);
  try {
    const data = fixture();
    const path = join(directory, 'dossier.json');
    const write = () => writeFileSync(path, `${JSON.stringify(data, null, 2)}\n`);
    write();
    for (const a of data.artifacts) writeFileSync(join(directory, a.file), artifact);
    action({ data, path, directory, write });
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
}

test('CLI reads actual local artifacts and distinguishes review failure and invalid exit codes', t => {
  const output = t.mock.method(console, 'log', () => {});
  t.mock.method(console, 'error', () => {});
  withBundle(({ data, path, directory, write }) => {
    assert.equal(main([path]), 0);
    const result = JSON.parse(output.mock.calls[0].arguments[0]);
    assert.equal(result.status, 'review-required');
    assert.equal(result.releaseAccepted, false);
    data.operations[0].ok = false;
    write();
    assert.equal(main([path]), 1);
    assert.equal(JSON.parse(output.mock.calls[1].arguments[0]).status, 'targets-not-met');
    writeFileSync(join(directory, data.artifacts[0].file), 'changed');
    assert.equal(main([path]), 2);
    assert.equal(output.mock.callCount(), 2);
  });
});

test('CLI rejects duplicate keys and noncanonical JSON instead of last-key-wins parsing', t => {
  t.mock.method(console, 'error', () => {});
  withBundle(({ data, path }) => {
    writeFileSync(path, JSON.stringify(data));
    assert.equal(main([path]), 2);
    const text = `${JSON.stringify(data, null, 2)}\n`;
    writeFileSync(path, text.replace('"schemaVersion": 1,', '"schemaVersion": 0,\n  "schemaVersion": 1,'));
    assert.equal(main([path]), 2);
  });
});

test('CLI rejects malformed UTF-8 empty oversized and absent files', t => {
  t.mock.method(console, 'error', () => {});
  withBundle(({ path, directory }) => {
    writeFileSync(path, Buffer.from([0xff]));
    assert.equal(main([path]), 2);
    truncateSync(path, 0);
    assert.equal(main([path]), 2);
    truncateSync(path, MAX_BYTES + 1);
    assert.equal(main([path]), 2);
    assert.equal(main([join(directory, 'absent.json')]), 2);
    assert.equal(main([directory]), 2);
  });
});
