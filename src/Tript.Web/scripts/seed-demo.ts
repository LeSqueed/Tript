// SPDX-License-Identifier: GPL-2.0-or-later

import { spawnSync } from 'node:child_process';
import { mkdirSync, rmSync, utimesSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

const GAME = 'Overwatch';
const GAME_ID = '57ZZVAZ0PJK8VQGPKB728QE57C';
const FONT = process.platform === 'win32' ? 'C\\:/Windows/Fonts/segoeuib.ttf' : 'DejaVuSans-Bold.ttf';

type BookmarkSeed = { type: 'Kill' | 'Assist' | 'Death' | 'Goal' | 'Manual'; at: number; subtype?: string };

type SessionSeed = {
  file: string;
  title: string;
  start: string;
  seconds: number;
  colors: [string, string];
  bookmarks: BookmarkSeed[];
  highlights: { start: number; end: number; favorite?: boolean }[];
  clips: { file: string; title: string; start: number; end: number; favorite?: boolean }[];
};

const sessions: SessionSeed[] = [
  {
    file: 'session-20260921-201512000.mp4',
    title: 'Ranked with the squad',
    start: '2026-09-21T20:15:12Z',
    seconds: 150,
    colors: ['0x1d2b53', '0x7e2553'],
    bookmarks: [
      { type: 'Kill', at: 18 },
      { type: 'Kill', at: 21 },
      { type: 'Assist', at: 44 },
      { type: 'Death', at: 61 },
      { type: 'Manual', at: 75 },
      { type: 'Kill', at: 97 },
      { type: 'Assist', at: 99 },
      { type: 'Kill', at: 128 },
    ],
    highlights: [
      { start: 13, end: 29 },
      { start: 39, end: 52 },
      { start: 92, end: 107, favorite: true },
      { start: 123, end: 136 },
    ],
    clips: [{ file: 'clip-20260921-203000000.mp4', title: 'Double kill on point', start: 15, end: 27, favorite: true }],
  },
  {
    file: 'session-20260920-183000000.mp4',
    title: 'Quick play warmup',
    start: '2026-09-20T18:30:00Z',
    seconds: 120,
    colors: ['0x0f3b3a', '0x1f6f8b'],
    bookmarks: [
      { type: 'Kill', at: 30 },
      { type: 'Death', at: 55 },
      { type: 'Kill', at: 88 },
    ],
    highlights: [
      { start: 25, end: 38 },
      { start: 83, end: 96 },
    ],
    clips: [],
  },
  {
    file: 'session-20260918-212000000.mp4',
    title: 'Late night arcade',
    start: '2026-09-18T21:20:00Z',
    seconds: 90,
    colors: ['0x3b1f0f', '0x8b5a1f'],
    bookmarks: [{ type: 'Assist', at: 40 }],
    highlights: [],
    clips: [{ file: 'clip-20260918-214000000.mp4', title: 'Clutch save', start: 35, end: 50 }],
  },
];

const bufferOnly = {
  file: 'session-20260919-190000000.mp4',
  start: '2026-09-19T19:00:00Z',
  colors: ['0x2b1d53', '0x25537e'] as [string, string],
  highlights: [
    { start: 10, end: 23 },
    { start: 60, end: 73 },
  ],
};

function video(path: string, seconds: number, colors: [string, string], label: string, at: string): void {
  const text = label.replace(/[:']/g, ' ');
  const filter =
    `gradients=s=1280x720:c0=${colors[0]}:c1=${colors[1]}:speed=0.01:d=${seconds},` +
    `drawtext=fontfile='${FONT}':text='${text}':fontcolor=white@0.85:fontsize=54:x=(w-tw)/2:y=(h-th)/2-40,` +
    `drawtext=fontfile='${FONT}':text='%{pts\\:hms}':fontcolor=white@0.6:fontsize=40:x=(w-tw)/2:y=(h/2)+30`;
  const result = spawnSync(
    'ffmpeg',
    [
      '-hide_banner', '-loglevel', 'error', '-y',
      '-f', 'lavfi', '-i', filter,
      '-f', 'lavfi', '-i', `sine=frequency=220:duration=${seconds}`,
      '-r', '30', '-c:v', 'libx264', '-preset', 'ultrafast', '-pix_fmt', 'yuv420p',
      '-c:a', 'aac', '-shortest', path,
    ],
    { stdio: 'inherit' },
  );
  if (result.status !== 0) {
    throw new Error(`ffmpeg failed for ${path}`);
  }
  const when = new Date(at);
  utimesSync(path, when, when);
}

function json(path: string, value: unknown): void {
  writeFileSync(path, JSON.stringify(value, null, 2));
}

function timeSpan(seconds: number): string {
  const h = Math.floor(seconds / 3600);
  const m = Math.floor((seconds % 3600) / 60);
  const s = seconds % 60;
  return [h, m, s].map((part) => String(part).padStart(2, '0')).join(':');
}

function highlightName(session: string, index: number): string {
  return `${session.replace(/\.mp4$/, '')}-highlight-${index + 1}-${(index + 1).toString(16).padStart(8, 'a')}.mp4`;
}

export function seedDemo(root: string): void {
  rmSync(root, { recursive: true, force: true });
  const game = join(root, GAME);
  const metadata = join(root, 'metadata');
  for (const dir of ['sessions', 'clips', 'highlights']) {
    mkdirSync(join(game, dir), { recursive: true });
  }
  mkdirSync(metadata, { recursive: true });

  for (const session of sessions) {
    const wire = `${GAME}/sessions/${session.file}`;
    video(join(game, 'sessions', session.file), session.seconds, session.colors, session.title, session.start);
    json(join(metadata, `${session.file}.metadata.json`), {
      videoPath: wire,
      game: GAME,
      gameId: GAME_ID,
      contentType: 'Recording',
      startTime: session.start,
      title: session.title,
      favorite: false,
      durationSeconds: session.seconds,
      audioTracks: [],
      bookmarks: session.bookmarks.map((bookmark) => ({
        id: crypto.randomUUID(),
        type: bookmark.type,
        subtype: bookmark.subtype ?? null,
        time: timeSpan(bookmark.at),
        isAutomaticClipCandidate: bookmark.type === 'Manual' ? true : null,
      })),
      compressed: false,
    });

    session.highlights.forEach((highlight, index) => {
      const name = highlightName(session.file, index);
      const seconds = highlight.end - highlight.start;
      video(join(game, 'highlights', name), seconds, session.colors, `${session.title} highlight ${index + 1}`, session.start);
      json(join(metadata, `${name}.title.json`), {
        title: '',
        favorite: highlight.favorite ?? false,
        durationSeconds: seconds,
        isAutomatic: true,
        sourceSessionPath: wire,
        game: GAME,
        gameId: GAME_ID,
        clipStartTime: highlight.start,
        clipEndTime: highlight.end,
      });
    });

    for (const clip of session.clips) {
      const seconds = clip.end - clip.start;
      video(join(game, 'clips', clip.file), seconds, session.colors, clip.title, session.start);
      json(join(metadata, `${clip.file}.title.json`), {
        title: clip.title,
        favorite: clip.favorite ?? false,
        durationSeconds: seconds,
        isAutomatic: false,
        sourceSessionPath: wire,
        game: GAME,
        gameId: GAME_ID,
        sourceSpans: [{ start: clip.start, end: clip.end }],
      });
    }
  }

  const bufferWire = `${GAME}/sessions/${bufferOnly.file}`;
  bufferOnly.highlights.forEach((highlight, index) => {
    const name = highlightName(bufferOnly.file, index);
    const seconds = highlight.end - highlight.start;
    video(join(game, 'highlights', name), seconds, bufferOnly.colors, `Replay buffer highlight ${index + 1}`, bufferOnly.start);
    json(join(metadata, `${name}.title.json`), {
      title: '',
      favorite: false,
      durationSeconds: seconds,
      isAutomatic: true,
      sourceSessionPath: bufferWire,
      sourceSessionHighlightsOnly: true,
      game: GAME,
      gameId: GAME_ID,
      clipStartTime: highlight.start,
      clipEndTime: highlight.end,
    });
  });
}

if (process.argv[1]?.endsWith('seed-demo.ts')) {
  const target = process.argv[2];
  if (!target) {
    throw new Error('usage: seed-demo.ts <content-root>');
  }
  seedDemo(target);
  console.log(`seeded ${target}`);
}
