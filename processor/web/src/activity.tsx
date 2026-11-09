import { useEffect, useRef, useState } from 'react';
import type { ActivityPage, DesktopApi, Snapshot, SourceJob, Sources } from './api';

function stateLabel(job: SourceJob): string {
  if (!job.current) return 'Source settings changed';
  if (job.summary.isCanceled) return 'Canceled';
  if (job.summary.paused) return 'Paused';
  if (job.summary.running) return 'Scanning';
  if (job.summary.queued) return 'Queued';
  if (job.summary.failed) return 'Needs attention';
  return 'Scan complete';
}

export function Activity({ api, snapshot, busy, pause, back }: { api: DesktopApi; snapshot: Snapshot; busy: boolean; pause: () => Promise<void>; back: () => void }) {
  const [page, setPage] = useState<ActivityPage | null>(null);
  const [sources, setSources] = useState<Sources | null>(null);
  const [cursor, setCursor] = useState<string | null>(null);
  const [history, setHistory] = useState<(string | null)[]>([]);
  const [notice, setNotice] = useState<string | null>(null);
  const [failure, setFailure] = useState<string | null>(null);
  const [action, setAction] = useState<string | null>(null);
  const [refresh, setRefresh] = useState(0);
  const [loading, setLoading] = useState(true);
  const pendingScans = useRef(new Map<string, string>());
  const errors = (error: unknown) => error instanceof Error ? error.message : 'Activity could not refresh. Existing progress is retained.';

  useEffect(() => {
    const controller = new AbortController(); let timer: ReturnType<typeof setTimeout> | undefined;
    setLoading(true);
    async function poll() {
      try {
        const [progress, configuration] = await Promise.all([api.activity(cursor, controller.signal), api.sources(controller.signal)]);
        if (controller.signal.aborted) return;
        setPage(progress); setSources(configuration); setFailure(null);
      } catch (error) { if (!controller.signal.aborted) setFailure(errors(error)); }
      finally {
        if (!controller.signal.aborted) { setLoading(false); timer = setTimeout(() => { void poll(); }, 2500); }
      }
    }
    void poll(); return () => { controller.abort(); if (timer !== undefined) clearTimeout(timer); };
  }, [api, cursor, refresh]);

  async function retry(job: SourceJob) {
    setAction(job.jobId); setNotice(null);
    try {
      const count = await api.retrySource(job);
      setNotice(count ? `${count} failed ${count === 1 ? 'step queued' : 'steps queued'} for retry. Completed work was retained.` : 'No retryable failed steps remain.');
      setRefresh(value => value + 1);
    } catch (error) { setNotice(errors(error)); }
    finally { setAction(null); }
  }
  async function rescan(job: SourceJob) {
    setAction(job.jobId); setNotice(null);
    const key = `${job.scope.sourceId}:${job.scope.sourceRevision}:${job.scope.accountRevision}`;
    const requestId = pendingScans.current.get(key) ?? crypto.randomUUID(); pendingScans.current.set(key, requestId);
    try {
      await api.scanSource(job.scope, requestId); pendingScans.current.delete(key);
      setNotice('Source scan queued. Your photo files were not changed.'); setCursor(null); setHistory([]); setRefresh(value => value + 1);
    } catch (error) { setNotice(errors(error)); }
    finally { setAction(null); }
  }
  const paused = snapshot.state === 'paused';
  const canControl = ['ready', 'running', 'paused'].includes(snapshot.state);
  return <section className="settings activity"><button className="back" onClick={back}>← Settings</button><h1>Activity</h1>
    <section className="setting-card"><h2>{paused ? 'Processing paused' : snapshot.state === 'faulted' ? 'Processing stopped' : 'Background processing'}</h2>
      <p>{paused ? 'Your progress is saved. Resume when you are ready.' : snapshot.state === 'faulted' ? 'An internal failure stopped processing. Check diagnostics on the library computer.' : 'You can keep browsing or close this browser while the engine works.'}</p>
      <div className="row-actions"><button disabled={busy || !canControl} onClick={() => { void pause(); }}>{paused ? 'Resume processing' : 'Pause processing'}</button>
        <button disabled={loading} onClick={() => { setCursor(null); setHistory([]); setRefresh(value => value + 1); }}>Refresh</button></div>
    </section>
    {snapshot.storageAttention && <section className="setting-card storage-attention" role="status"><h2>Waiting for free space</h2><p>Free space on the computer running this library. Scanning will check again automatically; your photos and saved progress are retained.</p></section>}
    {notice && <p className="inline-message" role="status">{notice}</p>}
    {failure && <p className="inline-message" role="alert">{failure}</p>}
    {!page && loading && <p role="status">Loading activity…</p>}
    {page?.jobs.length === 0 && <p>No source scans yet. Choose folders in Sources to begin.</p>}
    {page?.jobs.map(job => {
      const summary = job.summary;
      const total = summary.ready + summary.queued + summary.running + summary.failed + summary.unsupported + summary.canceled;
      const name = sources?.sources.find(source => source.id === job.scope.sourceId)?.displayName ?? 'Drive folder';
      return <section className="setting-card scan-card" key={job.jobId} aria-label={`Scan of ${name}`}>
        <div className="scan-heading"><h2 dir="auto">{name}</h2><span className="scan-status">{stateLabel(job)}</span></div>
        <p>{summary.ready} of {total} known steps complete{summary.failed ? ` · ${summary.failed} failed` : ''}</p>
        <progress max={Math.max(1, total)} value={summary.ready} aria-label={`Completed scan steps for ${name}`} />
        {job.problems.length > 0 && <ul className="scan-problems">{job.problems.map(problem => <li key={problem.itemId + ':' + problem.stage}>{problem.message}</li>)}</ul>}
        {job.moreProblems && <p className="muted">Additional failed steps are retained in this scan. Retry includes all eligible failures.</p>}
        {!job.current && <p className="muted">This scan kept its original source and account settings. The engine schedules changed settings separately.</p>}
        <div className="row-actions">
          {job.retryableFailures > 0 && <button disabled={action !== null || !job.current || summary.isCanceled || !canControl} onClick={() => { void retry(job); }}>Retry failed items</button>}
          {job.current && <button disabled={action !== null || !canControl || !!summary.running || !!summary.queued} onClick={() => { void rescan(job); }}>Scan source again</button>}
          {action === job.jobId && <span role="status">Saving…</span>}
        </div>
      </section>;
    })}
    <div className="row-actions">
      {history.length > 0 && <button disabled={loading} onClick={() => { setCursor(history[history.length - 1] ?? null); setHistory(values => values.slice(0, -1)); }}>Previous page</button>}
      {page?.nextCursor && <button disabled={loading} onClick={() => { setHistory(values => [...values, cursor].slice(-128)); setCursor(page.nextCursor); }}>Next page</button>}
    </div>
  </section>;
}
