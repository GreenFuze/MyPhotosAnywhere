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
