import { useEffect, useRef, useState } from 'react';
import { AuthenticationRequired, type LibraryCatalogPage, type CatalogPhoto, type DesktopApi, type LibraryViewerWindow } from './api';

type Query = { account: string | null; cursor: string | null; newer: boolean; date: number | null };
type GalleryPage = { query: Query; page: LibraryCatalogPage };
const failureMessage = (error: unknown) => error instanceof Error ? error.message : 'Photos could not load. The current photos are retained.';
function ViewerIcon({ name }: { name: 'close' | 'previous' | 'next' }) {
  return <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d={name === 'close' ? 'M6 6l12 12 M6 18L18 6' : name === 'previous' ? 'M15 5l-7 7 7 7' : 'M9 5l7 7-7 7'} /></svg>;
}

function dateLabel(photo: CatalogPhoto | undefined): string {
  return photo?.effectiveUtc === null || !photo ? 'Unknown date' : new Intl.DateTimeFormat(undefined, { year: 'numeric', month: 'long' }).format(new Date(photo.effectiveUtc));
}
function PhotoTile({ api, photo, stage, account, open }: { api: DesktopApi; photo: CatalogPhoto; stage: string; account: string | null; open: () => void }) {
  const [failed, setFailed] = useState(false);
  const ratio = photo.width && photo.height ? Math.max(.55, Math.min(2.1, photo.width / photo.height)) : 1;
  return <button className="photo" style={{ flexGrow: ratio, flexBasis: `${ratio * 190}px` }} onClick={open} aria-label={`Open ${photo.name}`}>
    {failed ? <span className="preview-unavailable">Preview unavailable</span> : <img src={api.libraryPreview(stage, photo.id, account)} alt="" loading="lazy" decoding="async" onError={() => setFailed(true)} />}
    {photo.mimeType.startsWith('video/') && <span className="video-label">Video</span>}
  </button>;
}

function CatalogViewer({ api, photos, selected, account, stage, close, report }: { api: DesktopApi; photos: readonly CatalogPhoto[]; selected: number; account: string | null; stage: string; close: () => void; report: (error: unknown) => void }) {
  const initial = photos[selected]; if (!initial) throw new Error('The selected gallery identity is missing.');
  const [sequence, setSequence] = useState<LibraryViewerWindow | null>(null), [position, setPosition] = useState(0);
  const [details, setDetails] = useState(false), [failed, setFailed] = useState(false), [failure, setFailure] = useState<string | null>(null), [pending, setPending] = useState(false);
  const sessionId = useRef(crypto.randomUUID()), dialog = useRef<HTMLDialogElement>(null), navigation = useRef<AbortController | null>(null);
  const reportCurrent = useRef(report); reportCurrent.current = report;
  const entry = sequence?.entries.find(item => item.position === position);
  const photo = sequence ? entry?.photo ?? null : initial;
  useEffect(() => { const current = dialog.current; current?.showModal(); return () => current?.close(); }, []);
  useEffect(() => {
    const controller = new AbortController(); const id = sessionId.current;
    api.openLibraryViewer(id, initial.id, account, stage, controller.signal).then(sequence => {
      if (sequence.id !== id || sequence.entries.find(item => item.position === sequence.position)?.assetId !== initial.id) throw new Error('MPA returned a different selected identity. Photo navigation stopped.');
      if (!controller.signal.aborted) { setSequence(sequence); setPosition(sequence.position); }
    }).catch(error => { if (!controller.signal.aborted) setFailure(failureMessage(error)); });
    return () => { controller.abort(); navigation.current?.abort(); void api.closeViewer(id).catch(error => reportCurrent.current(error)); };
  }, [api, initial.id, account, stage]);
  async function move(target: number) {
    if (!sequence || target < 0 || target >= sequence.count || navigation.current) return;
    if (sequence.entries.some(item => item.position === target)) { setPosition(target); setFailure(null); return; }
    const controller = new AbortController(); navigation.current = controller; setPending(true); setFailure(null);
    try {
      const next = await api.libraryViewer(sessionId.current, target, stage, account, controller.signal);
      if (next.id !== sessionId.current || next.position !== target || next.count !== sequence.count) throw new Error('The frozen photo sequence changed unexpectedly. Navigation stopped.');
      if (!controller.signal.aborted) { setSequence(next); setPosition(target); }
    } catch (error) { if (!controller.signal.aborted) setFailure(failureMessage(error)); }
    finally { if (navigation.current === controller) { navigation.current = null; setPending(false); } }
  }
  useEffect(() => {
    function key(event: KeyboardEvent) {
      if (event.key === 'ArrowLeft') { event.preventDefault(); void move(position - 1); }
      if (event.key === 'ArrowRight') { event.preventDefault(); void move(position + 1); }
    }
    window.addEventListener('keydown', key); return () => window.removeEventListener('keydown', key);
  }, [sequence, position]);
  useEffect(() => setFailed(false), [photo?.id]);
  return <dialog ref={dialog} className="viewer" aria-label="Photo viewer" onCancel={close}>
    <header><button className="icon" onClick={close} aria-label="Close photo"><ViewerIcon name="close" /></button><span dir="auto">{photo?.name ?? 'Photo unavailable'}</span><button disabled={!photo} onClick={() => setDetails(value => !value)} aria-expanded={details}>Details</button></header>
    {failure && <p className="viewer-message" role="alert">{failure}</p>}
    {!sequence && !failure && <p className="viewer-message" role="status">Preparing photo navigation…</p>}
    {pending && <p className="viewer-message" role="status">Loading nearby photos…</p>}
    <div className="viewer-media"><button className="icon previous" disabled={!sequence || position === 0 || pending} onClick={() => { void move(position - 1); }} aria-label="Previous photo"><ViewerIcon name="previous" /></button>
      {!photo || failed ? <p role="status">This preview is unavailable. Your place in the gallery is retained.</p> : <img key={photo.id} src={api.libraryPreview(stage, photo.id, account)} alt={photo.name} onError={() => setFailed(true)} />}
      <button className="icon next" disabled={!sequence || position === sequence.count - 1 || pending} onClick={() => { void move(position + 1); }} aria-label="Next photo"><ViewerIcon name="next" /></button></div>
    {details && photo && <aside className="details"><h2>Details</h2><dl><dt>File</dt><dd dir="auto">{photo.name}</dd><dt>Preview</dt><dd>Google Drive thumbnail · original has not been downloaded</dd>
      {photo.width && photo.height && <><dt>Dimensions</dt><dd>{photo.width} × {photo.height}</dd></>}
      <dt>{photo.dateProvenance === 'DriveCapture' ? 'Capture time reported by Drive' : photo.dateProvenance === 'DriveModification' ? 'Drive modification time' : 'Date'}</dt><dd>{photo.effectiveUtc === null ? 'Unknown date' : new Date(photo.effectiveUtc).toLocaleString()}</dd>
      {photo.rawCapture && photo.dateProvenance !== 'DriveCapture' && <><dt>Original capture text</dt><dd>{photo.rawCapture}</dd></>}
    </dl></aside>}
  </dialog>;
}

/** Retain one bounded page and an immutable opened preview sequence. Background scans
 * never replace the viewport; explicit navigation/date requests use repository keysets. */
export function CatalogGallery({ api, account, sources, locked }: { api: DesktopApi; account: string; sources: () => void; locked: () => void }) {
  const [query, setQuery] = useState<Query>({ account: account || null, cursor: null, newer: false, date: null });
  const [shown, setShown] = useState<GalleryPage | null>(null), [loading, setLoading] = useState(true), [failure, setFailure] = useState<string | null>(null);
  const [refresh, setRefresh] = useState(0), [date, setDate] = useState('');
  const [viewer, setViewer] = useState<{ photos: readonly CatalogPhoto[]; index: number; stage: string; account: string | null } | null>(null);
  const grid = useRef<HTMLDivElement>(null);
  useEffect(() => { setQuery({ account: account || null, cursor: null, newer: false, date: null }); setViewer(null); }, [account]);
  useEffect(() => {
    const controller = new AbortController(); setLoading(true); setFailure(null);
    api.libraryCatalog(query.account, query.cursor, query.newer, query.date, controller.signal).then(page => {
      if (!controller.signal.aborted) { setShown({ query, page }); setLoading(false); }
    }).catch(error => {
      if (!controller.signal.aborted) { setLoading(false); setFailure(failureMessage(error)); if (error instanceof AuthenticationRequired) locked(); }
    });
    return () => controller.abort();
  }, [api, query, refresh]);
  const matching = shown?.query.account === (account || null);
  const page = matching ? shown?.page : null;
  function navigate(cursor: string | null, newer: boolean) { setViewer(null); setQuery({ account: account || null, cursor, newer, date: null }); }
  function seek() {
    if (!date) return;
    const end = new Date(date + 'T00:00:00'); end.setDate(end.getDate() + 1);
    if (!Number.isFinite(end.getTime())) { setFailure('Choose a valid date.'); return; }
    setViewer(null); setQuery({ account: account || null, cursor: null, newer: false, date: end.getTime() - 1 });
  }
  return <section className="gallery" aria-label="Photos" aria-busy={loading}>
    <div className="gallery-tools"><span className="date-capsule" aria-label="Gallery month">{page?.photos.length ? dateLabel(page.photos[0]) : 'Photos'}</span>
      <details className="date-jump"><summary>Jump to date</summary><label>Date<input type="date" value={date} onChange={event => setDate(event.target.value)} /></label><button disabled={!date || loading} onClick={seek}>Go to date</button></details>
      <button disabled={loading} onClick={() => setRefresh(value => value + 1)}>Refresh</button></div>
    {failure && <p className="inline-message" role="alert">{failure}</p>}
    {loading && !page && <p className="loading" role="status">Loading photos…</p>}
    {page && !page.available && !loading && !failure && <div className="welcome"><h1>Your shared library is getting ready</h1><p>Finish library setup and choose your Drive folders. Photos appear after MPA prepares your shared library.</p><button onClick={sources}>Choose sources</button></div>}
    {page?.available && page.photos.length === 0 && !loading && !failure && <div className="welcome"><h1>{query.date !== null ? 'No photos on or before this date' : query.cursor ? 'No more photos here' : 'No photos here yet'}</h1>
      {!query.cursor && query.date === null && <><p>Choose your Drive folders in Sources. Photos appear after MPA prepares your shared library.</p><button onClick={sources}>Choose sources</button></>}</div>}
    <div className="photo-grid" ref={grid}>{page?.available && page.photos.map((photo, index) => <PhotoTile key={`${page.stageId}:${photo.id}`} api={api} photo={photo} stage={page.stageId} account={(shown?.query.account ?? null)} open={() => setViewer({ photos: page.photos, index, stage: page.stageId, account: (shown?.query.account ?? null) })} />)}</div>
    <div className="gallery-pagination" aria-label="Timeline navigation">
      {page?.newerCursor && <button disabled={loading} onClick={() => navigate(page.newerCursor, true)}>Newer photos</button>}
      {page?.olderCursor && <button disabled={loading} onClick={() => navigate(page.olderCursor, false)}>Older photos</button>}
      {query.cursor || query.date !== null ? <button disabled={loading} onClick={() => navigate(null, false)}>Latest photos</button> : null}
    </div>
    {loading && page && <p className="loading" role="status">Loading photos…</p>}
    {viewer && <CatalogViewer api={api} photos={viewer.photos} selected={viewer.index} account={viewer.account} stage={viewer.stage} close={() => setViewer(null)} report={error => { setFailure(failureMessage(error)); if (error instanceof AuthenticationRequired) locked(); }} />}
  </section>;
}
