// SPDX-License-Identifier: GPL-2.0-or-later

import { useEffect, useRef, useState, type ReactNode } from 'react';
import { thumbnailUrl } from '../../ipc/endpoints';

const RETRY_BASE_MS = 250;
const RETRY_MAX_MS = 5_000;
const RETRY_STAGGER_MS = 100;

export function ContentThumbnail({
  filePath,
  className,
  loading = 'lazy',
  fetchPriority = 'low',
  fallback = null,
  onLoad,
}: {
  filePath: string;
  className: string;
  loading?: 'eager' | 'lazy';
  fetchPriority?: 'high' | 'low';
  fallback?: ReactNode;
  onLoad?: () => void;
}) {
  const [attempt, setAttempt] = useState(0);
  const [waiting, setWaiting] = useState(false);
  const retryTimer = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(() => {
    setAttempt(0);
    setWaiting(false);
    return () => {
      if (retryTimer.current !== null) clearTimeout(retryTimer.current);
    };
  }, [filePath]);

  if (waiting) return fallback;

  const source = new URL(thumbnailUrl(filePath));
  if (attempt > 0) source.searchParams.set('thumbnailRetry', String(attempt));

  return (
    <img
      className={className}
      src={source.toString()}
      alt=""
      loading={loading}
      fetchPriority={fetchPriority}
      decoding="async"
      width={480}
      height={270}
      onLoad={onLoad}
      onError={() => {
        if (retryTimer.current !== null) return;

        setWaiting(true);
        const delay = Math.min(RETRY_BASE_MS * 2 ** attempt, RETRY_MAX_MS) + retryOffset(filePath);
        retryTimer.current = setTimeout(() => {
          retryTimer.current = null;
          setAttempt((current) => current + 1);
          setWaiting(false);
        }, delay);
      }}
    />
  );
}

function retryOffset(filePath: string): number {
  let hash = 0;
  for (let index = 0; index < filePath.length; index += 1) {
    hash = (hash * 31 + filePath.charCodeAt(index)) >>> 0;
  }
  return hash % RETRY_STAGGER_MS;
}
