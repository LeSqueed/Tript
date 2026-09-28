// SPDX-License-Identifier: GPL-2.0-or-later

import { useEffect, useRef, useState } from 'react';
import type { TrainingPublishResultMessage } from '../../ipc/protocol';
import type { IpcClient } from '../../ipc/websocketClient';
import { Button, Field, TextField } from '../ui/controls';

const VERSION_PATTERN = /^\d+\.\d+\.\d+$/;

export function TrainingPublishForm({ client, gameId, hasModel }: {
  client: IpcClient;
  gameId: string;
  hasModel: boolean;
}) {
  const [username, setUsername] = useState('admin');
  const [password, setPassword] = useState('');
  const [minimumVersion, setMinimumVersion] = useState('');
  const [requestId, setRequestId] = useState<string | null>(null);
  const [result, setResult] = useState<string | null>(null);
  const requestRef = useRef<string | null>(null);

  useEffect(() => client.on('trainingPublishResult', (content) => {
    const message = content as Partial<TrainingPublishResultMessage>;
    if (message.requestId !== requestRef.current) return;
    requestRef.current = null;
    setRequestId(null);
    setPassword('');
    setResult(message.success
      ? `Published revision ${message.revision}.`
      : message.error || 'The trained model could not be published.');
  }), [client]);

  const publish = () => {
    if (!gameId || !username.trim() || !password) return;
    const next = crypto.randomUUID();
    requestRef.current = next;
    setRequestId(next);
    setResult(null);
    client.send('PublishTrainingModel', {
      requestId: next,
      gameId,
      username: username.trim(),
      password,
      ...(minimumVersion.trim() ? { minimumAppVersion: minimumVersion.trim() } : {}),
    });
  };

  const minimumVersionValid = minimumVersion.trim() === '' || VERSION_PATTERN.test(minimumVersion.trim());
  const canPublish = hasModel && Boolean(username.trim()) && Boolean(password) && minimumVersionValid
    && requestId === null;
  const publishOnEnter = () => {
    if (canPublish) publish();
  };

  return (
    <section className="training-panel training-publish">
      <div>
        <h2>Publish trained model</h2>
        <p className="muted small">Credentials are used for this upload only and are not saved.</p>
      </div>
      <div className="training-publish-fields">
        <Field label="Admin username">
          <TextField value={username} onChange={setUsername} onEnter={publishOnEnter} autoComplete="username" />
        </Field>
        <Field label="Admin password">
          <TextField
            type="password"
            value={password}
            onChange={setPassword}
            onEnter={publishOnEnter}
            autoComplete="current-password"
          />
        </Field>
        <Field label="Minimum app version">
          <TextField
            value={minimumVersion}
            onChange={setMinimumVersion}
            onEnter={publishOnEnter}
            placeholder="Optional"
            aria-invalid={!minimumVersionValid}
            aria-describedby="training-publish-version-hint"
          />
        </Field>
        <Button onClick={publish} disabled={!canPublish}>
          {requestId ? 'Publishing…' : 'Publish model'}
        </Button>
      </div>
      <p
        id="training-publish-version-hint"
        className={minimumVersionValid ? 'muted small' : 'training-label-warning small'}
        role={minimumVersionValid ? undefined : 'alert'}
      >
        {minimumVersionValid
          ? 'Set a minimum app version, such as 1.2.1, when older Tript versions cannot use this model. Leave it empty if every version can.'
          : 'Use the form 1.2.1.'}
      </p>
      {result && <p className="training-progress" role="status">{result}</p>}
    </section>
  );
}
