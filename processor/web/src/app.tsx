import { useEffect, useRef, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { AuthenticationRequired, DesktopApi } from './api';
import type { Photo, Snapshot } from './api';
import './app.css';

const api = new DesktopApi();
type GalleryState = { account: string; photos: Photo[]; cursor: string | null; phase: 'loading' | 'ready' | 'error'; message: string | null };
const message = (error: unknown): string => error instanceof Error ? error.message : 'MPA encountered an unexpected failure.';

function Icon({ name }: { name: 'photos' | 'settings' | 'close' | 'back' | 'next' | 'pause' | 'play' | 'cloud' }) {
  const paths = {
    photos: 'M3 3h18v18H3z M3 16l5-5 4 4 3-3 6 6 M8 7h.01',
    settings: 'M12 8a4 4 0 1 0 0 8 4 4 0 0 0 0-8 M9 3h6l1 3 3 1 2 5-2 5-3 1-1 3H9l-1-3-3-1-2-5 2-5 3-1z',
    close: 'M6 6l12 12 M6 18L18 6', back: 'M15 5l-7 7 7 7', next: 'M9 5l7 7-7 7',
    pause: 'M8 5v14 M16 5v14', play: 'M8 4l12 8-12 8z', cloud: 'M6 18a5 5 0 0 1-1-10 7 7 0 0 1 13-1 5 5 0 0 1 0 11z'
  };
  return <svg viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round"><path d={paths[name]} /></svg>;
}

function PhotoTile({ photo, account, open }: { photo: Photo; account: string; open: () => void }) {
  const [failed, setFailed] = useState(false);
  const ratio = photo.width && photo.height ? Math.max(0.55, Math.min(2.1, photo.width / photo.height)) : 1;
  return <button className="photo" style={{ flexGrow: ratio, flexBasis: `${ratio * 190}px` }} onClick={open} aria-label={`Open ${photo.name}`}>
    {failed ? <span className="preview-unavailable"><Icon name="photos" /><span>Preview unavailable</span></span> :
      <img src={api.preview(account, photo.id)} alt="" loading="lazy" decoding="async" onError={() => setFailed(true)} />}
    {photo.mimeType.startsWith('video/') && <span className="video-label">Video</span>}
  </button>;
}

function Viewer({ photos, account, selected, close }: { photos: Photo[]; account: string; selected: number; close: () => void }) {
  const [index, setIndex] = useState(selected);
  const [details, setDetails] = useState(false);
  const dialog = useRef<HTMLDialogElement>(null);
  const photo = photos[index];
  useEffect(() => {
    const current = dialog.current; current?.showModal();
    return () => current?.close();
  }, []);
  useEffect(() => {
    function key(event: KeyboardEvent) {
      if (event.key === 'ArrowLeft') setIndex(value => Math.max(0, value - 1));
      if (event.key === 'ArrowRight') setIndex(value => Math.min(photos.length - 1, value + 1));
    }
    window.addEventListener('keydown', key); return () => window.removeEventListener('keydown', key);
  }, [photos.length]);
  if (!photo) throw new Error('The selected photo is no longer in its opened sequence.');
  return <dialog ref={dialog} className="viewer" onCancel={close} aria-label="Photo viewer">
    <header><button className="icon" onClick={close} aria-label="Close photo"><Icon name="close" /></button><span>{photo.name}</span><button onClick={() => setDetails(value => !value)} aria-expanded={details}>Details</button></header>
    <div className="viewer-media"><button className="icon previous" disabled={index === 0} onClick={() => setIndex(value => value - 1)} aria-label="Previous photo"><Icon name="back" /></button>
      <img key={photo.id} src={api.preview(account, photo.id)} alt={photo.name} />
      <button className="icon next" disabled={index === photos.length - 1} onClick={() => setIndex(value => value + 1)} aria-label="Next photo"><Icon name="next" /></button></div>
    {details && <aside className="details"><h2>Details</h2><dl><dt>File</dt><dd>{photo.name}</dd><dt>Preview</dt><dd>Google Drive thumbnail · original has not been downloaded</dd>
      {photo.width && photo.height && <><dt>Dimensions</dt><dd>{photo.width} × {photo.height}</dd></>}
      {photo.capturedTime && <><dt>Capture time reported by Drive</dt><dd>{photo.capturedTime}</dd></>}
    </dl></aside>}
  </dialog>;
}

function App() {
  const [access, setAccess] = useState<'connecting' | 'ready' | 'locked'>('connecting');
  const [notice, setNotice] = useState<string | null>(null);
  const [snapshot, setSnapshot] = useState<Snapshot | null>(null);
  const [account, setAccount] = useState('');
  const [gallery, setGallery] = useState<GalleryState | null>(null);
  const [settings, setSettings] = useState(false);
  const [selected, setSelected] = useState<number | null>(null);
  const [busy, setBusy] = useState(false);
  const generation = useRef(0);
  const request = useRef<AbortController | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    api.connect().then(() => api.status(controller.signal)).then(status => {
      if (controller.signal.aborted) return;
      setSnapshot(status); setAccount(status.accounts.find(item => item.connected)?.id ?? ''); setAccess('ready');
    }).catch(error => { if (!controller.signal.aborted) { setNotice(message(error)); setAccess('locked'); } });
    return () => { controller.abort(); request.current?.abort(); };
  }, []);

  useEffect(() => {
    request.current?.abort(); const controller = new AbortController(); request.current = controller;
    const current = ++generation.current; setSelected(null);
    if (!account) { setGallery(null); return () => controller.abort(); }
    setGallery({ account, photos: [], cursor: null, phase: 'loading', message: null });
    api.photos(account, null, controller.signal).then(page => {
      if (!controller.signal.aborted && generation.current === current) setGallery({ account, photos: page.photos, cursor: page.nextCursor,
        phase: 'ready', message: page.incompleteSearch ? 'Google Drive returned a partial listing.' : null });
    }).catch(error => {
      if (controller.signal.aborted || generation.current !== current) return;
      if (error instanceof AuthenticationRequired) setAccess('locked');
      setGallery({ account, photos: [], cursor: null, phase: 'error', message: message(error) });
    });
    return () => controller.abort();
  }, [account]);

  async function more() {
    if (!gallery?.cursor || gallery.phase === 'loading' || gallery.photos.length >= 300) return;
    const current = generation.current, previous = gallery;
    request.current?.abort(); const controller = new AbortController(); request.current = controller;
    setGallery({ ...previous, phase: 'loading', message: null });
    try {
      const page = await api.photos(previous.account, previous.cursor, controller.signal);
      if (controller.signal.aborted || current !== generation.current) return;
      const ids = new Set(previous.photos.map(photo => photo.id));
      setGallery({ ...previous, photos: [...previous.photos, ...page.photos.filter(photo => !ids.has(photo.id))], cursor: page.nextCursor,
        phase: 'ready', message: page.incompleteSearch ? 'Google Drive returned a partial listing.' : null });
    } catch (error) {
      if (!controller.signal.aborted && current === generation.current) setGallery({ ...previous, phase: 'error', message: message(error) });
    }
  }
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
      if (!status.accounts.some(item => item.connected && item.id === account)) setAccount(status.accounts.find(item => item.connected)?.id ?? '');
      setNotice(operation === 'check' ? 'Google Drive connection verified.' : 'Google account settings updated.');
    } catch (error) { setNotice(message(error));
      try { setSnapshot(await api.status(new AbortController().signal)); }
      catch (refreshError) { setNotice(message(error) + ' Settings could not refresh: ' + message(refreshError)); }
    } finally { setBusy(false); }
  }
  return <>
    <header className="app-bar"><a className="brand" href="/" aria-label="My Photos Anywhere home"><img src="/mpa.svg" alt="" /><span>My Photos Anywhere</span></a>
      <div className="bar-actions">{access === 'ready' && <><label className="account"><span className="visually-hidden">Google Drive account</span><select value={account} onChange={event => setAccount(event.target.value)}>
        {snapshot?.accounts.filter(item => item.connected).map(item => <option key={item.id} value={item.id}>{item.name}</option>)}</select></label>
        <button className="icon" onClick={() => setSettings(value => !value)} aria-label="Settings" aria-expanded={settings}><Icon name="settings" /></button></>}</div>
    </header>
    <main>
      {access !== 'ready' ? <section className="welcome"><Icon name="photos" /><h1>{access === 'connecting' ? 'Opening your photos…' : 'Connect to My Photos Anywhere'}</h1>
        <p>{notice ?? 'Open MPA on the computer running your library to connect this browser.'}</p></section> : settings ?
        <section className="settings"><button className="back" onClick={() => setSettings(false)}><Icon name="back" />Back to Photos</button><h1>Settings</h1>
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
        </section> : <section className="gallery" aria-label="Photos" aria-busy={gallery?.phase === 'loading'}>
          {!account ? <div className="welcome"><Icon name="cloud" /><h1>Connect Google Drive</h1><p>Open Google account settings on the host computer to add your photos.</p></div> :
          <>{gallery?.photos.length === 0 && gallery.phase === 'ready' && <div className="welcome"><Icon name="photos" /><h1>No photos found in this Drive account</h1></div>}
            <div className="photo-grid">{gallery?.photos.map((photo, index) => <PhotoTile key={account + ':' + photo.id} photo={photo} account={account} open={() => setSelected(index)} />)}</div>
            {gallery?.phase === 'loading' && <p className="loading" role="status">Loading photos…</p>}
            {gallery?.message && <p className="inline-message" role={gallery.phase === 'error' ? 'alert' : 'status'}>{gallery.message}</p>}
            {gallery?.cursor && gallery.photos.length < 300 && <button className="load-more" disabled={gallery.phase === 'loading'} onClick={() => { void more(); }}>Load more photos</button>}
            {gallery && gallery.photos.length >= 300 && gallery.cursor && <p className="inline-message">This discovery view is limited to 300 photos. The full indexed timeline is being connected.</p>}
          </>}
        </section>}
      {notice && access === 'ready' && <p className="notice" role="alert">{notice}</p>}
    </main>
    {selected !== null && gallery && <Viewer photos={gallery.photos} account={gallery.account} selected={selected} close={() => setSelected(null)} />}
  </>;
}

const root = document.getElementById('root'); if (!root) throw new Error('The MPA application root is missing.');
createRoot(root).render(<App />);
