// SPDX-License-Identifier: GPL-2.0-or-later

import { useEffect, useState } from 'react';
import type { IpcClient } from '../../ipc/websocketClient';
import { Button, Field, TextField } from '../ui/controls';
import { examplePaths, usePlatformCapabilities } from '../../app/platformCapabilities';

export function TrainingImportPanel({ client, importing, onImport }: {
  client: IpcClient;
  importing: boolean;
  onImport: (sourcePath: string) => void;
}) {
  const { platform } = usePlatformCapabilities();
  const [sourcePath, setSourcePath] = useState('');
  const [pickerStatus, setPickerStatus] = useState<'idle' | 'selected' | 'cancelled'>('idle');

  useEffect(() => {
    const removeFolder = client.on('trainingFolderSelected', (content) => {
      const path = (content as { path?: unknown }).path;
      if (typeof path === 'string') {
        setSourcePath(path);
        setPickerStatus('selected');
      }
    });
    const removeCancelled = client.on('trainingFolderCancelled', () => {
      setPickerStatus('cancelled');
    });
    return () => {
      removeFolder();
      removeCancelled();
    };
  }, [client]);

  const status = importing
    ? 'Importing workspace...'
    : pickerStatus === 'cancelled'
      ? 'Folder selection cancelled'
      : sourcePath.trim() ? 'Folder selected' : 'No folder selected';

  return (
    <section className="panel training-panel training-import-panel">
      <p className="training-eyebrow">Workspace</p>
      <h2>Import an existing workspace</h2>
      <p className="muted">Use this when you already have an event contract, model, and full-resolution labeled samples on disk.</p>
      <div className="training-import-guide">
        <strong>Expected contents</strong>
        <span className="muted small"><code>events.json</code> and paired files in <code>samples/</code> are required. <code>model.onnx</code> is optional; <code>dataset/</code> is ignored.</span>
      </div>
      <Field label="Training folder" hint="The selected folder is copied into this game's local workspace.">
        <div className="training-path-row">
          <TextField value={sourcePath} onChange={setSourcePath} placeholder={examplePaths(platform).trainingFolder} aria-label="Training folder" />
          <Button variant="ghost" onClick={() => client.send('BrowseTrainingFolder')}>Browse</Button>
        </div>
      </Field>
      <div className="training-import-footer">
        <span className="muted small">{status}</span>
        <Button onClick={() => onImport(sourcePath.trim())} disabled={!sourcePath.trim() || importing}>
          {importing ? 'Importing...' : 'Import workspace'}
        </Button>
      </div>
    </section>
  );
}
