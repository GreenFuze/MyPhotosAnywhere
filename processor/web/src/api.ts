export interface Account { id: string; name: string; connected: boolean; revision: number }
export interface StorageBudget { reserveBytes: number; cacheLimitBytes: number; reservedBytes: number; reservableBytes: number }
export interface Snapshot { libraryId: string; state: string; accounts: Account[]; pending: { id: string; expectedAccountId: string | null }[]; credentialCleanupCount: number; storageAttention: StorageBudget | null }
export interface CatalogPhoto { id: string; accountId: string; name: string; mimeType: string; width: number | null; height: number | null; effectiveUtc: number | null; dateProvenance: 'Unknown' | 'DriveCapture' | 'DriveModification'; rawCapture: string | null }
export interface CatalogPage { discoveryRevision: number; configurationRevision: number; photos: CatalogPhoto[]; olderCursor: string | null; newerCursor: string | null }
export type LibraryCatalogPage = { available: false; stageId: null; photos: []; olderCursor: null; newerCursor: null } | { available: true; stageId: string; photos: CatalogPhoto[]; olderCursor: string | null; newerCursor: string | null };
export interface LibraryViewerWindow extends ViewerWindow { stageId: string; accountId: string | null }
const libraryIdentity = (value: unknown): value is string => typeof value === 'string' && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(value) && value !== '00000000-0000-0000-0000-000000000000';
const libraryAccount = (value: unknown): value is string => typeof value === 'string' && /^[A-Za-z0-9_-]{1,256}$/.test(value);
export interface ViewerEntry { position: number; assetId: string; photo: CatalogPhoto | null }
export interface ViewerWindow { id: string; count: number; position: number; entries: ViewerEntry[] }
export interface Source { id: string; accountId: string; folderId: string; displayName: string; includeDescendants: boolean; enabled: boolean; revision: number; accountConnected: boolean }
export interface Sources { revision: number; sources: Source[] }
export interface Folder { id: string; name: string; canListChildren: boolean }
export interface FolderLocation { current: Folder; children: Folder[]; nextCursor: string | null; incompleteSearch: boolean }
export interface LibraryController { libraryId: string; actorId: string; epoch: number; accountId: string; folderId: string; publicKeySpki: string; keyId: string }
export interface LibraryConnection { code: string; documentSha256: string; controllerKeyId: string }
export interface WorkspaceInspection {
  workspace: { accountId: string; folderId: string; name: string; driveVersion: number; shared: boolean; verifiedEmpty: boolean };
  accountRevision: number; sources: Sources;
  identity: { controllerKeyId: string }; controller: LibraryController | null;
}
export interface WorkspaceApproval {
  accountId: string; folderId: string; accountRevision: number; folderVersion: number; configurationRevision: number;
  controllerKeyId: string; acceptSharedWorkspace: boolean; associateSelectedSources: boolean;
}
export interface SourceScope { sourceId: string; sourceRevision: number; accountId: string; accountRevision: number; rootFolderId: string; includeDescendants: boolean }
export interface SourceJob {
  jobId: string; scope: SourceScope; current: boolean;
  summary: { queued: number; running: number; ready: number; failed: number; unsupported: number; canceled: number; paused: boolean; isCanceled: boolean };
  problems: { itemId: string; stage: string; code: string; message: string; retryable: boolean }[];
  moreProblems: boolean; retryableFailures: number;
}
export interface ActivityPage { jobs: SourceJob[]; nextCursor: string | null }

export class AuthenticationRequired extends Error {}
export interface LibraryPublication {
  stage: 'NeedsApproval' | 'Running' | 'PoliciesPublished' | 'NeedsAttention' | 'Stopping' | 'Stopped';
  detail: string | null; canRetry: boolean;
}

/** The browser never receives a Google credential. CSRF stays in this page's memory;
 * the session cookie is HttpOnly. Closing a page cancels reads, not engine jobs. */
export class DesktopApi {
  private csrf = '';
  hostOwner = false;
  async connect(): Promise<void> {
    const fragment = new URLSearchParams(location.hash.slice(1));
    const secret = fragment.get('connect');
    if (location.hash) history.replaceState(null, '', location.pathname);
    let response: Response;
    if (secret !== null) {
      if (!/^[a-f0-9]{64}$/.test(secret)) throw new AuthenticationRequired('This connection link is invalid.');
      response = await fetch('/api/session', { method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ secret }) });
    } else response = await fetch('/api/session', { credentials: 'same-origin' });
    await this.check(response);
    const session: unknown = await response.json();
    if (typeof session !== 'object' || session === null || !('csrf' in session) || typeof session.csrf !== 'string' || !/^[a-f0-9]{64}$/.test(session.csrf)) throw new Error('MPA returned an invalid browser session.');
    this.csrf = session.csrf;
    this.hostOwner = 'hostOwner' in session && session.hostOwner === true;
  }
  async status(signal: AbortSignal): Promise<Snapshot> {
    return await this.get<Snapshot>('/api/status', signal);
  }
  async libraryCatalog(account: string | null, cursor: string | null, newer: boolean, date: number | null, signal: AbortSignal): Promise<LibraryCatalogPage> {
    if (account !== null && !libraryAccount(account)) throw new Error('Invalid shared-library account.');
    const query = new URLSearchParams(); if (account !== null) query.set('account', account); if (cursor !== null) query.set('cursor', cursor);
    if (newer) query.set('direction', 'newer'); if (date !== null) query.set('date', date.toString());
    const value = await this.get<unknown>('/api/library/catalog?' + query, signal);
    const invalid = () => new Error('MPA returned an invalid shared photo page. The visible photos were retained.');
    if (typeof value !== 'object' || value === null || !('available' in value) || typeof value.available !== 'boolean' || !('stageId' in value) ||
      !('photos' in value) || !Array.isArray(value.photos) || value.photos.length > 60 || !('olderCursor' in value) || !('newerCursor' in value)) throw invalid();
    if (!value.available) {
      if (cursor !== null || value.stageId !== null || value.photos.length !== 0 || value.olderCursor !== null || value.newerCursor !== null) throw invalid();
    } else {
      if (!libraryIdentity(value.stageId)) throw invalid();
      if (cursor !== null) this.libraryCursor(cursor, value.stageId, account);
      if (value.olderCursor !== null) this.libraryCursor(value.olderCursor, value.stageId, account);
      if (value.newerCursor !== null) this.libraryCursor(value.newerCursor, value.stageId, account);
      const ids = new Set<string>();
      for (const photo of value.photos) { const valid = this.libraryPhoto(photo, account); if (ids.has(valid.id)) throw invalid(); ids.add(valid.id); }
    }
    return value as unknown as LibraryCatalogPage;
  }
  libraryPreview(stage: string, asset: string, account: string | null): string {
    if (!libraryIdentity(stage) || !libraryIdentity(asset) || account !== null && !libraryAccount(account)) throw new Error('Invalid captured shared photo identity.');
    const query = new URLSearchParams({ stage, asset }); if (account !== null) query.set('account', account); return '/api/library/preview?' + query;
  }
  async openLibraryViewer(id: string, selectedId: string, accountId: string | null, stageId: string, signal: AbortSignal): Promise<LibraryViewerWindow> {
    if (!libraryIdentity(id) || !libraryIdentity(selectedId) || !libraryIdentity(stageId) || accountId !== null && !libraryAccount(accountId)) throw new Error('Invalid captured shared viewer context.');
    const response = await fetch('/api/viewers', { method: 'POST', credentials: 'same-origin', signal,
      headers: { 'Content-Type': 'application/json', 'X-MPA-CSRF': this.csrf }, body: JSON.stringify({ id, selectedId, accountId, stageId }) });
    await this.check(response); return this.libraryViewerWindow(await response.json(), stageId, accountId);
  }
  async libraryViewer(id: string, position: number, stage: string, account: string | null, signal: AbortSignal): Promise<LibraryViewerWindow> {
    return this.libraryViewerWindow(await this.get<unknown>('/api/viewers?' + new URLSearchParams({ viewer: id, position: position.toString() }), signal), stage, account);
  }
  private libraryViewerWindow(value: unknown, stage: string, account: string | null): LibraryViewerWindow {
    const frame = this.viewerWindow(value);
    if (!('stageId' in frame) || frame.stageId !== stage || !('accountId' in frame) || frame.accountId !== account)
      throw new Error('MPA returned another shared viewer context. Navigation stopped.');
    const ids = new Set<string>();
    for (const entry of frame.entries) { if (!libraryIdentity(entry.assetId) || ids.has(entry.assetId)) throw new Error('MPA returned conflicting shared photo identities.'); ids.add(entry.assetId); if (entry.photo !== null) this.libraryPhoto(entry.photo, account); }
    return frame as LibraryViewerWindow;
  }
  private libraryCursor(value: unknown, stage: string, account: string | null): void {
    const invalid = () => new Error('MPA returned an invalid shared photo cursor. The visible photos were retained.');
    if (typeof value !== 'string' || value.length > 400) throw invalid();
    const fields = value.split('|');
    if (fields.length !== 5 || fields[0] !== 'c1' || fields[1] !== stage || fields[2] !== (account ?? '*') || !libraryIdentity(fields[4])) throw invalid();
    const date = fields[3]; if (typeof date !== 'string') throw invalid(); if (date !== 'unknown' && (!/^(0|[1-9][0-9]*|-[1-9][0-9]*)$/.test(date) || !Number.isSafeInteger(Number(date)) || Number(date) < -62135596800000 || Number(date) > 253402300799999)) throw invalid();
  }
  private libraryPhoto(value: unknown, account: string | null): CatalogPhoto {
    const invalid = () => new Error('MPA returned an invalid shared photo identity. The visible photos were retained.');
    if (typeof value !== 'object' || value === null || !('id' in value) || !libraryIdentity(value.id) || !('accountId' in value) || !libraryAccount(value.accountId) || account !== null && value.accountId !== account ||
      !('name' in value) || typeof value.name !== 'string' || value.name.length > 4096 || !('mimeType' in value) || typeof value.mimeType !== 'string' || value.mimeType.length > 256 || !/^(image|video)\//.test(value.mimeType) ||
      !('effectiveUtc' in value) || value.effectiveUtc !== null && (typeof value.effectiveUtc !== 'number' || !Number.isSafeInteger(value.effectiveUtc) || value.effectiveUtc < -62135596800000 || value.effectiveUtc > 253402300799999) ||
      !('dateProvenance' in value) || !['Unknown', 'DriveCapture', 'DriveModification'].includes(value.dateProvenance as string) ||
      !('rawCapture' in value) || value.rawCapture !== null && (typeof value.rawCapture !== 'string' || value.rawCapture.length > 128)) throw invalid();
    if (!('width' in value) || !('height' in value)) throw invalid();
    for (const dimension of [value.width, value.height]) if (dimension !== null && (typeof dimension !== 'number' || !Number.isInteger(dimension) || dimension < 1 || dimension > 1000000)) throw invalid();
    return value as CatalogPhoto;
  }
  async catalog(account: string | null, cursor: string | null, newer: boolean, date: number | null, signal: AbortSignal): Promise<CatalogPage> {
    const query = new URLSearchParams(); if (account) query.set('account', account); if (cursor) query.set('cursor', cursor);
    if (newer) query.set('direction', 'newer'); if (date !== null) query.set('date', date.toString());
    return await this.get<CatalogPage>('/api/catalog?' + query, signal);
  }
  catalogPreview(asset: string): string { return '/api/catalog-preview?' + new URLSearchParams({ asset }); }
  async openViewer(id: string, selectedId: string, accountId: string | null, signal: AbortSignal): Promise<ViewerWindow> {
    const response = await fetch('/api/viewers', { method: 'POST', credentials: 'same-origin', signal,
      headers: { 'Content-Type': 'application/json', 'X-MPA-CSRF': this.csrf }, body: JSON.stringify({ id, selectedId, accountId }) });
    await this.check(response); return this.viewerWindow(await response.json());
  }
  async viewer(id: string, position: number, signal: AbortSignal): Promise<ViewerWindow> {
    return this.viewerWindow(await this.get<unknown>('/api/viewers?' + new URLSearchParams({ viewer: id, position: position.toString() }), signal));
  }
  async closeViewer(id: string): Promise<void> {
    await this.check(await fetch('/api/viewers?' + new URLSearchParams({ viewer: id }), { method: 'DELETE', credentials: 'same-origin', keepalive: true,
      headers: { 'Content-Type': 'application/json', 'X-MPA-CSRF': this.csrf }, body: '{}' }));
  }
  private viewerWindow(value: unknown): ViewerWindow {
    const identity = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
    if (typeof value !== 'object' || value === null || !('id' in value) || typeof value.id !== 'string' ||
      !identity.test(value.id) ||
      !('count' in value) || !Number.isSafeInteger(value.count) || (value.count as number) < 1 ||
      !('position' in value) || !Number.isSafeInteger(value.position) || (value.position as number) < 0 || (value.position as number) >= (value.count as number) ||
      !('entries' in value) || !Array.isArray(value.entries) || value.entries.length < 1 || value.entries.length > 90)
      throw new Error('MPA returned an invalid photo sequence. The selected photo was retained.');
    const entries: unknown[] = value.entries; let previous = -1;
    for (const entry of entries) {
      if (typeof entry !== 'object' || entry === null || !('position' in entry) || typeof entry.position !== 'number' || !Number.isSafeInteger(entry.position) ||
        entry.position <= previous || previous !== -1 && entry.position !== previous + 1 || entry.position >= (value.count as number) || !('assetId' in entry) || typeof entry.assetId !== 'string' || !identity.test(entry.assetId) ||
        !('photo' in entry) || entry.photo !== null && (typeof entry.photo !== 'object' || !('id' in entry.photo) || entry.photo.id !== entry.assetId))
        throw new Error('MPA returned a conflicting photo identity. Navigation stopped.');
      previous = entry.position;
    }
    if (!entries.some(entry => (entry as ViewerEntry).position === value.position)) throw new Error('MPA lost the selected position in this sequence.');
    return value as unknown as ViewerWindow;
  }
  async sources(signal: AbortSignal): Promise<Sources> { return await this.get<Sources>('/api/sources', signal); }
  async library(signal: AbortSignal): Promise<{ controller: LibraryController | null }> { return await this.get('/api/library', signal); }
  async libraryConnection(signal: AbortSignal): Promise<LibraryConnection | null> {
    const value = await this.get<unknown>('/api/library-connection', signal);
    const invalid = () => new Error('MPA returned an invalid library connection. Your saved setup was retained.');
    if (typeof value !== 'object' || value === null || !('connection' in value)) throw invalid();
    if (value.connection === null) return null;
    const result = value.connection;
    if (typeof result !== 'object' || !('code' in result) || typeof result.code !== 'string' || result.code.length > 4096 ||
      !/^mpa-library-v1:[A-Za-z0-9_-]+$/.test(result.code) ||
      !('documentSha256' in result) || typeof result.documentSha256 !== 'string' || !/^[a-f0-9]{64}$/.test(result.documentSha256) ||
      !('controllerKeyId' in result) || typeof result.controllerKeyId !== 'string' || !/^[a-f0-9]{64}$/.test(result.controllerKeyId)) throw invalid();
    return result as LibraryConnection;
  }
  async libraryPublication(signal: AbortSignal): Promise<LibraryPublication> {
    return this.publicationStatus(await this.get<unknown>('/api/library-publication', signal));
  }
  async retryLibraryPublication(): Promise<LibraryPublication> {
    return this.publicationStatus(await this.sourceCommand('/api/library-publication', {}));
  }
  private publicationStatus(value: unknown): LibraryPublication {
    if (typeof value !== 'object' || value === null || !('stage' in value) || typeof value.stage !== 'string' ||
      !['NeedsApproval', 'Running', 'PoliciesPublished', 'NeedsAttention', 'Stopping', 'Stopped'].includes(value.stage) ||
      !('detail' in value) || value.detail !== null && (typeof value.detail !== 'string' || value.detail.length > 1024) ||
      !('canRetry' in value) || typeof value.canRetry !== 'boolean' || value.canRetry && value.stage !== 'NeedsAttention')
      throw new Error('MPA returned an invalid library setup status. Your saved setup was retained.');
    return value as LibraryPublication;
  }
  async inspectWorkspace(account: string, folder: string, signal: AbortSignal): Promise<WorkspaceInspection> {
    return await this.get('/api/workspace-inspection?' + new URLSearchParams({ account, folder }), signal);
  }
  async approveWorkspace(approval: WorkspaceApproval): Promise<{ controller: LibraryController }> {
    const response = await fetch('/api/library', { method: 'POST', credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json', 'X-MPA-CSRF': this.csrf }, body: JSON.stringify(approval) });
    await this.check(response); return await response.json() as { controller: LibraryController };
  }
  async activity(cursor: string | null, signal: AbortSignal): Promise<ActivityPage> {
    return await this.get<ActivityPage>('/api/activity' + (cursor ? '?' + new URLSearchParams({ cursor }) : ''), signal);
  }
  async scanSource(scope: SourceScope, requestId: string): Promise<void> {
    await this.sourceCommand('/api/source-scans', { requestId, sourceId: scope.sourceId, sourceRevision: scope.sourceRevision, accountRevision: scope.accountRevision });
  }
  async retrySource(job: SourceJob): Promise<number> {
    const result = await this.sourceCommand('/api/source-retries', { jobId: job.jobId, sourceId: job.scope.sourceId, sourceRevision: job.scope.sourceRevision, accountRevision: job.scope.accountRevision });
    if (typeof result !== 'object' || result === null || !('queued' in result) || typeof result.queued !== 'number' || !Number.isSafeInteger(result.queued) || result.queued < 0) throw new Error('MPA returned an invalid retry count. Refresh Activity to inspect the retained job.');
    return result.queued;
  }
  private async sourceCommand(path: string, body: unknown): Promise<unknown> {
    const response = await fetch(path, { method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json', 'X-MPA-CSRF': this.csrf }, body: JSON.stringify(body) });
    await this.check(response); return await response.json();
  }
  async folders(account: string, folder: string, cursor: string | null, signal: AbortSignal): Promise<FolderLocation> {
    const query = new URLSearchParams({ account, folder }); if (cursor !== null) query.set('cursor', cursor);
    return await this.get<FolderLocation>('/api/folders?' + query, signal);
  }
  async addSource(accountId: string, folderId: string, includeDescendants: boolean, expectedRevision: number): Promise<Sources> {
    return await this.sourceChange('POST', { accountId, folderId, includeDescendants, expectedRevision });
  }
  async updateSource(source: Source, enabled: boolean, includeDescendants: boolean, expectedRevision: number): Promise<Sources> {
    return await this.sourceChange('PUT', { id: source.id, enabled, includeDescendants, expectedSourceRevision: source.revision, expectedRevision });
  }
  private async sourceChange(method: 'POST' | 'PUT', body: object): Promise<Sources> {
    const response = await fetch('/api/sources', { method, credentials: 'same-origin', headers: { 'Content-Type': 'application/json', 'X-MPA-CSRF': this.csrf }, body: JSON.stringify(body) });
    await this.check(response); return await response.json() as Sources;
  }
  async pause(paused: boolean): Promise<void> {
    await this.check(await fetch(paused ? '/api/resume' : '/api/pause', { method: 'POST', credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json', 'X-MPA-CSRF': this.csrf }, body: '{}' }));
  }
  async signOut(): Promise<void> {
    await this.check(await fetch('/api/session', { method: 'DELETE', credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json', 'X-MPA-CSRF': this.csrf }, body: '{}' }));
    this.csrf = '';
  }
  async account(operation: 'connect' | 'check' | 'disconnect' | 'resume' | 'abandon' | 'cleanup', accountId: string | null = null, pendingId: string | null = null, expectedRevision: number | null = null): Promise<void> {
    await this.check(await fetch('/api/accounts', { method: 'POST', credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json', 'X-MPA-CSRF': this.csrf }, body: JSON.stringify({ operation, accountId, pendingId, expectedRevision }) }));
  }
  private async get<T>(path: string, signal: AbortSignal): Promise<T> {
    const response = await fetch(path, { signal, credentials: 'same-origin' }); await this.check(response); return await response.json() as T;
  }
  private async check(response: Response): Promise<void> {
    if (response.status === 401) throw new AuthenticationRequired('Open MPA on the host computer to connect this browser.');
    if (!response.ok) {
      let message = 'MPA could not complete this request. Your photos and saved work were preserved.';
      if (response.headers.get('Content-Type')?.includes('application/json')) {
        const body: unknown = await response.json();
        if (typeof body === 'object' && body !== null && 'message' in body && typeof body.message === 'string') message = body.message;
      }
      throw new Error(message);
    }
  }
}
