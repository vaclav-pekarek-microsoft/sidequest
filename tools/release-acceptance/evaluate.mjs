import { createHash } from 'node:crypto';
import { lstatSync, readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

export const MAX_BYTES = 32 * 1024 * 1024;
export const OPERATIONS = ['discover', 'quest-detail', 'joined', 'join-leave', 'edit', 'notifications'];
const MIX = [25, 25, 20, 15, 5, 10];
const ROLES = ['environment', 'generator', 'application', 'circuits', 'delivery', 'recovery'];
const OPEN_GATES = ['provenance-review', 'representative-deployment', 'live-identity-provider',
  'device-accessibility', 'outlook', 'actual-restore', 'operational-approval'];

function requireValue(condition, message) {
  if (!condition) throw new Error(message);
}

function object(value, keys, label) {
  requireValue(value !== null && typeof value === 'object' && !Array.isArray(value), `${label}: object required`);
  requireValue(Object.keys(value).sort().join(',') === [...keys].sort().join(','), `${label}: exact keys required`);
}

function integer(value, min, max, label) {
  requireValue(Number.isSafeInteger(value) && value >= min && value <= max, `${label}: bounded integer required`);
}

function identifier(value, label) {
  requireValue(typeof value === 'string' && /^[a-zA-Z0-9][a-zA-Z0-9_-]{0,79}$/.test(value), `${label}: pseudonymous identifier required`);
}

function rows(value, min, max, label) {
  requireValue(Array.isArray(value) && value.length >= min && value.length <= max, `${label}: insufficient or excessive rows`);
}

function unique(values, label) {
  requireValue(new Set(values).size === values.length, `${label}: duplicate identifier`);
}

function utc(value, label) {
  requireValue(typeof value === 'string' && /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/.test(value)
    && Number.isFinite(Date.parse(value)) && new Date(value).toISOString() === value, `${label}: canonical UTC required`);
  return Date.parse(value);
}

function percentile95(values) {
  return [...values].sort((a, b) => a - b)[Math.ceil(values.length * 0.95) - 1];
}

/**
 * Checks a bounded measurement dossier, not the truth of its provenance.
 * Even target-conforming records leave every live release gate open.
 * Artifact bytes are supplied by the local CLI or a deterministic test reader.
 */
export function evaluate(data, readArtifact) {
  object(data, ['schemaVersion', 'run', 'artifacts', 'sessions', 'operations', 'isolation', 'notifications', 'reminders', 'recovery'], 'dossier');
  requireValue(data.schemaVersion === 1, 'unsupported schemaVersion');
  const r = data.run;
  object(r, ['id', 'commit', 'startedUtc', 'durationMs', 'environment', 'authentication', 'transport', 'users', 'databaseBytes', 'seed', 'hostProfile'], 'run');
  identifier(r.id, 'run.id');
  identifier(r.seed, 'run.seed');
  identifier(r.hostProfile, 'run.hostProfile');
  requireValue(typeof r.commit === 'string' && /^[a-f0-9]{40}$/.test(r.commit), 'run.commit: full lowercase SHA required');
  const started = utc(r.startedUtc, 'run.startedUtc');
  integer(r.durationMs, 1_800_000, 3_600_000, 'run.durationMs');
  integer(r.databaseBytes, 1, Number.MAX_SAFE_INTEGER, 'run.databaseBytes');
  requireValue(r.environment === 'approved-representative' && r.authentication === 'entra-distinct-users'
    && r.transport === 'blazor-interactive', 'HTTP-only, synthetic or unapproved load is not representative evidence');
  integer(r.users, 300, 300, 'run.users');
  rows(data.artifacts, ROLES.length, ROLES.length, 'artifacts');
  unique(data.artifacts.map(a => a.role), 'artifacts.role');
  unique(data.artifacts.map(a => a.file), 'artifacts.file');
  for (const a of data.artifacts) {
    object(a, ['role', 'file', 'sha256', 'bytes'], 'artifact');
    requireValue(ROLES.includes(a.role), 'artifact.role: unknown role');
    requireValue(typeof a.file === 'string' && /^[a-z0-9][a-z0-9._-]{0,95}$/.test(a.file)
      && !/^(con|prn|aux|nul|com[0-9]|lpt[0-9])(?:\.|$)/i.test(a.file), 'artifact.file: plain local filename required');
    requireValue(typeof a.sha256 === 'string' && /^[a-f0-9]{64}$/.test(a.sha256), 'artifact.sha256: lowercase SHA-256 required');
    integer(a.bytes, 1, MAX_BYTES, 'artifact.bytes');
    const bytes = readArtifact(a.file);
    requireValue(Buffer.isBuffer(bytes) && bytes.length === a.bytes, 'artifact: missing or inconsistent bytes');
    requireValue(createHash('sha256').update(bytes).digest('hex') === a.sha256, 'artifact: digest mismatch');
  }

  // These intervals must be derived from authenticated server circuit lifecycle logs.
  rows(data.sessions, 300, 300, 'sessions');
  for (const s of data.sessions) {
    object(s, ['user', 'circuit', 'fromMs', 'toMs'], 'session');
    identifier(s.user, 'session.user');
    identifier(s.circuit, 'session.circuit');
    requireValue(s.fromMs === 0 && s.toMs === r.durationMs, 'session: steady-state circuit must span the complete measurement window');
  }
  unique(data.sessions.map(s => s.user), 'sessions.user');
  unique(data.sessions.map(s => s.circuit), 'sessions.circuit');
  const users = new Set(data.sessions.map(s => s.user));
  const userCounts = new Map([...users].map(user => [user, 0]));
  rows(data.operations, 18_000, 100_000, 'operations');
  unique(data.operations.map(o => o.id), 'operations.id');
  const failures = [];
  const byOperation = new Map(OPERATIONS.map(name => [name, []]));
  const minutes = Array.from({ length: Math.ceil(r.durationMs / 60_000) }, () => 0);
  for (const o of data.operations) {
    object(o, ['id', 'user', 'operation', 'atMs', 'appMs', 'ok'], 'operation');
    identifier(o.id, 'operation.id');
    requireValue(users.has(o.user), 'operation: unknown user');
    requireValue(byOperation.has(o.operation), 'operation: unsupported class');
    integer(o.atMs, 0, r.durationMs - 1, 'operation.atMs');
    integer(o.appMs, 0, r.durationMs, 'operation.appMs');
    requireValue(o.atMs + o.appMs <= r.durationMs, 'operation: completion outside measurement window');
    requireValue(typeof o.ok === 'boolean', 'operation.ok: boolean required');
    byOperation.get(o.operation).push(o.appMs);
    userCounts.set(o.user, userCounts.get(o.user) + 1);
    minutes[Math.floor(o.atMs / 60_000)]++;
    if (!o.ok) failures.push(`operation-failed:${o.id}`);
  }
  requireValue([...userCounts.values()].every(count => count >= 6), 'operations: every user needs at least six measured operations');
  requireValue(minutes.every(count => count >= 300), 'operations: every measurement minute needs at least 300 samples');
  const p95 = {};
  for (const [name, values] of byOperation) {
    requireValue(values.length >= 100, `operations: insufficient ${name} samples`);
    const share = values.length * 100 / data.operations.length;
    requireValue(Math.abs(share - MIX[OPERATIONS.indexOf(name)]) <= 5, `operations: ${name} mix outside five percentage-point tolerance`);
    p95[name] = percentile95(values);
    if (p95[name] >= 2_000) failures.push(`p95:${name}`);
  }
  p95.all = percentile95(data.operations.map(o => o.appMs));
  if (p95.all >= 2_000) failures.push('p95:all');

  rows(data.isolation, 300, 300, 'isolation');
  unique(data.isolation.map(i => i.user), 'isolation.user');
  for (const i of data.isolation) {
    object(i, ['user', 'otherUser', 'ownRead', 'foreignReadDenied', 'foreignMutationDenied'], 'isolation');
    requireValue(users.has(i.user) && users.has(i.otherUser) && i.user !== i.otherUser, 'isolation: distinct known users required');
    for (const key of ['ownRead', 'foreignReadDenied', 'foreignMutationDenied']) {
      requireValue(typeof i[key] === 'boolean', 'isolation: boolean outcomes required');
      if (!i[key]) failures.push(`isolation:${i.user}:${key}`);
    }
  }

  const delivery = {};
  for (const kind of ['notifications', 'reminders']) {
    rows(data[kind], 100, 20_000, kind);
    unique(data[kind].map(n => n.id), `${kind}.id`);
    let onTime = 0;
    for (const n of data[kind]) {
      object(n, ['id', 'dueMs', 'submittedMs', 'providerHealthy'], kind);
      identifier(n.id, `${kind}.id`);
      integer(n.dueMs, 0, r.durationMs - 120_000, `${kind}.dueMs`);
      // Unhealthy runs must be investigated and rerun, not trimmed to a favorable denominator.
      requireValue(n.providerHealthy === true, `${kind}: healthy-provider evidence required for the whole cohort`);
      if (n.submittedMs !== null) {
        integer(n.submittedMs, n.dueMs, r.durationMs, `${kind}.submittedMs`);
        if (n.submittedMs - n.dueMs <= 120_000) onTime++;
      }
    }
    delivery[kind] = { onTime, total: data[kind].length };
    if (kind === 'notifications' ? onTime * 100 < data[kind].length * 95 : onTime !== data[kind].length) {
      failures.push(`${kind}:deadline`);
    }
  }

  const recovery = data.recovery;
  object(recovery, ['incidentUtc', 'usableUtc', 'sqlRecoveredThroughUtc', 'blobRecoveredThroughUtc', 'checks'], 'recovery');
  const incident = utc(recovery.incidentUtc, 'recovery.incidentUtc');
  const usable = utc(recovery.usableUtc, 'recovery.usableUtc');
  const sql = utc(recovery.sqlRecoveredThroughUtc, 'recovery.sqlRecoveredThroughUtc');
  const blob = utc(recovery.blobRecoveredThroughUtc, 'recovery.blobRecoveredThroughUtc');
  requireValue(incident >= started + r.durationMs && usable >= incident && sql <= incident && blob <= incident,
    'recovery: inconsistent timeline');
  const checks = ['sqlIntegrity', 'blobContent', 'crossStoreConsistency', 'authorization', 'keyRing', 'queueReconciliation', 'externalEffectsRecorded'];
  object(recovery.checks, checks, 'recovery.checks');
  for (const key of checks) {
    requireValue(typeof recovery.checks[key] === 'boolean', 'recovery.checks: boolean outcomes required');
    if (!recovery.checks[key]) failures.push(`recovery:${key}`);
  }
  const rpoMs = incident - Math.min(sql, blob);
  const rtoMs = usable - incident;
  if (rpoMs > 3_600_000) failures.push('recovery:rpo');
  if (rtoMs > 14_400_000) failures.push('recovery:rto');
  return {
    status: failures.length ? 'targets-not-met' : 'review-required',
    releaseAccepted: false,
    openGates: [...OPEN_GATES],
    provenance: 'unverified-local-records-and-digests',
    p95Ms: p95,
    delivery,
    rpoMs,
    rtoMs,
    failureCount: failures.length,
    failures: failures.slice(0, 100)
  };
}

function boundedFile(path) {
  const stat = lstatSync(path);
  requireValue(stat.isFile() && !stat.isSymbolicLink() && stat.size > 0 && stat.size <= MAX_BYTES,
    'input must be a nonempty bounded regular file, not a symlink');
  const bytes = readFileSync(path);
  requireValue(bytes.length === stat.size, 'input changed while reading');
  return bytes;
}

export function main(args) {
  try {
    requireValue(args.length === 1, 'usage: node tools\\release-acceptance\\evaluate.mjs <dossier.json>');
    const path = resolve(args[0]);
    const bytes = boundedFile(path);
    const text = new TextDecoder('utf-8', { fatal: true }).decode(bytes);
    const data = JSON.parse(text);
    requireValue(text === `${JSON.stringify(data, null, 2)}\n`, 'dossier must be canonical two-space JSON with final LF');
    const result = evaluate(data, file => {
      const artifactPath = join(dirname(path), file);
      requireValue(artifactPath !== path, 'artifact must not be the dossier itself');
      return boundedFile(artifactPath);
    });
    console.log(JSON.stringify(result, null, 2));
    // No exit code means release acceptance. 0 means numerical review readiness only.
    return result.status === 'review-required' ? 0 : 1;
  } catch {
    // Do not echo supplied records, filenames, credentials or provider diagnostics.
    console.error('Invalid or insufficient dossier. Check the release evidence schema and local artifacts.');
    return 2;
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  process.exitCode = main(process.argv.slice(2));
}
