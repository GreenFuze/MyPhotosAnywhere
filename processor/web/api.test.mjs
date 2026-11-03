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


const sharedStage = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';
const sharedAsset = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb';
test('export metadata uses an abortable same-origin read and preserves exact opaque bytes in a bounded download', async () => {
  const controller = new AbortController(), raw = new Uint8Array([239, 187, 191, 123, 34, 255, 0, 125, 13, 10]); let calls = 0;
  await withBrowser(async (url, options) => {
    calls++; const query = new URL(url, 'https://local.invalid'); assert.equal(query.pathname, '/api/library/metadata-source');
    assert.equal(query.searchParams.get('stage'), sharedStage); assert.equal(query.searchParams.get('asset'), sharedAsset); assert.equal(query.searchParams.get('account'), 'account-A');
    assert.equal(options.credentials, 'same-origin'); assert.equal(options.cache, 'no-store'); assert.equal(options.signal, controller.signal);
    return new Response(raw, { headers: { 'Content-Type': 'application/octet-stream', 'Content-Length': String(raw.length) } });
  }, async api => {
    const blob = await api.libraryMetadataSource(sharedStage, sharedAsset, 'account-A', controller.signal); assert.equal(blob.type, 'application/octet-stream');
    assert.deepEqual(new Uint8Array(await blob.arrayBuffer()), raw);
    await assert.rejects(api.libraryMetadataSource('', sharedAsset, null, controller.signal), /Invalid captured shared/);
    await assert.rejects(api.libraryMetadataSource(sharedStage, sharedAsset, 'someone@example.com', controller.signal), /Invalid captured shared/);
  }); assert.equal(calls, 1);
});
test('invalid export metadata lengths and types cannot become a downloadable file', async () => {
  for (const [type, length, raw] of [['text/html', '4', 'test'], ['application/octet-stream', null, 'test'], ['application/octet-stream', '0', ''],
    ['application/octet-stream', '04', 'test'], ['application/octet-stream', '131073', 'test'], ['application/octet-stream', '3', 'test']]) {
    await withBrowser(async () => {
      const headers = { 'Content-Type': type }; if (length !== null) headers['Content-Length'] = length; return new Response(raw, { headers });
    }, async api => { await assert.rejects(api.libraryMetadataSource(sharedStage, sharedAsset, null, new AbortController().signal), /metadata|verified length/i); });
  }
});
test('missing or canceled export metadata reads cannot report a saved file', async () => {
  await withBrowser(async () => new Response(JSON.stringify({ message: 'No saved export metadata is available for this photo.' }), { status: 404, headers: { 'Content-Type': 'application/json' } }),
    async api => { await assert.rejects(api.libraryMetadataSource(sharedStage, sharedAsset, null, new AbortController().signal), /No saved export metadata/); });
  const controller = new AbortController();
  await withBrowser(async () => {
    controller.abort(); return new Response('test', { headers: { 'Content-Type': 'application/octet-stream', 'Content-Length': '4' } });
  }, async api => { await assert.rejects(api.libraryMetadataSource(sharedStage, sharedAsset, null, controller.signal), { name: 'AbortError' }); });
});
const sharedOther = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc';
const sharedViewer = 'dddddddd-dddd-4ddd-8ddd-dddddddddddd';
const sharedPhoto = { id: sharedAsset, accountId: 'account-A', name: 'Literal <tag> שלום.jpg', mimeType: 'image/jpeg', width: 40, height: 30, effectiveUtc: 100, dateProvenance: 'DriveCapture', rawCapture: null };
const sharedPage = { available: true, stageId: sharedStage, rootSequence: 7, photos: [sharedPhoto], olderCursor: null, newerCursor: null };
const importedDetails = () => ({ title: 'Export name.jpg', description: 'Literal <tag> שלום', capturedUtc: 1000, latitude: 31.777, longitude: -35.123, nameHints: ['Unmatched name'], attention: null });

test('captured viewer reads preserve imported detail provenance and the human filename without downloading or editing an original', async () => {
  const signal = new AbortController().signal; const photo = { ...sharedPhoto, effectiveUtc: 1000, dateProvenance: 'ImportedCapture', importedMetadata: importedDetails() };
  const frame = { id: sharedViewer, count: 1, position: 0, stageId: sharedStage, accountId: 'account-A', entries: [{ position: 0, assetId: sharedAsset, photo }] };
  await withBrowser(async (url, options) => {
    assert.equal(new URL(url, 'https://local.invalid').pathname, '/api/viewers'); assert.equal(options.method, undefined); assert.equal(options.signal, signal); assert.equal(options.credentials, 'same-origin'); return json(frame);
  }, async api => {
    const read = await api.libraryViewer(sharedViewer, 0, sharedStage, 'account-A', signal);
    assert.equal(read.entries[0].photo.name, sharedPhoto.name); assert.deepEqual(read.entries[0].photo.importedMetadata, importedDetails()); assert.equal(read.entries[0].photo.dateProvenance, 'ImportedCapture');
  });
  const attention = { title: null, description: null, capturedUtc: null, latitude: null, longitude: null, nameHints: [], attention: 'conflicting-metadata' };
  await withBrowser(async () => json({ ...frame, entries: [{ ...frame.entries[0], photo: { ...sharedPhoto, importedMetadata: attention } }] }), async api => {
    assert.deepEqual((await api.libraryViewer(sharedViewer, 0, sharedStage, 'account-A', signal)).entries[0].photo.importedMetadata, attention);
  });
});

test('malformed imported details cannot replace a captured viewer with false hints, dates, coordinates or completion claims', async () => {
  const frame = { id: sharedViewer, count: 1, position: 0, stageId: sharedStage, accountId: 'account-A', entries: [] };
  const invalid = [null, {}, { ...importedDetails(), capturedUtc: 1.5 }, { ...importedDetails(), capturedUtc: 253402300800000 },
    { ...importedDetails(), latitude: 91 }, { ...importedDetails(), longitude: null }, { ...importedDetails(), latitude: '31.777' },
    { ...importedDetails(), description: '界'.repeat(21900) }, { ...importedDetails(), nameHints: Array(101).fill('name') },
    { ...importedDetails(), nameHints: [{ personId: sharedAsset }] }, { ...importedDetails(), nameHints: ['x'.repeat(201)] },
    { ...importedDetails(), attention: 'unknown' }, { ...importedDetails(), attention: 'conflicting-metadata' }, { ...importedDetails(), backupVerified: true }];
  for (const details of invalid.filter(value => value !== null)) await withBrowser(async () => json({ ...frame, entries: [{ position: 0, assetId: sharedAsset, photo: { ...sharedPhoto, importedMetadata: details } }] }), async api => {
    await assert.rejects(api.libraryViewer(sharedViewer, 0, sharedStage, 'account-A', new AbortController().signal), /invalid imported photo details/);
  });
});

test('canonical photo reads use the shared route with a captured stage/account cursor and preserve literal metadata', async () => {
  const signal = new AbortController().signal; const cursor = `c1|${sharedStage}|account-A|100|${sharedAsset}`;
  await withBrowser(async (url, options) => {
    const uri = new URL(url, 'https://local.invalid'); assert.equal(uri.pathname, '/api/library/catalog'); assert.equal(uri.searchParams.get('account'), 'account-A');
    assert.equal(uri.searchParams.get('cursor'), cursor); assert.equal(uri.searchParams.get('direction'), 'newer'); assert.equal(options.signal, signal); assert.equal(options.credentials, 'same-origin'); assert.equal(options.method, undefined);
    return json({ ...sharedPage, olderCursor: cursor });
  }, async api => { const page = await api.libraryCatalog('account-A', cursor, true, null, signal); assert.deepEqual(page.photos, [sharedPhoto]); assert.equal(page.stageId, sharedStage); assert.equal('discoveryRevision' in page, false); });
});

test('canonical photo responses cannot replace a retained page with another stage/account, duplicate identities or a false empty selection', async () => {
  const invalid = [null, {}, { ...sharedPage, stageId: '00000000-0000-0000-0000-000000000000' }, { ...sharedPage, photos: [sharedPhoto, sharedPhoto] },
    { ...sharedPage, photos: [{ ...sharedPhoto, accountId: 'account-B' }] }, { ...sharedPage, photos: [{ ...sharedPhoto, effectiveUtc: 253402300800000 }] },
    { ...sharedPage, olderCursor: `c1|${sharedOther}|account-A|100|${sharedAsset}` }, { ...sharedPage, olderCursor: `c1|${sharedStage}|account-B|100|${sharedAsset}` },
    { ...sharedPage, olderCursor: `c1|${sharedStage}|account-A|+100|${sharedAsset}` }, { ...sharedPage, available: false }];
  for (const value of invalid) await withBrowser(async () => json(value), async api => { await assert.rejects(api.libraryCatalog('account-A', null, false, null, new AbortController().signal), /invalid shared photo/); });
  const cursor = `c1|${sharedStage}|account-A|100|${sharedAsset}`;
  for (const value of [{ ...sharedPage, stageId: sharedOther }, { available: false, stageId: null, photos: [], olderCursor: null, newerCursor: null }])
    await withBrowser(async () => json(value), async api => { await assert.rejects(api.libraryCatalog('account-A', cursor, false, null, new AbortController().signal), /invalid shared photo/); });
  await withBrowser(async () => json({ available: false, stageId: null, photos: [], olderCursor: null, newerCursor: null }), async api => {
    assert.equal((await api.libraryCatalog(null, null, false, null, new AbortController().signal)).available, false);
  });
});

test('canonical viewer commands keep the captured stage/account and reject a different context while retaining missing slots', async () => {
  const csrf = 'a'.repeat(64); const signal = new AbortController().signal; let reads = 0;
  const frame = { id: sharedViewer, count: 2, position: 0, stageId: sharedStage, accountId: 'account-A', entries: [{ position: 0, assetId: sharedAsset, photo: sharedPhoto }, { position: 1, assetId: sharedOther, photo: null }] };
  await withBrowser(async (url, options) => {
    if (url === '/api/session') return json({ csrf, hostOwner: true });
    assert.equal(options.credentials, 'same-origin'); assert.equal(options.signal, signal);
    if (options.method === 'POST') {
      assert.equal(url, '/api/viewers'); assert.equal(options.headers['X-MPA-CSRF'], csrf); assert.deepEqual(JSON.parse(options.body), { id: sharedViewer, selectedId: sharedAsset, accountId: 'account-A', stageId: sharedStage }); return json(frame);
    }
    const uri = new URL(url, 'https://local.invalid'); assert.equal(uri.pathname, '/api/viewers'); assert.equal(uri.searchParams.get('viewer'), sharedViewer); assert.equal(uri.searchParams.get('position'), '1');
    return json({ ...frame, position: 1, stageId: ++reads === 1 ? sharedStage : sharedOther });
  }, async api => {
    await api.connect(); assert.equal((await api.openLibraryViewer(sharedViewer, sharedAsset, 'account-A', sharedStage, signal)).stageId, sharedStage);
    const missing = await api.libraryViewer(sharedViewer, 1, sharedStage, 'account-A', signal); assert.equal(missing.entries[1].assetId, sharedOther); assert.equal(missing.entries[1].photo, null);
    await assert.rejects(api.libraryViewer(sharedViewer, 1, sharedStage, 'account-A', signal), /another shared viewer context/);
  });
});

test('canonical preview URLs carry only captured identities and cancellation remains an abortable same-origin read', async () => {
  const controller = new AbortController(); controller.abort(); let requests = 0;
  await withBrowser(async (url, options) => { requests++; assert.equal(new URL(url, 'https://local.invalid').pathname, '/api/library/catalog'); assert.equal(options.signal, controller.signal); assert.equal(options.method, undefined); throw new DOMException('Read canceled', 'AbortError'); }, async api => {
    const uri = new URL(api.libraryPreview(sharedStage, sharedAsset, 'account-A'), 'https://local.invalid'); assert.equal(uri.pathname, '/api/library/preview'); assert.equal(uri.searchParams.get('stage'), sharedStage); assert.equal(uri.searchParams.get('asset'), sharedAsset); assert.equal(uri.searchParams.get('account'), 'account-A');
    assert.throws(() => api.libraryPreview('00000000-0000-0000-0000-000000000000', sharedAsset, null), /Invalid captured shared/);
    assert.throws(() => api.libraryPreview(sharedStage, sharedAsset, 'someone@example.com'), /Invalid captured shared/);
    await assert.rejects(api.libraryCatalog(null, null, false, null, controller.signal), { name: 'AbortError' });
  }); assert.equal(requests, 1);
});
const importIdentity = '22222222-2222-4222-8222-222222222222';
const validImport = () => ({ id: importIdentity, inputName: 'Takeout photos', target: { libraryId: '33333333-3333-4333-8333-333333333333', accountId: 'account-one', accountRevision: '1', folderId: 'photos', configurationRevision: '2' }, state: 'copying', entries: '12', media: '10', verified: '3', requested: true, issue: null, metadata: null });

const metadataIssue = () => ({ key: 'b'.repeat(64), name: 'Photos/שלום <photo>.jpg.json', code: 'input-unavailable', retryable: true });
test('metadata issues are bounded same-origin reads bound to a saved import and advancing cursor', async () => {
  const signal = new AbortController().signal; const after = 'a'.repeat(64); const page = { issues: [metadataIssue()], next: 'c'.repeat(64) };
  await withBrowser(async (url, options) => {
    const uri = new URL(url, 'https://local.invalid'); assert.equal(uri.pathname, '/api/import-metadata-issues');
    assert.equal(uri.searchParams.get('id'), importIdentity); assert.equal(uri.searchParams.get('after'), after);
    assert.equal(options.signal, signal); assert.equal(options.credentials, 'same-origin'); assert.equal(options.method, undefined); return json(page);
  }, async api => { assert.deepEqual(await api.importMetadataIssues(importIdentity, after, signal), page); });
  // A page containing only resolved native rows can still advance without displaying those rows.
  await withBrowser(async () => json({ issues: [], next: 'b'.repeat(64) }), async api => {
    assert.deepEqual(await api.importMetadataIssues(importIdentity, after, signal), { issues: [], next: 'b'.repeat(64) });
  });
});

test('metadata details reject forged paths, identities, status claims, oversized pages and nonadvancing cursors', async () => {
  const issue = metadataIssue(); const page = { issues: [issue], next: null }; const after = 'a'.repeat(64);
  const invalid = [null, {}, { ...page, issues: Array(61).fill(issue) }, { ...page, issues: [issue, issue] },
    { ...page, issues: [issue, { ...issue, key: 'a'.repeat(64) }] }, { ...page, next: after }, { ...page, next: 'A'.repeat(64) },
    { ...page, next: 'a'.repeat(63) + 'b' }, { ...page, issues: [{ ...issue, key: 'z'.repeat(64) }] },
    ...['', '/outside.json', 'C:/outside.json', '../outside.json', 'a/../outside.json', 'a//b.json', 'a\\b.json', 'a\u0000.json', 'x'.repeat(4097)].map(name => ({ ...page, issues: [{ ...issue, name }] })),
    ...[{ code: 'unknown' }, { code: ['input-unavailable'] }, { retryable: false }, { code: 'invalid-metadata', retryable: true }, { retryable: 'true' }].map(change => ({ ...page, issues: [{ ...issue, ...change }] }))];
  for (const value of invalid) await withBrowser(async () => json(value), async api => {
    await assert.rejects(api.importMetadataIssues(importIdentity, after, new AbortController().signal), /invalid metadata issue details/);
  });
  let calls = 0;
  await withBrowser(async () => { calls++; return json(page); }, async api => {
    await assert.rejects(api.importMetadataIssues('00000000-0000-0000-0000-000000000000', null, new AbortController().signal), /saved import issue page/);
    await assert.rejects(api.importMetadataIssues(importIdentity, '../outside', new AbortController().signal), /saved import issue page/);
  }); assert.equal(calls, 0);
});

test('canceling metadata details never sends a retry or changes saved work', async () => {
  const stop = new AbortController(); stop.abort(); let calls = 0;
  await withBrowser(async (url, options) => {
    calls++; assert.equal(new URL(url, 'https://local.invalid').pathname, '/api/import-metadata-issues');
    assert.equal(options.signal, stop.signal); assert.equal(options.method, undefined); throw new DOMException('Canceled', 'AbortError');
  }, async api => { await assert.rejects(api.importMetadataIssues(importIdentity, null, stop.signal), { name: 'AbortError' }); });
  assert.equal(calls, 1);
});
test('import progress keeps exact decimal counts and never infers verified originals from an offset', async () => {
  await withBrowser(async () => json({ canChooseFolder: true, paused: false, imports: [validImport()] }), async api => {
    const actual = await api.imports(new AbortController().signal); assert.equal(actual.imports[0].verified, '3'); assert.equal(actual.imports[0].state, 'copying');
  });
  const bad = [ { ...validImport(), verified: '11' }, { ...validImport(), state: 'originals-verified' }, { ...validImport(), media: 10 }, { ...validImport(), verified: '03' }, { ...validImport(), id: 'not-an-identity' }, { ...validImport(), state: 'ready', issue: 'unknown' } ];
  for (const item of bad) await withBrowser(async () => json({ canChooseFolder: true, paused: false, imports: [item] }), async api => { await assert.rejects(api.imports(new AbortController().signal), /conflicting import progress/); });
});
test('metadata progress cannot claim unfinished sidecars complete or count them as verified originals', async () => {
  const progress = { total: '2', ready: '1', issues: '1', waiting: '0', finished: true };
  await withBrowser(async () => json({ canChooseFolder: true, paused: false, imports: [{ ...validImport(), metadata: progress }] }), async api => {
    const item = (await api.imports(new AbortController().signal)).imports[0]; assert.equal(item.verified, '3'); assert.equal(item.state, 'copying'); assert.deepEqual(item.metadata, progress);
  });
  const bad = [{ ...progress, total: '3' }, { ...progress, ready: 1 }, { ...progress, ready: '01' }, { ...progress, waiting: '1' }, { ...progress, issues: '0' }, { ...progress, finished: 'true' }];
  for (const metadata of bad) await withBrowser(async () => json({ canChooseFolder: true, paused: false, imports: [{ ...validImport(), metadata }] }), async api => { await assert.rejects(api.imports(new AbortController().signal), /conflicting import progress/); });
});
test('explicit import actions send captured identifiers and CSRF, never local paths or caller-made backup proofs', async () => {
  let calls = 0;
  await withBrowser(async (url, options) => {
    if (url === '/api/session') return json({ csrf: 'a'.repeat(64), hostOwner: true });
    calls++; assert.equal(url, '/api/import-start'); assert.equal(options.method, 'POST'); assert.equal(options.credentials, 'same-origin'); assert.equal(options.headers['X-MPA-CSRF'], 'a'.repeat(64)); assert.deepEqual(JSON.parse(options.body), { id: importIdentity });
    return json({ import: validImport() });
  }, async api => { await api.connect(); await api.importAction('start', importIdentity); await assert.rejects(api.importAction('start', 'arbitrary-path'), /captured import/); });
  assert.equal(calls, 1);
});
test('canceled native folder chooser has no import and malformed import pages fail closed', async () => {
  await withBrowser(async () => json({ import: null }), async api => { assert.equal(await api.importAction('folders', importIdentity), null); });
  for (const page of [{ canChooseFolder: true, imports: [] }, { canChooseFolder: true, paused: false, imports: Array(65).fill(validImport()) }]) await withBrowser(async () => json(page), async api => { await assert.rejects(api.imports(new AbortController().signal), /invalid import progress/); });
});

test('manual metadata selection lists bounded verified originals and sends only native identifiers with CSRF', async () => {
  const signal = new AbortController().signal; const sidecarKey = 'a'.repeat(64); const originalKey = 'b'.repeat(64);
  const page = { originals: [{ key: originalKey, name: 'Photos/שלום <photo>.jpg', fileId: 'verified-original' }], next: null }; let selections = 0;
  await withBrowser(async (url, options) => {
    if (url === '/api/session') return json({ csrf: 'c'.repeat(64), hostOwner: true });
    if (options.method === 'POST') {
      assert.equal(url, '/api/import-metadata-original'); assert.equal(options.headers['X-MPA-CSRF'], 'c'.repeat(64));
      assert.equal(options.credentials, 'same-origin'); assert.deepEqual(JSON.parse(options.body), { id: importIdentity, sidecarKey, originalKey });
      selections++; return new Response(null, { status: 204 });
    }
    const parsed = new URL(url, 'https://local.invalid'); assert.equal(parsed.pathname, '/api/import-original-choices'); assert.equal(parsed.searchParams.get('id'), importIdentity);
    assert.equal(options.signal, signal); assert.equal(options.credentials, 'same-origin'); return json(page);
  }, async api => {
    await api.connect(); assert.deepEqual(await api.importOriginalChoices(importIdentity, null, signal), page);
    await api.selectImportMetadataOriginal(importIdentity, sidecarKey, originalKey);
    await assert.rejects(api.selectImportMetadataOriginal(importIdentity, '../file.json', originalKey), /captured metadata file/);
    await assert.rejects(api.selectImportMetadataOriginal(importIdentity, sidecarKey, 'provider-id'), /captured metadata file/);
  }); assert.equal(selections, 1);
});

test('original choice pages reject invalid paths, receipts, oversized pages and cursors before selection', async () => {
  const original = { key: 'b'.repeat(64), name: 'photo.jpg', fileId: 'verified-original' }; const page = { originals: [original], next: null };
  for (const value of [null, {}, { ...page, originals: Array(61).fill(original) }, { ...page, originals: [original, original] },
    { ...page, next: importIdentity }, ...[{ name: '../photo.jpg' }, { name: 'C:/photo.jpg' }, { name: 'x\\photo.jpg' }, { name: '' }, { name: 'x'.repeat(4097) },
      { fileId: 'root' }, { fileId: '../provider' }, { key: 'A'.repeat(64) }].map(change => ({ ...page, originals: [{ ...original, ...change }] }))]) {
    await withBrowser(async () => json(value), async api => { await assert.rejects(api.importOriginalChoices(importIdentity, null, new AbortController().signal), /invalid original choices/); });
  }
  let calls = 0;
  await withBrowser(async () => { calls++; return json(page); }, async api => {
    await assert.rejects(api.importOriginalChoices(importIdentity, '../page', new AbortController().signal), /saved import original page/);
    await assert.rejects(api.importOriginalChoices('00000000-0000-0000-0000-000000000000', null, new AbortController().signal), /saved import original page/);
  }); assert.equal(calls, 0);
});

test('explicit metadata cleanup sends only the saved import with CSRF and validates bounded slot counts', async () => {
  let calls = 0;
  await withBrowser(async (url, options) => {
    if (url === '/api/session') return json({ csrf: 'c'.repeat(64), hostOwner: true });
    calls++; assert.equal(url, '/api/import-metadata-cleanup'); assert.equal(options.method, 'POST'); assert.equal(options.credentials, 'same-origin');
    assert.equal(options.headers['X-MPA-CSRF'], 'c'.repeat(64)); assert.deepEqual(JSON.parse(options.body), { id: importIdentity });
    return json({ retired: '1', kept: '2' });
  }, async api => { await api.connect(); assert.deepEqual(await api.cleanupImportMetadata(importIdentity), { retired: '1', kept: '2' }); await assert.rejects(api.cleanupImportMetadata('../source.bin'), /saved import/); });
  assert.equal(calls, 1);
  for (const value of [null, {}, { retired: 1, kept: '0' }, { retired: '01', kept: '0' }, { retired: '-1', kept: '0' }, { retired: '1', kept: '6400000' }, { retired: '0', kept: '900000000' }])
    await withBrowser(async () => json(value), async api => { await assert.rejects(api.cleanupImportMetadata(importIdentity), /cleanup reply/); });
});

test('archive selection uses the native dialog command and validates the declared capability without accepting browser paths', async () => {
  const signal = new AbortController().signal;
  for (const enabled of [true, false]) await withBrowser(async () => json({ canChooseFolder: true, canChooseArchives: enabled, paused: false, imports: [] }), async api => {
    assert.equal((await api.imports(signal)).canChooseArchives, enabled);
  });
  await withBrowser(async () => json({ canChooseFolder: true, paused: false, imports: [] }), async api => { assert.equal((await api.imports(signal)).canChooseArchives, false); });
  for (const invalid of [null, 'true', 1, {}]) await withBrowser(async () => json({ canChooseFolder: true, canChooseArchives: invalid, paused: false, imports: [] }), async api => {
    await assert.rejects(api.imports(signal), /invalid import progress/);
  });
  let calls = 0;
  await withBrowser(async (url, options) => {
    if (url === '/api/session') return json({ csrf: 'c'.repeat(64), hostOwner: true });
    calls++; assert.equal(url, '/api/import-archives'); assert.equal(options.method, 'POST'); assert.equal(options.credentials, 'same-origin');
    assert.equal(options.headers['X-MPA-CSRF'], 'c'.repeat(64)); assert.deepEqual(JSON.parse(options.body), { id: importIdentity }); return json({ import: null });
  }, async api => { await api.connect(); assert.equal(await api.importAction('archives', importIdentity), null); await assert.rejects(api.importAction('archives', 'C:/photos.zip'), /captured import/); });
  assert.equal(calls, 1);
});
