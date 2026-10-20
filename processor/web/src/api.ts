export interface Account { id: string; name: string; connected: boolean; revision: number }
export interface Snapshot { libraryId: string; state: string; accounts: Account[]; pending: { id: string; expectedAccountId: string | null }[]; credentialCleanupCount: number }
export interface Photo { id: string; name: string; mimeType: string; width: number | null; height: number | null; capturedTime: string | null; modifiedTime: string | null }
export interface PhotoPage { photos: Photo[]; nextCursor: string | null; incompleteSearch: boolean }

export class AuthenticationRequired extends Error {}

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
  async photos(account: string, cursor: string | null, signal: AbortSignal): Promise<PhotoPage> {
    const query = new URLSearchParams({ account });
    if (cursor !== null) query.set('cursor', cursor);
    return await this.get<PhotoPage>('/api/photos?' + query, signal);
  }
  preview(account: string, file: string): string { return '/api/preview?' + new URLSearchParams({ account, file }); }
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
