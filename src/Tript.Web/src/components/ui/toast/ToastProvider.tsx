// SPDX-License-Identifier: GPL-2.0-or-later

import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
  type ReactNode,
} from 'react';
import {
  dismissItem,
  dismissKey,
  expireToast,
  pushToast,
  releaseToast,
  removeToast,
  type ToastItem,
  type ToastSpec,
} from './toastModel';
import { Toast } from './Toast';
import './toast.css';

const EXIT_MS = 180;
const RELEASE_GRACE_MS = 400;

export interface ToastApi {
  push: (spec: ToastSpec) => number;
  dismiss: (key: string) => void;
  dismissSelf: (id: number) => void;
  noteHover: (id: number, hovered: boolean) => void;
}

const ToastContext = createContext<ToastApi | null>(null);

export function useToast(): ToastApi {
  const context = useContext(ToastContext);
  if (context === null) {
    throw new Error('useToast must be used inside a ToastProvider');
  }
  return context;
}

export function ToastProvider({ children }: { children: ReactNode }) {
  const [items, setItems] = useState<ToastItem[]>([]);
  const idRef = useRef(0);
  const hoverRef = useRef(new Map<number, boolean>());
  const lifetimeTimers = useRef(new Map<number, { duration: number; handle: number }>());
  const exitTimers = useRef(new Map<number, number>());
  const graceTimers = useRef(new Map<number, number>());

  const itemsRef = useRef(items);
  itemsRef.current = items;

  const push = useCallback((spec: ToastSpec) => {
    const id = idRef.current + 1;
    idRef.current = id;
    hoverRef.current.set(id, false);
    setItems((current) => pushToast(current, spec, id));
    return id;
  }, []);

  const dismiss = useCallback((key: string) => {
    setItems((current) => dismissKey(current, key));
  }, []);

  const dismissSelf = useCallback((id: number) => {
    const item = itemsRef.current.find((toast) => toast.id === id);
    if (item === undefined) {
      return;
    }
    setItems((current) => dismissItem(current, id));
    item.onDismiss?.();
  }, []);

  const noteHover = useCallback((id: number, hovered: boolean) => {
    hoverRef.current.set(id, hovered);
    const grace = graceTimers.current.get(id);
    if (hovered) {
      if (grace !== undefined) {
        clearTimeout(grace);
        graceTimers.current.delete(id);
      }
      return;
    }
    const item = itemsRef.current.find((toast) => toast.id === id);
    if (item === undefined || item.state !== 'visible' || !item.held) {
      return;
    }
    graceTimers.current.set(
      id,
      window.setTimeout(() => {
        graceTimers.current.delete(id);
        setItems((current) => releaseToast(current, id));
      }, RELEASE_GRACE_MS),
    );
  }, []);

  useEffect(() => {
    for (const [id, entry] of lifetimeTimers.current) {
      const item = items.find((toast) => toast.id === id);
      if (item === undefined || item.state !== 'visible' || item.duration === 0
        || item.duration !== entry.duration) {
        clearTimeout(entry.handle);
        lifetimeTimers.current.delete(id);
      }
    }
    for (const item of items) {
      if (item.state === 'visible' && item.duration > 0 && !lifetimeTimers.current.has(item.id)) {
        const handle = window.setTimeout(() => {
          lifetimeTimers.current.delete(item.id);
          setItems((current) => expireToast(current, item.id, hoverRef.current.get(item.id) === true));
        }, item.duration);
        lifetimeTimers.current.set(item.id, { duration: item.duration, handle });
      }
    }
  }, [items]);

  useEffect(() => {
    const leaving = new Set(items.filter((toast) => toast.state === 'leaving').map((toast) => toast.id));
    for (const [id, handle] of exitTimers.current) {
      if (!leaving.has(id)) {
        clearTimeout(handle);
        exitTimers.current.delete(id);
      }
    }
    for (const item of items) {
      if (item.state === 'leaving' && !exitTimers.current.has(item.id)) {
        const handle = window.setTimeout(() => {
          exitTimers.current.delete(item.id);
          setItems((current) => removeToast(current, item.id));
        }, EXIT_MS);
        exitTimers.current.set(item.id, handle);
      }
    }
  }, [items]);

  useEffect(() => () => {
    for (const entry of lifetimeTimers.current.values()) {
      clearTimeout(entry.handle);
    }
    for (const handle of exitTimers.current.values()) {
      clearTimeout(handle);
    }
    for (const handle of graceTimers.current.values()) {
      clearTimeout(handle);
    }
    lifetimeTimers.current.clear();
    exitTimers.current.clear();
    graceTimers.current.clear();
  }, []);

  const api = useMemo<ToastApi>(
    () => ({ push, dismiss, dismissSelf, noteHover }),
    [push, dismiss, dismissSelf, noteHover],
  );

  const rendered = items.filter((toast) => toast.state !== 'waiting');

  return (
    <ToastContext.Provider value={api}>
      {children}
      {rendered.length > 0 && (
        <div className="toast-stack" aria-label="Notifications">
          {rendered.map((item) => (
            <Toast key={item.id} item={item} onDismissSelf={dismissSelf} onHover={noteHover} />
          ))}
        </div>
      )}
    </ToastContext.Provider>
  );
}
