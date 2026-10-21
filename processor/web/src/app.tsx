import { useEffect, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { AuthenticationRequired, DesktopApi } from './api';
import type { Snapshot } from './api';
import { CatalogGallery } from './gallery';
import { SourceSettings } from './sources';
import { Activity } from './activity';
import './app.css';

const api = new DesktopApi();
const message = (error: unknown): string => error instanceof Error ? error.message : 'MPA encountered an unexpected failure.';

function Icon({ name }: { name: 'photos' | 'settings' | 'close' | 'back' | 'next' | 'pause' | 'play' | 'cloud' | 'activity' }) {
  const paths = {
    photos: 'M3 3h18v18H3z M3 16l5-5 4 4 3-3 6 6 M8 7h.01',
    settings: 'M12 8a4 4 0 1 0 0 8 4 4 0 0 0 0-8 M9 3h6l1 3 3 1 2 5-2 5-3 1-1 3H9l-1-3-3-1-2-5 2-5 3-1z',
    close: 'M6 6l12 12 M6 18L18 6', back: 'M15 5l-7 7 7 7', next: 'M9 5l7 7-7 7',
    pause: 'M8 5v14 M16 5v14', play: 'M8 4l12 8-12 8z', cloud: 'M6 18a5 5 0 0 1-1-10 7 7 0 0 1 13-1 5 5 0 0 1 0 11z',
    activity: 'M3 12h4l3-8 4 16 3-8h4'
  };
  return <svg viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round"><path d={paths[name]} /></svg>;
}

function App() {
  const [access, setAccess] = useState<'connecting' | 'ready' | 'locked'>('connecting');
  const [notice, setNotice] = useState<string | null>(null);
  const [snapshot, setSnapshot] = useState<Snapshot | null>(null);
  const [account, setAccount] = useState('');
  const [settings, setSettings] = useState(false);
  const [sourcesOpen, setSourcesOpen] = useState(false);
  const [activityOpen, setActivityOpen] = useState(false);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    const controller = new AbortController();
    api.connect().then(() => api.status(controller.signal)).then(status => {
      if (controller.signal.aborted) return;
      setSnapshot(status); setAccess('ready');
    }).catch(error => { if (!controller.signal.aborted) { setNotice(message(error)); setAccess('locked'); } });
    return () => controller.abort();
  }, []);

  useEffect(() => {
    if (access !== 'ready' || (!settings && !activityOpen) || busy) return;
    const controller = new AbortController(); let timer: ReturnType<typeof setTimeout> | undefined;
    async function poll() {
      try { const status = await api.status(controller.signal); if (!controller.signal.aborted) setSnapshot(status); }
      catch (error) { if (!controller.signal.aborted) { setNotice(message(error)); if (error instanceof AuthenticationRequired) setAccess('locked'); } }
      finally { if (!controller.signal.aborted) timer = setTimeout(() => { void poll(); }, 2500); }
    }
    void poll(); return () => { controller.abort(); if (timer !== undefined) clearTimeout(timer); };
  }, [access, settings, activityOpen, busy]);

  async function pause() {
    setBusy(true); setNotice(null);
    try { await api.pause(snapshot?.state === 'paused'); setSnapshot(await api.status(new AbortController().signal)); }
    catch (error) { setNotice(message(error)); } finally { setBusy(false); }
  }
  async function accountAction(operation: 'connect' | 'check' | 'disconnect' | 'resume' | 'abandon' | 'cleanup', accountId: string | null = null, pendingId: string | null = null, revision: number | null = null) {
    setBusy(true); setNotice(operation === 'connect' || operation === 'resume' ? 'Complete Google authorization in the host computer’s browser.' : null);
    try {
      await api.account(operation, accountId, pendingId, revision);
      const status = await api.status(new AbortController().signal); setSnapshot(status);
      if (account && !status.accounts.some(item => item.id === account)) setAccount('');
      setNotice(operation === 'check' ? 'Google Drive connection verified.' : 'Google account settings updated.');
    } catch (error) { setNotice(message(error));
      try { setSnapshot(await api.status(new AbortController().signal)); }
      catch (refreshError) { setNotice(message(error) + ' Settings could not refresh: ' + message(refreshError)); }
    } finally { setBusy(false); }
  }
  return <>
    <header className="app-bar"><a className="brand" href="/" aria-label="My Photos Anywhere home"><img src="/mpa.svg" alt="" /><span>My Photos Anywhere</span></a>
      <div className="bar-actions">{access === 'ready' && <><label className="account"><span className="visually-hidden">Google Drive account</span><select value={account} onChange={event => setAccount(event.target.value)}>
        <option value="">All accounts</option>{snapshot?.accounts.map(item => <option key={item.id} value={item.id}>{item.name}{item.connected ? '' : ' � Disconnected'}</option>)}</select></label>
        <button className="icon" onClick={() => { setActivityOpen(value => !value); setSourcesOpen(false); }} aria-label="Activity" aria-expanded={activityOpen}><Icon name="activity" /></button>
        <button className="icon" onClick={() => { setSettings(value => !value); setSourcesOpen(false); setActivityOpen(false); }} aria-label="Settings" aria-expanded={settings}><Icon name="settings" /></button></>}</div>
    </header>
    <main>
      {access !== 'ready' ? <section className="welcome"><Icon name="photos" /><h1>{access === 'connecting' ? 'Opening your photos…' : 'Connect to My Photos Anywhere'}</h1>
        <p>{notice ?? 'Open MPA on the computer running your library to connect this browser.'}</p></section> : activityOpen && snapshot ? <Activity api={api} snapshot={snapshot} busy={busy} pause={pause} back={() => { setActivityOpen(false); setSettings(true); }} /> : sourcesOpen && snapshot ? <SourceSettings api={api} accounts={snapshot.accounts} back={() => setSourcesOpen(false)} /> : settings ?
        <section className="settings"><button className="back" onClick={() => setSettings(false)}><Icon name="back" />Back to Photos</button><h1>Settings</h1>
          <button className="setting-card setting-link" onClick={() => setSourcesOpen(true)}><span><strong>Sources</strong><span>Choose the Drive folders in your library</span></span><span aria-hidden="true">›</span></button>
          <button className="setting-card setting-link" onClick={() => setActivityOpen(true)}><span><strong>Activity</strong><span>Scan progress, failures, and retry</span></span><span aria-hidden="true">›</span></button>
          <section className="setting-card"><h2>Processing</h2><p>{snapshot?.state === 'paused' ? 'Paused. Your saved jobs are retained.' : 'The engine runs independently of this browser.'}</p>
            <button onClick={pause} disabled={busy}><Icon name={snapshot?.state === 'paused' ? 'play' : 'pause'} />{snapshot?.state === 'paused' ? 'Resume processing' : 'Pause processing'}</button></section>
          <section className="setting-card"><h2>Google Drive</h2>{snapshot?.accounts.map(item => <div className="account-entry" key={item.id}><p>{item.name} · {item.connected ? 'Connected' : 'Disconnected'}</p>
            {api.hostOwner && <div className="row-actions">{item.connected && <><button disabled={busy} onClick={() => { void accountAction('check', item.id); }}>Check connection</button>
              <button disabled={busy} onClick={() => { if (window.confirm(`Disconnect ${item.name} from this computer? Your photos and saved work will be preserved.`)) void accountAction('disconnect', item.id, null, item.revision); }}>Disconnect</button></>}
              {!snapshot.pending.some(pending => pending.expectedAccountId === item.id) && <button disabled={busy} onClick={() => { void accountAction('connect', item.id); }}>Reconnect</button>}</div>}</div>)}
            {api.hostOwner ? <><button disabled={busy} onClick={() => { void accountAction('connect'); }}>Connect Google Drive</button>
              {snapshot?.pending.map(pending => <div className="account-entry" key={pending.id}><p>Unfinished Google connection</p><div className="row-actions">
                <button disabled={busy} onClick={() => { void accountAction('resume', null, pending.id); }}>Resume setup</button>
                <button disabled={busy} onClick={() => { void accountAction('abandon', null, pending.id); }}>Remove setup</button></div></div>)}
              {!!snapshot?.credentialCleanupCount && <button disabled={busy} onClick={() => { void accountAction('cleanup'); }}>Retry credential cleanup</button>}
            </> : <p>Manage Google authorization on the host computer.</p>}</section>
          <section className="setting-card"><h2>This browser</h2><p>Signing out leaves processing and other connected devices running.</p><button onClick={() => { void api.signOut().then(() => { setAccess('locked'); setNotice(null); }).catch(error => setNotice(message(error))); }}>Sign out</button></section>
        </section> : null}
      {access === 'ready' && <div hidden={settings || sourcesOpen || activityOpen}><CatalogGallery api={api} account={account}
        sources={() => { setSettings(true); setSourcesOpen(true); }} locked={() => { setAccess('locked'); setNotice('Open MPA on the host computer to connect this browser.'); }} /></div>}
      {notice && access === 'ready' && <p className="notice" role="alert">{notice}</p>}
    </main>
  </>;
}

const root = document.getElementById('root'); if (!root) throw new Error('The MPA application root is missing.');
createRoot(root).render(<App />);
