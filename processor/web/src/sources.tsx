import { useEffect, useRef, useState } from 'react';
import type { Account, DesktopApi, Folder, FolderLocation, Source, Sources } from './api';
import './sources.css';

const errorText = (error: unknown) => error instanceof Error ? error.message : 'MPA could not update Sources.';

export function SourceSettings({ api, accounts, back }: { api: DesktopApi; accounts: Account[]; back: () => void }) {
  const [sources, setSources] = useState<Sources | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [picker, setPicker] = useState(false);
  useEffect(() => {
    const controller = new AbortController();
    api.sources(controller.signal).then(setSources).catch(error => { if (!controller.signal.aborted) setNotice(errorText(error)); });
    return () => controller.abort();
  }, [api]);
  async function reload() {
    setBusy(true);
    try { setSources(await api.sources(new AbortController().signal)); setNotice(null); }
    catch (error) { setNotice(errorText(error)); } finally { setBusy(false); }
  }
  async function update(source: Source, enabled: boolean, descendants: boolean) {
    if (!sources) return; setBusy(true); setNotice(null);
    try { setSources(await api.updateSource(source, enabled, descendants, sources.revision)); setNotice('Saved on this desktop. Source images and existing jobs were preserved.'); }
    catch (error) { setNotice(errorText(error)); } finally { setBusy(false); }
  }
  return <section className="settings"><button className="back" onClick={back}>← Settings</button><h1>Sources</h1>
    <p className="muted">Choose the Drive folders in your library. Their original names and structure stay as they are.</p>
    <div className="row-actions"><button disabled={busy || !sources || !accounts.some(account => account.connected)} onClick={() => setPicker(true)}>Add a folder</button><button disabled={busy} onClick={() => { void reload(); }}>Refresh</button></div>
    {notice && <p role="status" className="inline-message">{notice}</p>}
    {!sources && !notice && <p role="status">Loading sources…</p>}
    {sources?.sources.length === 0 && <p>No source folders selected yet.</p>}
    {sources?.sources.map(source => <section className="setting-card" key={source.id}><h2 dir="auto">{source.displayName}</h2>
      <p>{accounts.find(account => account.id === source.accountId)?.name ?? 'Google Drive account'} · {source.accountConnected ? source.enabled ? 'Connected' : 'Disabled' : 'Account disconnected'}</p>
      <label className="switch-row"><span>Include in this library</span><input type="checkbox" checked={source.enabled} disabled={busy} onChange={event => { void update(source, event.target.checked, source.includeDescendants); }} /></label>
      <label className="switch-row"><span>Include subfolders</span><input type="checkbox" checked={source.includeDescendants} disabled={busy} onChange={event => { void update(source, source.enabled, event.target.checked); }} /></label>
    </section>)}
    <p className="muted">Source settings are saved on the desktop. Drive publication and indexing are being connected.</p>
    {picker && sources && <FolderPicker api={api} accounts={accounts.filter(account => account.connected)} revision={sources.revision}
      close={() => setPicker(false)} saved={value => { setSources(value); setPicker(false); setNotice('Folder saved on this desktop. Its original files were not changed.'); }} />}
  </section>;
}

function FolderPicker({ api, accounts, revision, close, saved }: { api: DesktopApi; accounts: Account[]; revision: number; close: () => void; saved: (value: Sources) => void }) {
  const [account, setAccount] = useState(accounts[0]?.id ?? '');
  const [location, setLocation] = useState<FolderLocation | null>(null);
  const [path, setPath] = useState<Folder[]>([]);
  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [descendants, setDescendants] = useState(true);
  const [notice, setNotice] = useState<string | null>(null);
  const [cursor, setCursor] = useState<string | null>(null);
  const [previous, setPrevious] = useState<(string | null)[]>([]);
  const dialog = useRef<HTMLDialogElement>(null);
  const request = useRef<AbortController | null>(null);
  const generation = useRef(0);
  useEffect(() => { const current = dialog.current; current?.showModal(); return () => { request.current?.abort(); current?.close(); }; }, []);
  useEffect(() => {
    setLocation(null); setPath([]); setCursor(null); setPrevious([]);
    if (account) void navigate('root', [], null, []);
    return () => request.current?.abort();
    // The account transition owns its initial root read. Other navigation is explicit.
  }, [account]);
  async function navigate(folder: string, ancestors: Folder[], next: string | null, history: (string | null)[]) {
    request.current?.abort(); const controller = new AbortController(); request.current = controller;
    const current = ++generation.current; setLoading(true); setNotice(null);
    try {
      const result = await api.folders(account, folder, next, controller.signal);
      if (controller.signal.aborted || current !== generation.current) return;
      setLocation(result); setPath([...ancestors, result.current]); setCursor(next); setPrevious(history.slice(-64));
      if (result.incompleteSearch) setNotice('Google Drive returned a partial folder listing.');
    } catch (error) { if (!controller.signal.aborted && current === generation.current) setNotice(errorText(error)); }
    finally { if (!controller.signal.aborted && current === generation.current) setLoading(false); }
  }
  async function add() {
    if (!location || saving) return; setSaving(true); setNotice(null);
    try { saved(await api.addSource(account, location.current.id, descendants, revision)); }
    catch (error) { setNotice(errorText(error)); } finally { setSaving(false); }
  }
  return <dialog ref={dialog} className="folder-picker" aria-labelledby="folder-picker-title" onCancel={event => { if (saving) event.preventDefault(); else close(); }}>
    <header><h2 id="folder-picker-title">Choose a source folder</h2><button onClick={close} disabled={saving} aria-label="Close folder picker">✕</button></header>
    <label className="picker-account">Google Drive account<select value={account} disabled={saving} onChange={event => setAccount(event.target.value)}>{accounts.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}</select></label>
    <nav className="breadcrumbs" aria-label="Folder path">{path.map((folder, index) => <button key={folder.id} disabled={loading || saving} onClick={() => { void navigate(folder.id, path.slice(0, index), null, []); }} dir="auto">{folder.name}</button>)}</nav>
    <div className="folder-list" aria-busy={loading}>{location?.children.map(folder => <button key={folder.id} className="folder-row" disabled={loading || saving || !folder.canListChildren} onClick={() => { void navigate(folder.id, path, null, []); }}>
      <svg viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" strokeWidth="1.6"><path d="M3 5h7l2 3h9v12H3z" /></svg><span dir="auto">{folder.name}</span><span aria-hidden="true">›</span></button>)}
      {!loading && location?.children.length === 0 && <p>No subfolders. You can use this folder.</p>}
      {loading && <p role="status">Loading folders…</p>}</div>
    <div className="row-actions">{previous.length > 0 && location && <button disabled={loading || saving} onClick={() => { void navigate(location.current.id, path.slice(0, -1), previous[previous.length - 1] ?? null, previous.slice(0, -1)); }}>Previous page</button>}
      {location?.nextCursor && <button disabled={loading || saving} onClick={() => { void navigate(location.current.id, path.slice(0, -1), location.nextCursor, [...previous, cursor]); }}>Next page</button>}</div>
    {notice && <p role="alert">{notice}</p>}
    <footer><label className="switch-row"><span>Include subfolders</span><input type="checkbox" checked={descendants} disabled={saving} onChange={event => setDescendants(event.target.checked)} /></label>
      <button className="primary" disabled={loading || saving || !location?.current.canListChildren} onClick={() => { void add(); }}>{saving ? 'Saving folder…' : 'Use this folder'}</button></footer>
  </dialog>;
}
