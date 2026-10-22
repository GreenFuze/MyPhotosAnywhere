import { useEffect, useRef, useState } from 'react';
import type { Account, DesktopApi, Folder, LibraryController, WorkspaceInspection } from './api';
import { FolderPicker } from './sources';

const message = (error: unknown): string => error instanceof Error ? error.message : 'MPA could not finish library setup.';

export function LibrarySettings({ api, accounts, back }: { api: DesktopApi; accounts: Account[]; back: () => void }) {
  const [controller, setController] = useState<LibraryController | null | undefined>(undefined);
  const [preview, setPreview] = useState<WorkspaceInspection | null>(null);
  const [picker, setPicker] = useState(false);
  const [shared, setShared] = useState(false);
  const [associated, setAssociated] = useState(false);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const inspection = useRef<AbortController | null>(null);
  useEffect(() => {
    const request = new AbortController();
    api.library(request.signal).then(result => { if (!request.signal.aborted) setController(result.controller); })
      .catch(error => { if (!request.signal.aborted) setNotice(message(error)); });
    return () => { request.abort(); inspection.current?.abort(); };
  }, [api]);
  async function inspect(account: string, folder: Folder) {
    inspection.current?.abort(); const request = new AbortController(); inspection.current = request;
    const result = await api.inspectWorkspace(account, folder.id, request.signal);
    if (request.signal.aborted) return;
    if (result.controller) setController(result.controller);
    else { setPreview(result); setShared(false); setAssociated(false); }
    setNotice(null); setPicker(false);
  }
  async function approve() {
    if (!preview || busy) return; setBusy(true); setNotice(null);
    try {
      const result = await api.approveWorkspace({ accountId: preview.workspace.accountId, folderId: preview.workspace.folderId,
        accountRevision: preview.accountRevision, folderVersion: preview.workspace.driveVersion, configurationRevision: preview.sources.revision,
        controllerKeyId: preview.identity.controllerKeyId, acceptSharedWorkspace: shared, associateSelectedSources: associated });
      setController(result.controller); setPreview(null);
    } catch (error) { setNotice(message(error)); } finally { setBusy(false); }
  }
  return <section className="settings"><button className="back" onClick={back} disabled={busy}>← Settings</button><h1>Library workspace</h1>
    <p className="muted">Choose a separate Google Drive folder for MPA’s metadata and generated previews. Your source photos keep their names and locations.</p>
    {notice && <p role="alert" className="inline-message">{notice}</p>}
    {controller === undefined && !notice && <p role="status">Loading library setup…</p>}
    {controller && <section className="setting-card"><h2>Workspace approved</h2>
      <p>{accounts.find(account => account.id === controller.accountId)?.name ?? 'Google Drive account'}</p>
      <p>Library setup is saved on this computer. The first shared catalog is pending.</p></section>}
    {controller === null && <>
      <button disabled={busy || !accounts.some(account => account.connected)} onClick={() => setPicker(true)}>{preview ? 'Choose another folder' : 'Choose a folder'}</button>
      {!accounts.some(account => account.connected) && <p>Connect Google Drive in Settings first.</p>}
      {preview && <section className="setting-card"><h2 dir="auto">{preview.workspace.name}</h2>
        <p>{accounts.find(account => account.id === preview.workspace.accountId)?.name ?? 'Google Drive account'}</p>
        {!preview.workspace.verifiedEmpty && <p role="status">This folder has existing contents or its listing is incomplete. Choose an empty folder to create a new library. This folder’s existing contents will be kept.</p>}
        <p>Metadata and previews for {preview.sources.sources.length} selected source {preview.sources.sources.length === 1 ? 'folder' : 'folders'} will be stored here. Anyone with access to this workspace may be able to read them.</p>
        {preview.sources.sources.length > 0 && <ul>{preview.sources.sources.map(source => <li key={source.id} dir="auto">{source.displayName} · {accounts.find(account => account.id === source.accountId)?.name ?? 'Google Drive account'}</li>)}</ul>}
        <label className="switch-row"><span>Use this workspace for the selected source folders</span><input type="checkbox" checked={associated} disabled={busy} onChange={event => setAssociated(event.target.checked)} /></label>
        {preview.workspace.shared && <label className="switch-row"><span>I approve storing private metadata and previews in this shared folder</span><input type="checkbox" checked={shared} disabled={busy} onChange={event => setShared(event.target.checked)} /></label>}
        <button className="primary" disabled={busy || !preview.workspace.verifiedEmpty || !associated || preview.workspace.shared && !shared} onClick={() => { void approve(); }}>{busy ? 'Saving library setup…' : 'Approve workspace'}</button>
      </section>}
    </>}
    {picker && <FolderPicker mode="workspace" api={api} accounts={accounts.filter(account => account.connected)} close={() => setPicker(false)} picked={inspect} />}
  </section>;
}
