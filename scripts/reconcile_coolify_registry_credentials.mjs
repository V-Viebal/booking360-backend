#!/usr/bin/env node
// Target-locked, value-free reconciliation of temporary registry env rows.

function parseArgs(argv) {
  const result = { keys: [] };
  for (let index = 0; index < argv.length; index += 1) {
    const value = argv[index];
    if (value === '--application') result.uuid = argv[++index];
    else if (value === '--expected-name') result.name = argv[++index];
    else if (value === '--expected-project') result.project = argv[++index];
    else if (value === '--expected-environment') result.environment = argv[++index];
    else if (value === '--expected-environment-uuid') result.environmentUuid = argv[++index];
    else if (value === '--expected-destination') result.destination = argv[++index];
    else if (value === '--expected-server') result.server = argv[++index];
    else if (value === '--expected-branch') result.branch = argv[++index];
    else if (value === '--expected-repository') result.repository = argv[++index];
    else if (value === '--expected-domain') result.domain = argv[++index];
    else if (value === '--key') result.keys.push(argv[++index]);
    else if (value === '--apply') result.apply = true;
    else throw new Error(`unknown argument: ${value}`);
  }
  if (!result.uuid || result.keys.length === 0) {
    throw new Error('--application and at least one --key are required');
  }
  if (result.apply && (!result.name || !result.environment || !result.server)) {
    throw new Error('--apply requires expected name, environment, and server target locks');
  }
  return result;
}

function normalized(value) {
  return String(value || '').trim().replace(/^(['"])(.*)\1$/, '$2').trim();
}

function normalizeRows(payload) {
  return Array.isArray(payload) ? payload : (payload?.envs || payload?.data || []);
}

function firstDefined(object, keys) {
  for (const key of keys) {
    const value = object?.[key];
    if (value !== undefined && value !== null && normalized(value) !== '') {
      return normalized(value);
    }
  }
  return '';
}

function decodeJsonString(value) {
  if (typeof value !== 'string') return value;
  const trimmed = value.trim();
  if (!trimmed.startsWith('{') && !trimmed.startsWith('[')) return value;
  try {
    return JSON.parse(trimmed);
  } catch {
    return value;
  }
}

function domainContains(application, expectedDomain) {
  if (!expectedDomain) return true;
  const raw = decodeJsonString(
    application?.docker_compose_domains ||
      application?.fqdn ||
      application?.domains ||
      '',
  );
  return String(Array.isArray(raw) ? raw.join(' ') : raw).includes(expectedDomain);
}

function assertTarget(application, expected) {
  const observed = {
    uuid: firstDefined(application, ['uuid', 'id']),
    name: firstDefined(application, ['name']),
    repository: firstDefined(application, ['git_repository']),
    branch: firstDefined(application, ['git_branch']),
    project: firstDefined(application, ['project_uuid']) ||
      firstDefined(application?.environment?.project, ['uuid', 'id']),
    environmentUuid: firstDefined(application, ['environment_uuid']) ||
      firstDefined(application?.environment, ['uuid', 'id']),
    environment: firstDefined(application?.environment, ['name']),
    destination: firstDefined(application, ['destination_uuid']) ||
      firstDefined(application?.destination, ['uuid', 'id']),
    server: firstDefined(application, ['server_uuid']) ||
      firstDefined(application?.destination?.server, ['uuid', 'id']),
    buildPack: firstDefined(application, ['build_pack']),
    compose: firstDefined(application, ['docker_compose_location']),
    domain: domainContains(application, expected.domain),
  };

  const checks = [
    ['uuid', expected.uuid],
    ['name', expected.name],
    ['project', expected.project],
    ['environmentUuid', expected.environmentUuid],
    ['environment', expected.environment],
    ['destination', expected.destination],
    ['server', expected.server],
    ['branch', expected.branch],
    ['repository', expected.repository],
  ];
  for (const [key, wanted] of checks) {
    if (wanted && wanted !== observed[key]) {
      throw new Error(`target lock mismatch for ${key}: expected ${wanted}, observed ${observed[key] || 'missing'}`);
    }
  }
  if (observed.buildPack && observed.buildPack !== 'dockercompose') {
    throw new Error(`target lock mismatch for build_pack: expected dockercompose, observed ${observed.buildPack}`);
  }
  if (observed.compose && observed.compose !== '/compose.yaml') {
    throw new Error(`target lock mismatch for docker_compose_location: expected /compose.yaml, observed ${observed.compose}`);
  }
  if (!observed.domain) {
    throw new Error(`target lock mismatch for domain: expected ${expected.domain}, observed missing`);
  }
  return observed;
}

function summarizeRows(rows, keys) {
  const allowed = new Set(keys);
  return rows
    .filter((row) => allowed.has(row.key))
    .map((row) => ({
      uuid: row.uuid,
      key: row.key,
      has_value: Boolean(normalized(row.real_value || row.value || '')),
      is_preview: Boolean(row.is_preview),
      is_shared: Boolean(row.is_shared),
      is_buildtime: Boolean(row.is_buildtime),
      is_runtime: Boolean(row.is_runtime),
    }))
    .sort((left, right) => `${left.key}:${left.uuid}`.localeCompare(`${right.key}:${right.uuid}`));
}

async function reconcile(options) {
  const base = String(process.env.COOLIFY_URL || '').replace(/\/$/, '');
  const token = String(process.env.COOLIFY_API_TOKEN || '');
  if (!base || !token) throw new Error('COOLIFY_URL and COOLIFY_API_TOKEN are required');

  async function request(path, init = {}) {
    const response = await fetch(`${base}${path}`, {
      ...init,
      headers: {
        Authorization: `Bearer ${token}`,
        Accept: 'application/json',
        ...(init.headers || {}),
      },
      signal: AbortSignal.timeout(30000),
    });
    if (!response.ok && !(init.method === 'DELETE' && response.status === 404)) {
      throw new Error(`${init.method || 'GET'} ${path}: HTTP ${response.status}`);
    }
    if (response.status === 204 || init.method === 'DELETE') return null;
    return response.json();
  }

  const application = await request(`/api/v1/applications/${options.uuid}`);
  const target = assertTarget(application, {
    ...options,
    uuid: options.uuid,
  });
  const envPath = `/api/v1/applications/${options.uuid}/envs`;
  const before = summarizeRows(normalizeRows(await request(envPath)), options.keys);
  const deleted = [];

  if (options.apply) {
    for (const row of before) {
      if (!row.uuid) throw new Error(`Coolify did not expose a UUID for ${row.key}`);
      await request(`${envPath}/${row.uuid}`, { method: 'DELETE' });
      deleted.push({ uuid: row.uuid, key: row.key, is_preview: row.is_preview });
    }
  }

  const after = summarizeRows(normalizeRows(await request(envPath)), options.keys);
  const nonemptyAfter = after.filter((row) => row.has_value);
  const receipt = {
    schema_version: 1,
    operation: options.apply ? 'delete-and-readback' : 'audit-only',
    target,
    keys: [...new Set(options.keys)].sort(),
    before_count: before.length,
    before_nonempty_count: before.filter((row) => row.has_value).length,
    deleted,
    after_count: after.length,
    after_nonempty_count: nonemptyAfter.length,
    verified_absent: after.length === 0,
    verified_no_nonempty_value: nonemptyAfter.length === 0,
  };

  if (options.apply && nonemptyAfter.length > 0) {
    throw new Error(`registry credential reconciliation failed: ${nonemptyAfter.length} non-empty rows remain`);
  }
  console.log(JSON.stringify(receipt, null, 2));
}

reconcile(parseArgs(process.argv.slice(2))).catch((error) => {
  console.error(error.message);
  process.exitCode = 1;
});
