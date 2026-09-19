// SPDX-License-Identifier: GPL-2.0-or-later

import { useEffect, useState } from 'react';
import type { IpcClient } from '../ipc/websocketClient';
import type { StreamerShareState, StreamerStatusMessage } from '../ipc/protocol';
import { useIpcMessage, useSendOnConnect } from '../app/useConnection';
import { useSettings } from '../settings/useSettings';
import type { StreamShareWhen } from '../settings/settingsModel';
import { Field, SegmentedControl, TextField, Toggle } from './ui/controls';
import { StatusDot } from './ui/Ui';

const SENDER_NAME_PATTERN = /^[\x20-\x7e]{1,255}$/;

const SPOUT_PLUGIN_URL = 'https://github.com/Off-World-Live/obs-spout2-plugin';

const SHARE_WHEN_SEGMENTS: { value: StreamShareWhen; label: string }[] = [
  { value: 'WhileObsRuns', label: 'Only while OBS is open' },
  { value: 'Always', label: 'Always' },
];

type Tone = 'success' | 'warning' | 'error' | 'neutral';

const STATE_TEXT: Record<StreamerShareState, { tone: Tone; title: string; detail: string }> = {
  off: {
    tone: 'neutral',
    title: 'Sharing is off',
    detail: 'Tript records games on its own. Turn sharing on to send the game picture to OBS.',
  },
  unsupported: {
    tone: 'error',
    title: 'Sharing is not available',
    detail: 'Sharing only works on Windows.',
  },
  waitingForObs: {
    tone: 'neutral',
    title: 'Waiting for OBS',
    detail: 'Tript starts sharing as soon as OBS opens.',
  },
  waitingForCapture: {
    tone: 'warning',
    title: 'Waiting for a game',
    detail: 'Tript sends the picture to OBS while it is recording a game.',
  },
  live: {
    tone: 'success',
    title: 'Sharing with OBS',
    detail: 'Add a Spout2 Capture source in OBS to show this picture.',
  },
  sharingOnly: {
    tone: 'warning',
    title: 'Sharing only, not recording',
    detail: 'Your stream keeps the picture. Tript cannot record because the drive is out of space.',
  },
  failed: {
    tone: 'error',
    title: 'Sharing stopped',
    detail: "Your graphics card didn't allow the picture to be shared. Tript's log file has the details.",
  },
};

const SHARE_STATES = new Set<string>(Object.keys(STATE_TEXT));

export function readStreamerStatus(content: unknown): StreamerStatusMessage | null {
  if (!content || typeof content !== 'object') {
    return null;
  }
  const value = content as Partial<StreamerStatusMessage>;
  if (typeof value.state !== 'string' || !SHARE_STATES.has(value.state)) {
    return null;
  }
  return {
    state: value.state,
    shareEnabled: value.shareEnabled === true,
    obsRunning: value.obsRunning === true,
    obsVersion: typeof value.obsVersion === 'string' ? value.obsVersion : undefined,
    senderName: typeof value.senderName === 'string' ? value.senderName : '',
    width: typeof value.width === 'number' ? value.width : 0,
    height: typeof value.height === 'number' ? value.height : 0,
    adapterName: typeof value.adapterName === 'string' ? value.adapterName : undefined,
    hookConflictSuspected: value.hookConflictSuspected === true,
    recordingBlocked: value.recordingBlocked === true,
    blockedReason: typeof value.blockedReason === 'string' ? value.blockedReason : undefined,
  };
}

export function StreamerView({ client }: { client: IpcClient }) {
  const { settings, update, hasSettings, externalPushCount } = useSettings(client);
  const streaming = settings.streaming;
  const [status, setStatus] = useState<StreamerStatusMessage | null>(null);
  const [senderName, setSenderName] = useState(streaming.senderName);

  useSendOnConnect(client, 'GetStreamerStatus');
  useIpcMessage(client, 'streamerStatus', (content) => {
    const next = readStreamerStatus(content);
    if (next) setStatus(next);
  });

  useEffect(() => {
    setSenderName(streaming.senderName);
  }, [externalPushCount, hasSettings]);

  const senderNameValid = SENDER_NAME_PATTERN.test(senderName);

  function commitSenderName() {
    if (senderNameValid && senderName !== streaming.senderName) {
      update('streaming', { senderName });
    } else if (!senderNameValid) {
      setSenderName(streaming.senderName);
    }
  }

  const state = status?.state ?? (streaming.shareEnabled ? 'waitingForObs' : 'off');
  const text = STATE_TEXT[state];
  const shownName = status?.senderName || streaming.senderName;

  return (
    <section className="streamer-view" data-testid="streamer-view">
      {status?.hookConflictSuspected && state !== 'live' && state !== 'sharingOnly' && (
        <div className="streamer-alert" role="status" data-testid="streamer-conflict">
          OBS is open and may be recording the same game as Tript. This can make one of them show a
          black screen. Turn on sharing below and use the Tript source in OBS instead of Game Capture.
        </div>
      )}

      <div className="streamer-block">
        <Toggle
          label="Share the game picture with OBS"
          checked={streaming.shareEnabled}
          disabled={!hasSettings}
          onChange={(checked) => update('streaming', { shareEnabled: checked })}
        />
        {streaming.shareEnabled && (
          <Field
            label="When to share"
            hint="Only while OBS is open saves your computer some work when you are not streaming."
          >
            <SegmentedControl
              label="When to share"
              value={streaming.shareWhen}
              segments={SHARE_WHEN_SEGMENTS}
              onChange={(value) => update('streaming', { shareWhen: value })}
            />
          </Field>
        )}
      </div>

      <div className="streamer-status" data-testid="streamer-status">
        <div className="streamer-status-head">
          <StatusDot tone={text.tone} />
          <span>{text.title}</span>
        </div>
        <p className="muted small">
          {(state === 'live' || state === 'sharingOnly') && status
            ? `${status.width} x ${status.height}. ${text.detail}`
            : text.detail}
        </p>
        {status?.blockedReason && <p className="muted small">{status.blockedReason}</p>}
        <dl className="streamer-facts">
          <dt>OBS</dt>
          <dd>
            {status?.obsRunning
              ? `Open${status.obsVersion ? `, version ${status.obsVersion}` : ''}`
              : 'Not open'}
          </dd>
          <dt>Source name in OBS</dt>
          <dd>{shownName}</dd>
          {status?.adapterName && (
            <>
              <dt>Graphics card</dt>
              <dd>{status.adapterName}</dd>
            </>
          )}
        </dl>
      </div>

      <div className="streamer-block">
        <h2 className="subheading">Why OBS and Tript can clash</h2>
        <p>
          OBS and Tript use the same method to capture a game, and a game only works with one of them
          at a time. Leading to one of the applications receiving a black screen when they are competing.
          With sharing on, Tript hooks into the game and OBS gets the picture from Tript making sure they
          don't clash. Make sure to disable Game Capture in OBS.
        </p>
      </div>

      <div className="streamer-block">
        <h2 className="subheading">Set up OBS once</h2>
        <ol className="streamer-steps">
          <li>
            Install the{' '}
            <button
              type="button"
              className="streamer-link"
              onClick={() => client.send('OpenInBrowser', { url: SPOUT_PLUGIN_URL })}
            >
              Spout2 Plugin
            </button>{' '}
            for OBS and restart OBS.
          </li>
          <li>
            Add a Spout2 Capture source and pick <strong>{shownName}</strong> as the sender.
          </li>
          <li>
            Remove or disable the Game Capture source in OBS.
          </li>
          <li>If your computer has two graphics cards, make sure OBS and Tript use the same one.</li>
        </ol>
      </div>

      <div className="streamer-block">
        <h2 className="subheading">Good to know</h2>
        <ul className="streamer-steps">
          <li>
            The Tript source in OBS stays empty while Tript is closed or not recording a game. Your
            stream keeps running.
          </li>
          <li>You can open OBS before or after Tript. Sharing starts on its own.</li>
          <li>
            If you stop a recording yourself while the game is still open, OBS gets no picture until Tript records again.
          </li>
        </ul>
      </div>

      <div className="streamer-block">
        <h2 className="subheading">Performance</h2>
        <p>
          Sharing barely affects performance. The picture in OBS can be slightly delayed, for example
          about 17 ms at 60 FPS, which viewers will not notice. Recording in Tript while you stream
          does take some extra work from your computer. If your stream starts to stutter, lower the
          quality in Tript or OBS. 
        </p>
      </div>

      <details className="streamer-advanced">
        <summary>Advanced</summary>
        <Field
          label="Sender name"
          hint="The name Tript shows up as in OBS. Use only letters, numbers, spaces and basic symbols."
        >
          <TextField
            value={senderName}
            aria-label="Sender name"
            aria-invalid={!senderNameValid}
            onChange={setSenderName}
            onBlur={commitSenderName}
            onKeyDown={(event) => {
              if (event.key === 'Enter') {
                commitSenderName();
              }
            }}
          />
        </Field>
      </details>
    </section>
  );
}
