// SPDX-License-Identifier: GPL-2.0-or-later
//
// Shared form primitives for the settings pages, styled to the dark modern theme. The accent is a
// light teal on a dark primary, so the contrast trap is avoided by construction: dark text on the
// accent (`--color-accent-content`), light text on the dark ground (`--color-base-content`).

import type { ReactNode, SelectHTMLAttributes } from 'react';

export function Field({
  label,
  hint,
  children,
}: {
  label: string;
  hint?: string;
  children: ReactNode;
}) {
  return (
    <label className="settings-field">
      <span className="settings-field-label">{label}</span>
      {children}
      {hint ? <span className="settings-field-hint">{hint}</span> : null}
    </label>
  );
}

/** A text/number field bound to a value and an onChange. */
export function TextField({
  value,
  onChange,
  type = 'text',
  min,
  step,
  placeholder,
}: {
  value: string | number;
  onChange: (value: string) => void;
  type?: 'text' | 'number' | 'url';
  min?: number;
  step?: number;
  placeholder?: string;
}) {
  return (
    <input
      type={type}
      className="settings-input"
      value={value}
      min={min}
      step={step}
      placeholder={placeholder}
      onChange={(event) => onChange(event.target.value)}
    />
  );
}

export interface SelectOption {
  value: string;
  label: string;
}

export function SelectField({
  value,
  onChange,
  options,
  ...rest
}: {
  value: string;
  onChange: (value: string) => void;
  options: SelectOption[];
} & Omit<SelectHTMLAttributes<HTMLSelectElement>, 'value' | 'onChange'>) {
  return (
    <select
      {...rest}
      className="settings-input settings-select"
      value={value}
      onChange={(event) => onChange(event.target.value)}
    >
      {options.map((option) => (
        <option key={option.value} value={option.value}>
          {option.label}
        </option>
      ))}
    </select>
  );
}

/** An accent-filled action button. */
export function ActionButton({
  children,
  onClick,
  type = 'button',
  disabled,
  className,
  title,
}: {
  children: ReactNode;
  onClick?: () => void;
  type?: 'button' | 'submit';
  disabled?: boolean;
  className?: string;
  title?: string;
}) {
  return (
    <button
      type={type}
      className={className ? `btn ${className}` : 'btn'}
      onClick={onClick}
      disabled={disabled}
      title={title}
    >
      {children}
    </button>
  );
}

/** A ghost (dark) action button — used for destructive-but-not-dangerous actions. */
export function GhostButton({
  children,
  onClick,
  disabled,
  className,
  title,
}: {
  children: ReactNode;
  onClick?: () => void;
  disabled?: boolean;
  className?: string;
  title?: string;
}) {
  return (
    <button
      type="button"
      className={className ? `btn ghost ${className}` : 'btn ghost'}
      onClick={onClick}
      disabled={disabled}
      title={title}
    >
      {children}
    </button>
  );
}

export function DangerButton({
  children,
  onClick,
  disabled,
  title,
}: {
  children: ReactNode;
  onClick?: () => void;
  disabled?: boolean;
  title?: string;
}) {
  return (
    <button
      type="button"
      className="btn danger"
      onClick={onClick}
      disabled={disabled}
      title={title}
    >
      {children}
    </button>
  );
}

/** A status pill for a field that only the backend can settle (e.g. encoder availability). */
export function Pill({ tone, children }: { tone: 'muted' | 'info' | 'success' | 'warning'; children: ReactNode }) {
  return <span className={`pill pill-${tone}`}>{children}</span>;
}
