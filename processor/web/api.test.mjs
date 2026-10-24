import { test } from 'node:test';
import assert from 'node:assert/strict';
import { build } from 'esbuild';

const bundle = await build({ entryPoints: ['src/api.ts'], bundle: true, platform: 'node', format: 'esm', write: false, logLevel: 'silent' });
assert.equal(bundle.warnings.length, 0, 'API test bundle must be warning-free');
const { DesktopApi } = await import('data:text/javascript;base64,' + Buffer.from(bundle.outputFiles[0].contents).toString('base64'));

async function withBrowser(fetch, action) {
  const previous = { fetch: globalThis.fetch, location: Object.getOwnPropertyDescriptor(globalThis, 'location') };
  globalThis.fetch = fetch;
  Object.defineProperty(globalThis, 'location', { value: { hash: '', pathname: '/' }, configurable: true });
  try { await action(new DesktopApi()); }
  finally {
    globalThis.fetch = previous.fetch;
    if (previous.location) Object.defineProperty(globalThis, 'location', previous.location);
    else delete globalThis.location;
  }
}
const json = value => new Response(JSON.stringify(value), { headers: { 'Content-Type': 'application/json' } });

test('setup status uses the actual bounded server result and an abortable same-origin read', async () => {
  const controller = new AbortController(); let calls = 0;
  const statuses = [{ stage: 'Running', detail: null, canRetry: false }, { stage: 'PoliciesPublished', detail: null, canRetry: false }];
  await withBrowser(async (url, options) => {
    assert.equal(url, '/api/library-publication'); assert.equal(options.credentials, 'same-origin'); assert.equal(options.signal, controller.signal);
    return json(statuses[calls++]);
  }, async api => {
    assert.deepEqual(await api.libraryPublication(controller.signal), statuses[0]);
    assert.deepEqual(await api.libraryPublication(controller.signal), statuses[1]);
  });
  assert.equal(calls, 2);
});

test('invalid setup responses cannot be displayed as completed or retryable work', async () => {
  const invalid = [null, {}, { stage: 'Complete', detail: null, canRetry: false },
    { stage: 'PoliciesPublished', detail: null, canRetry: true }, { stage: 'Running', detail: 12, canRetry: false },
    { stage: 'NeedsAttention', detail: 'x'.repeat(1025), canRetry: true }, { stage: 'Running', detail: null, canRetry: 'false' }];
  for (const value of invalid) {
    await withBrowser(async () => json(value), async api => {
      await assert.rejects(api.libraryPublication(new AbortController().signal), /invalid library setup status/);
    });
  }
});

test('an explicit setup retry sends the authenticated CSRF command and accepts only its actual status', async () => {
  const csrf = 'a'.repeat(64); let requests = 0;
  await withBrowser(async (url, options) => {
    requests++;
    if (url === '/api/session') return json({ csrf, hostOwner: true });
    assert.equal(url, '/api/library-publication'); assert.equal(options.method, 'POST'); assert.equal(options.credentials, 'same-origin');
    assert.equal(options.headers['X-MPA-CSRF'], csrf); assert.equal(options.headers['Content-Type'], 'application/json'); assert.equal(options.body, '{}');
    return json({ stage: 'Running', detail: null, canRetry: false });
  }, async api => {
    await api.connect(); assert.equal(api.hostOwner, true);
    assert.deepEqual(await api.retryLibraryPublication(), { stage: 'Running', detail: null, canRetry: false });
  });
  assert.equal(requests, 2);
});

test('canceling a progress read propagates cancellation without sending a setup command', async () => {
  const controller = new AbortController(); controller.abort(); let requests = 0;
  await withBrowser(async (url, options) => {
    requests++; assert.equal(url, '/api/library-publication'); assert.notEqual(options.method, 'POST');
    assert.equal(options.signal, controller.signal); throw new DOMException('Read canceled', 'AbortError');
  }, async api => { await assert.rejects(api.libraryPublication(controller.signal), { name: 'AbortError' }); });
  assert.equal(requests, 1);
});


test('connection export is an abortable same-origin read and never sends a setup command', async () => {
  const controller = new AbortController(); let requests = 0;
  const connection = { code: 'mpa-library-v1:eyJmb3JtYXQiOiJzeW50aGV0aWMifQ', documentSha256: 'a'.repeat(64), controllerKeyId: 'b'.repeat(64) };
  await withBrowser(async (url, options) => {
    requests++; assert.equal(url, '/api/library-connection'); assert.equal(options.credentials, 'same-origin'); assert.equal(options.signal, controller.signal);
    assert.equal(options.method, undefined); assert.equal(options.body, undefined); return json({ connection: requests === 1 ? null : connection });
  }, async api => {
    assert.equal(await api.libraryConnection(controller.signal), null);
    assert.deepEqual(await api.libraryConnection(controller.signal), connection);
  });
  assert.equal(requests, 2);
});

test('malformed connection responses are rejected before the browser offers a code', async () => {
  const valid = { code: 'mpa-library-v1:e30', documentSha256: 'a'.repeat(64), controllerKeyId: 'b'.repeat(64) };
  const invalid = [null, {}, { connection: false }, { connection: { ...valid, code: 'https://processor.local/' } },
    { connection: { ...valid, code: 'mpa-library-v1:' } }, { connection: { ...valid, code: 'mpa-library-v1:e30=' } },
    { connection: { ...valid, code: 'mpa-library-v1:' + 'a'.repeat(4096) } },
    { connection: { ...valid, documentSha256: 'A'.repeat(64) } }, { connection: { ...valid, controllerKeyId: '' } }];
  for (const value of invalid) await withBrowser(async () => json(value), async api => {
    await assert.rejects(api.libraryConnection(new AbortController().signal), /invalid library connection/);
  });
});

test('canceling connection export never creates a client enrollment or setup request', async () => {
  const controller = new AbortController(); controller.abort(); let requests = 0;
  await withBrowser(async (url, options) => {
    requests++; assert.equal(url, '/api/library-connection'); assert.equal(options.signal, controller.signal);
    assert.equal(options.method, undefined); throw new DOMException('Read canceled', 'AbortError');
  }, async api => { await assert.rejects(api.libraryConnection(controller.signal), { name: 'AbortError' }); });
  assert.equal(requests, 1);
});
