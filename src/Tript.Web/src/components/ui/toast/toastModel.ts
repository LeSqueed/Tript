// SPDX-License-Identifier: GPL-2.0-or-later

export type ToastKind = 'success' | 'error' | 'warning' | 'info';

export interface ToastAction {
  label: string;
  onClick: () => void;
  variant?: 'primary' | 'ghost' | 'danger';
  disabled?: boolean;
}

export interface ToastSpec {
  key?: string;
  kind: ToastKind;
  message: string;
  title?: string;
  duration?: number;
  dismissible?: boolean;
  actions?: ToastAction[];
  note?: string;
  onDismiss?: () => void;
  testId?: string;
}

export type ToastState = 'waiting' | 'visible' | 'leaving';

export interface ToastItem {
  id: number;
  key?: string;
  kind: ToastKind;
  title?: string;
  message: string;
  note?: string;
  actions?: ToastAction[];
  duration: number;
  dismissible?: boolean;
  state: ToastState;
  held: boolean;
  onDismiss?: () => void;
  testId?: string;
}

export const MAX_VISIBLE = 4;

export const MIN_READING_MS = 4000;
export const MAX_READING_MS = 15000;
export const MS_PER_CHARACTER = 60;

export function readingDuration(title: string | undefined, message: string, note: string | undefined): number {
  const characters = `${title ?? ''} ${message} ${note ?? ''}`.trim().length;
  return Math.min(MAX_READING_MS, Math.max(MIN_READING_MS, characters * MS_PER_CHARACTER));
}

function visibleCount(items: ToastItem[]): number {
  return items.filter((toast) => toast.state === 'visible').length;
}

function normalize(items: ToastItem[]): ToastItem[] {
  let open = MAX_VISIBLE - visibleCount(items);
  if (open <= 0) {
    return items;
  }
  return items.map((toast) => {
    if (open > 0 && toast.state === 'waiting') {
      open -= 1;
      return { ...toast, state: 'visible' as const };
    }
    return toast;
  });
}

export function pushToast(items: ToastItem[], spec: ToastSpec, id: number): ToastItem[] {
  const fresh: ToastItem = {
    id,
    key: spec.key,
    kind: spec.kind,
    title: spec.title,
    message: spec.message,
    note: spec.note,
    actions: spec.actions,
    duration: spec.duration === undefined ? readingDuration(spec.title, spec.message, spec.note) : spec.duration,
    dismissible: spec.dismissible !== false,
    state: 'visible',
    held: false,
    onDismiss: spec.onDismiss,
    testId: spec.testId,
  };

  if (spec.key !== undefined) {
    const existing = items.find((toast) => toast.key === spec.key);
    if (existing !== undefined) {
      const others = items.filter((toast) => toast.id !== existing.id);
      fresh.state = existing.state === 'waiting' || visibleCount(others) >= MAX_VISIBLE
        ? 'waiting'
        : 'visible';
      return normalize(items.map((toast) => toast.id === existing.id ? fresh : toast));
    }
  }

  if (visibleCount(items) >= MAX_VISIBLE) {
    fresh.state = 'waiting';
  }
  return normalize([...items, fresh]);
}

export function dismissKey(items: ToastItem[], key: string): ToastItem[] {
  if (!items.some((toast) => toast.key === key && toast.state !== 'leaving')) {
    return items;
  }
  return normalize(
    items
      .map((toast) => {
        if (toast.key !== key || toast.state === 'leaving') {
          return toast;
        }
        if (toast.state === 'waiting') {
          return null;
        }
        return { ...toast, state: 'leaving' as const };
      })
      .filter((toast): toast is ToastItem => toast !== null),
  );
}

export function dismissItem(items: ToastItem[], id: number): ToastItem[] {
  if (!items.some((toast) => toast.id === id && toast.state !== 'leaving')) {
    return items;
  }
  return normalize(
    items
      .map((toast) => {
        if (toast.id !== id || toast.state === 'leaving') {
          return toast;
        }
        if (toast.state === 'waiting') {
          return null;
        }
        return { ...toast, state: 'leaving' as const };
      })
      .filter((toast): toast is ToastItem => toast !== null),
  );
}

export function expireToast(items: ToastItem[], id: number, hovered: boolean): ToastItem[] {
  return items.map((toast) => {
    if (toast.id !== id || toast.state !== 'visible' || toast.held) {
      return toast;
    }
    return hovered ? { ...toast, held: true } : { ...toast, state: 'leaving' as const };
  });
}

export function releaseToast(items: ToastItem[], id: number): ToastItem[] {
  return items.map((toast) =>
    toast.id === id && toast.state === 'visible' && toast.held
      ? { ...toast, state: 'leaving' as const }
      : toast,
  );
}

export function removeToast(items: ToastItem[], id: number): ToastItem[] {
  return normalize(items.filter((toast) => toast.id !== id));
}
