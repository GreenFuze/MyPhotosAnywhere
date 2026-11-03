import { useEffect, useState } from 'react';
import type { DesktopApi, PhotoImports, Source, ImportMetadataIssue, ImportMetadataIssueCode, ImportOriginalChoice } from './api';

const issueMessages: Record<ImportMetadataIssueCode, string> = {
  'invalid-metadata': 'This metadata file could not be read.',
  'unsupported-metadata': 'This file is not supported photo metadata.',
  'metadata-too-large': 'This metadata file exceeds the processing limit.',
  'name-mismatch': 'Its photo name does not match the reviewed export.',
  'missing-media': 'The matching photo is missing from the reviewed input.',
  'ambiguous-media': 'More than one reviewed photo matches this file.',
  'input-unavailable': 'Restore access to the captured file to retry.'
};

function MetadataOriginalPicker({ api, id, issue, disabled }: { api: DesktopApi; id: string; issue: ImportMetadataIssue; disabled: boolean }) {
  const [open, setOpen] = useState(false); const [cursor, setCursor] = useState<string | null>(null);
  const [originals, setOriginals] = useState<ImportOriginalChoice[]>([]); const [next, setNext] = useState<string | null>(null);
  const [choice, setChoice] = useState(''); const [loading, setLoading] = useState(false); const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false); const [notice, setNotice] = useState<string | null>(null);
  useEffect(() => {
    if (!open || saved) return;
    const stop = new AbortController(); setLoading(true); setChoice(''); setOriginals([]); setNext(null); setNotice(null);
    void api.importOriginalChoices(id, cursor, stop.signal).then(page => {
      if (!stop.signal.aborted) { setOriginals(page.originals); setNext(page.next); }
    }).catch(error => { if (!stop.signal.aborted) setNotice(error instanceof Error ? error.message : 'Original choices could not load.'); })
      .finally(() => { if (!stop.signal.aborted) setLoading(false); });
    return () => { stop.abort(); };
  }, [api, id, cursor, open, saved]);
  async function save() {
    const selected = originals.find(item => item.key === choice); if (!selected || saving || loading || disabled) return;
    if (!window.confirm(`Use metadata from ${issue.name} for ${selected.name}? This choice will be saved for processing. The original photo will stay unchanged.`)) return;
    setSaving(true); setNotice(null);
    try { await api.selectImportMetadataOriginal(id, issue.key, selected.key); setSaved(true); }
    catch (error) { setNotice(error instanceof Error ? error.message : 'The reply was lost. Refresh issues before retrying.'); }
    finally { setSaving(false); }
  }
  if (saved) return <p role="status">Original selection saved. Processing will continue in the background. Refresh issues to see the result.</p>;
  if (!open) return <button disabled={disabled} onClick={() => setOpen(true)}>Choose the matching original</button>;
  const selected = originals.find(item => item.key === choice);
  return <section aria-label={`Choose original for ${issue.name}`}><p>Choose a verified photo or video from this import. Check the original before saving your selection.</p>
    {notice && <p role="alert">{notice}</p>}{loading && <p role="status">Loading originals…</p>}
    {!loading && !notice && originals.length === 0 && <p>No verified originals are available on this page.</p>}
    {originals.length > 0 && <label>Matching original <select disabled={loading || saving || disabled} value={choice} onChange={event => setChoice(event.target.value)}>
      <option value="">Choose an original…</option>{originals.map(item => <option key={item.key} value={item.key}>{item.name}</option>)}
    </select></label>}
    {selected && <a href={`https://drive.google.com/file/d/${encodeURIComponent(selected.fileId)}/view`} target="_blank" rel="noopener noreferrer">View selected original in Drive</a>}
    <button disabled={!selected || loading || saving || disabled} onClick={() => { void save(); }}>{saving ? 'Saving selection…' : 'Use metadata for this original'}</button>
    {next !== null && <button disabled={loading || saving} onClick={() => setCursor(next)}>More originals</button>}
    <button disabled={loading || saving} onClick={() => { setCursor(null); setOpen(false); }}>Cancel selection</button>
  </section>;
}

function MetadataIssues({ api, id, disabled, retry }: { api: DesktopApi; id: string; disabled: boolean; retry: () => void }) {
  const [open, setOpen] = useState(false); const [cursor, setCursor] = useState<string | null>(null); const [reload, setReload] = useState(0);
  const [issues, setIssues] = useState<ImportMetadataIssue[]>([]); const [next, setNext] = useState<string | null>(null);
  const [busy, setBusy] = useState(false); const [notice, setNotice] = useState<string | null>(null);
  useEffect(() => {
    if (!open) return;
    const stop = new AbortController(); setBusy(true); setNotice(null);
    void api.importMetadataIssues(id, cursor, stop.signal).then(page => {
      if (!stop.signal.aborted) { setIssues(page.issues); setNext(page.next); }
    }).catch(error => { if (!stop.signal.aborted) setNotice(error instanceof Error ? error.message : 'Metadata issue details could not load.'); })
      .finally(() => { if (!stop.signal.aborted) setBusy(false); });
    return () => { stop.abort(); };
  }, [api, id, cursor, open, reload]);
  if (!open) return <button onClick={() => setOpen(true)}>Review metadata issues</button>;
  return <section aria-label="Metadata issues"><h3>Metadata files needing attention</h3>
    {notice && <p role="alert">{notice}</p>}{busy && <p role="status">Loading issue details…</p>}
    {issues.map(issue => <div key={issue.key}><strong>{issue.name}</strong><p>{issueMessages[issue.code]}</p>
      {['name-mismatch', 'missing-media', 'ambiguous-media'].includes(issue.code) && <MetadataOriginalPicker api={api} id={id} issue={issue} disabled={disabled || busy} />}
    </div>)}
    {!busy && !notice && issues.length === 0 && next === null && <p>No unresolved metadata issues.</p>}
    {next !== null && <button disabled={busy} onClick={() => setCursor(next)}>More issues</button>}
    <button disabled={busy} onClick={() => { setCursor(null); setReload(value => value + 1); }}>Refresh issues</button>
    {issues.some(issue => issue.retryable) && <button disabled={busy || disabled} onClick={retry}>Retry unavailable files</button>}
  </section>;
}

export function Imports({ api, back }: { api: DesktopApi; back: () => void }) {
  const [data, setData] = useState<PhotoImports | null>(null);
  const [sources, setSources] = useState<Source[]>([]);
  const [destination, setDestination] = useState('');
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  useEffect(() => {
    const stop = new AbortController(); let timer: ReturnType<typeof setTimeout> | undefined;
    async function refresh() {
      try {
        const [imports, sourceList] = await Promise.all([api.imports(stop.signal), api.sources(stop.signal)]);
        if (!stop.signal.aborted) { setData(imports); setSources(sourceList.sources); }
      } catch (error) { if (!stop.signal.aborted) setNotice(error instanceof Error ? error.message : 'Import progress could not refresh.'); }
      finally { if (!stop.signal.aborted) timer = setTimeout(() => { void refresh(); }, 2500); }
    }
    void refresh(); return () => { stop.abort(); if (timer !== undefined) clearTimeout(timer); };
  }, [api]);
  async function act(action: 'folders' | 'start' | 'retry', id: string) {
    setBusy(true); setNotice(action === 'folders' ? 'Choose a folder in the dialog on this computer.' : null);
    try { await api.importAction(action, id); setData(await api.imports(new AbortController().signal)); setNotice(null); }
    catch (error) { setNotice(error instanceof Error ? error.message : 'Import could not continue. Saved progress was retained.'); }
    finally { setBusy(false); }
  }
  const labels = { reviewing: 'Reviewing selected files', ready: 'Ready to import', copying: 'Copying and verifying originals', 'originals-verified': 'Originals verified', 'needs-attention': 'Needs attention', paused: 'Paused', canceled: 'Canceled' };
  const enabled = sources.filter(source => source.enabled && source.accountConnected);
  return <section className="settings"><button className="back" onClick={back}>‹ Back to Photos</button><h1>Import photos</h1>
    <p>Copy photos from a local photo folder or an extracted Takeout folder to Google Drive. Your selected files stay unchanged.</p>
    {notice && <p role="alert">{notice}</p>}
    {data?.paused && <p role="status">Processing is paused. Saved imports will continue when you resume processing in Settings.</p>}
    <section className="setting-card"><h2>Choose a destination</h2><label>Drive folder <select value={destination} onChange={event => setDestination(event.target.value)} disabled={busy}>
      <option value="">Choose an enabled Source…</option>{enabled.map(source => <option key={source.id} value={source.id}>{source.displayName}</option>)}</select></label>
      {!enabled.length && <p>Add and enable a Drive folder in Sources first.</p>}
      {data?.canChooseFolder ? <button disabled={busy || !enabled.some(source => source.id === destination)} onClick={() => { void act('folders', destination); }}>Choose folder on this computer</button> : data && <p>Open the desktop application on this computer to choose a local folder.</p>}
      <p>You can review the photo count before starting the copy.</p>
    </section>
    {!data && <p role="status">Loading saved imports…</p>}
    {data?.imports.map(item => {
      const target = sources.find(source => source.accountId === item.target.accountId && source.folderId === item.target.folderId);
      return <section className="setting-card" key={item.id}><h2>{item.inputName}</h2><p>{labels[item.state]}</p>
        <p>Destination: {target?.displayName ?? 'Captured Drive folder'}</p>
        {item.media !== null && <p>{BigInt(item.verified).toLocaleString()} of {BigInt(item.media).toLocaleString()} photo and video originals verified</p>}
        {item.metadata && <div role="status"><p>{item.metadata.finished ? 'Imported metadata processed' : 'Processing imported metadata'}</p>
          <p>{BigInt(item.metadata.ready).toLocaleString()} of {BigInt(item.metadata.total).toLocaleString()} sidecars processed</p>
          {item.metadata.issues !== '0' && <p>{BigInt(item.metadata.issues).toLocaleString()} sidecars could not be applied or matched. Verified originals are retained.</p>}
          {item.metadata.issues !== '0' && <MetadataIssues api={api} id={item.id} disabled={busy || data.paused} retry={() => { void act('retry', item.id); }} />}
          {item.metadata.waiting !== '0' && <p>{BigInt(item.metadata.waiting).toLocaleString()} sidecars are waiting for their original photos to verify.</p>}
        </div>}
        {item.state === 'ready' && <button disabled={busy || item.media === '0'} onClick={() => {
          if (window.confirm(`Import all ${item.media ?? ''} photo and video originals from ${item.inputName} into ${target?.displayName ?? 'the captured Drive folder'}? The source files will stay unchanged.`)) void act('start', item.id);
        }}>Import all originals</button>}
        {item.state === 'needs-attention' && <><p>{item.issue === 'metadata-capacity' ? 'Metadata storage is full. Saved originals and unfinished metadata are retained.' : item.issue === 'drive-unavailable' ? 'Google Drive is unavailable. Reconnect or wait for access to return.' : item.issue === 'input-unavailable' ? 'The selected input is unavailable. Restore access to the same folder.' : 'The captured destination or account permission changed. Review Sources and the account connection.'}</p><button disabled={busy} onClick={() => { void act('retry', item.id); }}>Retry unfinished work</button></>}
      </section>;
    })}
  </section>;
}
