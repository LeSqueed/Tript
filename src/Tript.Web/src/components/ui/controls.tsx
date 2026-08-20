// SPDX-License-Identifier: GPL-2.0-or-later
//
// The control layer. Every button, field and input in the app comes from here — see
// docs/design-system.md §4. A view may position these; it must never restyle their interiors, and it
// must never render a bare <button>, <input> or <select> of its own. This file used to live under
// settings/ with a settings- class prefix, which is why the player hand-rolled raw controls beside
// it and looked unstyled.

import type { InputHTMLAttributes, ReactNode, Ref, SelectHTMLAttributes } from 'react';
import { Icon, type IconName } from './Icon';

/* ---------------------------------------------------------------- buttons */

export type ButtonVariant = 'primary' | 'ghost' | 'danger';
export type ButtonSize = 'default' | 'small' | 'icon';

export interface ButtonProps {
  children?: ReactNode;
  variant?: ButtonVariant;
  size?: ButtonSize;
  /** Drawn before the label, or as the whole button when size is "icon". */
  icon?: IconName;
  iconFilled?: boolean;
  /** Marks a toggle-ish button as on. Not for one-of-N — use SegmentedControl. */
  active?: boolean;
  onClick?: () => void;
  type?: 'button' | 'submit';
  disabled?: boolean;
  title?: string;
  className?: string;
  /** React 19 hands `ref` to function components as an ordinary prop. */
  ref?: Ref<HTMLButtonElement>;
  'aria-label'?: string;
  'aria-pressed'?: boolean;
  'aria-describedby'?: string;
}

export function Button({
  children,
  variant = 'primary',
  size = 'default',
  icon,
  iconFilled,
  active,
  onClick,
  type = 'button',
  disabled,
  title,
  className,
  ref,
  ...aria
}: ButtonProps) {
  const classes = ['btn', `btn-${variant}`];
  if (size !== 'default') classes.push(`btn-${size}`);
  if (active) classes.push('is-active');
  if (className) classes.push(className);
  return (
    <button
      {...aria}
      ref={ref}
      type={type}
      className={classes.join(' ')}
      onClick={onClick}
      disabled={disabled}
      title={title}
    >
      {icon && <Icon name={icon} filled={iconFilled} size={size === 'small' ? 15 : 18} />}
      {children}
    </button>
  );
}

/* ----------------------------------------------------------------- fields */

export function Field({ label, hint, children }: { label: string; hint?: string; children: ReactNode }) {
  return (
    <label className="field">
      <span className="field-label">{label}</span>
      {children}
      {hint ? <span className="field-hint">{hint}</span> : null}
    </label>
  );
}

export function TextField({
  value,
  onChange,
  type = 'text',
  ...rest
}: {
  value: string | number;
  onChange: (value: string) => void;
  type?: 'text' | 'number' | 'url';
} & Omit<InputHTMLAttributes<HTMLInputElement>, 'value' | 'onChange' | 'type'>) {
  return (
    <input
      {...rest}
      type={type}
      className={rest.className ? `input ${rest.className}` : 'input'}
      value={value}
      onChange={(e) => onChange(e.target.value)}
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
  compact,
  ...rest
}: {
  value: string;
  onChange: (value: string) => void;
  options: SelectOption[];
  /** Narrower padding for a control sitting inside a dense row, like the transport. */
  compact?: boolean;
} & Omit<SelectHTMLAttributes<HTMLSelectElement>, 'value' | 'onChange'>) {
  return (
    <select
      {...rest}
      className={[
        'input',
        'select',
        compact ? 'select-compact' : '',
        rest.className ?? '',
      ]
        .filter(Boolean)
        .join(' ')}
      value={value}
      onChange={(e) => onChange(e.target.value)}
    >
      {options.map((option) => (
        <option key={option.value} value={option.value}>
          {option.label}
        </option>
      ))}
    </select>
  );
}

/* ---------------------------------------------------------------- slider */

export function Slider({
  value,
  min = 0,
  max = 1,
  step = 0.01,
  onChange,
  ...rest
}: {
  value: number;
  min?: number;
  max?: number;
  step?: number;
  onChange: (value: number) => void;
} & Omit<InputHTMLAttributes<HTMLInputElement>, 'value' | 'onChange' | 'type'>) {
  // The filled portion is a gradient stop rather than a second element: a range input has no
  // styleable "fill" pseudo that both engines agree on.
  const fraction = max > min ? ((value - min) / (max - min)) * 100 : 0;
  return (
    <input
      {...rest}
      type="range"
      className={rest.className ? `slider ${rest.className}` : 'slider'}
      style={{ ['--slider-fill' as string]: `${fraction}%` }}
      min={min}
      max={max}
      step={step}
      value={value}
      onChange={(e) => onChange(Number(e.currentTarget.value))}
    />
  );
}

/* ---------------------------------------------------------------- toggle */

/** A boolean. Deliberately unlike SegmentedControl — see design-system §4. */
export function Toggle({
  checked,
  onChange,
  label,
}: {
  checked: boolean;
  onChange: (checked: boolean) => void;
  label: string;
}) {
  return (
    <label className="toggle">
      <input type="checkbox" checked={checked} onChange={(e) => onChange(e.currentTarget.checked)} />
      <span className="toggle-track" aria-hidden="true">
        <span className="toggle-knob" />
      </span>
      <span className="toggle-label">{label}</span>
    </label>
  );
}

/* ------------------------------------------------------- segmented control */

export interface Segment<T extends string> {
  value: T;
  label: string;
}

/**
 * One-of-N, mutually exclusive. Never put a boolean in one — that is what Toggle is for.
 *
 * A radiogroup, which means the keyboard contract that comes with it: one tab stop for the whole
 * group, arrows move the selection. Declaring role="radio" without that is a worse lie than plain
 * buttons would have been.
 */
export function SegmentedControl<T extends string>({
  value,
  segments,
  onChange,
  label,
}: {
  value: T;
  segments: Segment<T>[];
  onChange: (value: T) => void;
  label: string;
}) {
  const move = (delta: number) => {
    const index = segments.findIndex((segment) => segment.value === value);
    const next = segments[(index + delta + segments.length) % segments.length];
    if (next) {
      onChange(next.value);
    }
  };

  return (
    <div className="segmented" role="radiogroup" aria-label={label}>
      {segments.map((segment) => {
        const selected = value === segment.value;
        return (
          <button
            key={segment.value}
            type="button"
            role="radio"
            aria-checked={selected}
            // Roving tabindex: the group is one stop, not one per option.
            tabIndex={selected ? 0 : -1}
            className={selected ? 'segment is-active' : 'segment'}
            onClick={() => onChange(segment.value)}
            onKeyDown={(event) => {
              if (event.key === 'ArrowRight' || event.key === 'ArrowDown') {
                event.preventDefault();
                move(1);
              } else if (event.key === 'ArrowLeft' || event.key === 'ArrowUp') {
                event.preventDefault();
                move(-1);
              }
            }}
          >
            {segment.label}
          </button>
        );
      })}
    </div>
  );
}

/* ------------------------------------------------------------ check/radio */

export function Checkbox({
  checked,
  onChange,
  ...rest
}: {
  checked: boolean;
  onChange: (checked: boolean) => void;
} & Omit<InputHTMLAttributes<HTMLInputElement>, 'checked' | 'onChange' | 'type'>) {
  return (
    <input
      {...rest}
      type="checkbox"
      className={rest.className ? `checkbox ${rest.className}` : 'checkbox'}
      checked={checked}
      onChange={(e) => onChange(e.currentTarget.checked)}
    />
  );
}

export function RadioOption({
  name,
  value,
  checked,
  onChange,
  label,
}: {
  name: string;
  value: string;
  checked: boolean;
  onChange: (value: string) => void;
  label: string;
}) {
  return (
    <label className={checked ? 'radio-option is-active' : 'radio-option'}>
      <input
        type="radio"
        className="radio"
        name={name}
        value={value}
        checked={checked}
        onChange={() => onChange(value)}
      />
      <span>{label}</span>
    </label>
  );
}

export { Icon };
export type { IconName };
