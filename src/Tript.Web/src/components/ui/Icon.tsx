// SPDX-License-Identifier: GPL-2.0-or-later
//
// The icon set. Inline SVG on a 24×24 grid, drawn in `currentColor` so an icon takes the colour of
// whatever it sits in. No icon font and no dependency: a published build is offline and CSP-bound,
// and unicode glyphs (which this replaces) render differently on every platform — `⌁` for Trash was
// illegible at 15px, because it is a character, not an icon.

import type { SVGProps } from 'react';

export type IconName =
  | 'play'
  | 'pause'
  | 'volume'
  | 'volumeOff'
  | 'fullscreen'
  | 'star'
  | 'trash'
  | 'check'
  | 'chevronDown'
  | 'chevronLeft'
  | 'chevronRight'
  | 'settings'
  | 'library'
  | 'clip'
  | 'close';

const SOLID = { fill: 'currentColor', stroke: 'none' } as const;

const PATHS: Record<IconName, React.ReactNode> = {
  play: <path d="M8 5.4v13.2L18.5 12z" {...SOLID} />,
  pause: (
    <>
      <rect x="7" y="5" width="3.4" height="14" rx="1" {...SOLID} />
      <rect x="13.6" y="5" width="3.4" height="14" rx="1" {...SOLID} />
    </>
  ),
  volume: (
    <>
      <path d="M4 9.5v5h3.5L12 18.5v-13L7.5 9.5z" {...SOLID} />
      <path d="M15.8 9.2a4 4 0 0 1 0 5.6" />
      <path d="M18.4 6.6a7.6 7.6 0 0 1 0 10.8" />
    </>
  ),
  volumeOff: (
    <>
      <path d="M4 9.5v5h3.5L12 18.5v-13L7.5 9.5z" {...SOLID} />
      <path d="M16 9.5l5 5M21 9.5l-5 5" />
    </>
  ),
  fullscreen: <path d="M8.5 3.5H5.5a2 2 0 0 0-2 2v3M15.5 3.5h3a2 2 0 0 1 2 2v3M8.5 20.5H5.5a2 2 0 0 1-2-2v-3M15.5 20.5h3a2 2 0 0 0 2-2v-3" />,
  star: <path d="M12 3.6l2.6 5.3 5.8.85-4.2 4.1 1 5.8L12 16.9l-5.2 2.75 1-5.8-4.2-4.1 5.8-.85z" />,
  trash: (
    <>
      <path d="M4.5 6.5h15M9.5 3.5h5M6.5 6.5l.9 13.1h9.2l.9-13.1" />
      <path d="M10 10.5v5.5M14 10.5v5.5" />
    </>
  ),
  check: <path d="M4.5 12.5l5 5 10-11" />,
  chevronDown: <path d="M6.5 9.5l5.5 5.5 5.5-5.5" />,
  chevronLeft: <path d="M14.5 6.5L9 12l5.5 5.5" />,
  chevronRight: <path d="M9.5 6.5L15 12l-5.5 5.5" />,
  settings: (
    <>
      <path d="M3.5 8.5h8M15.7 8.5h4.8M3.5 15.5h4M11.7 15.5h8.8" />
      <circle cx="13.5" cy="8.5" r="2.2" />
      <circle cx="9.5" cy="15.5" r="2.2" />
    </>
  ),
  library: (
    <>
      <rect x="3.5" y="5" width="17" height="14" rx="2.5" />
      <path d="M10.2 9.4v5.2L14.8 12z" {...SOLID} />
    </>
  ),
  clip: (
    <>
      <circle cx="6.2" cy="7" r="2.4" />
      <circle cx="6.2" cy="17" r="2.4" />
      <path d="M8.4 8.4L19 16.6M8.4 15.6L19 7.4" />
    </>
  ),
  close: <path d="M6.5 6.5l11 11M17.5 6.5l-11 11" />,
};

export interface IconProps extends Omit<SVGProps<SVGSVGElement>, 'name'> {
  name: IconName;
  /** Pixel size; icons are square. */
  size?: number;
  /** Give this only when the icon is the sole label — otherwise it stays hidden from assistive tech. */
  label?: string;
  /** `star` doubles as its own filled state rather than shipping two paths. */
  filled?: boolean;
}

export function Icon({ name, size = 18, label, filled, className, ...rest }: IconProps) {
  return (
    <svg
      {...rest}
      className={className ? `icon ${className}` : 'icon'}
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill={filled ? 'currentColor' : 'none'}
      stroke="currentColor"
      strokeWidth={1.7}
      strokeLinecap="round"
      strokeLinejoin="round"
      role={label ? 'img' : undefined}
      aria-label={label}
      aria-hidden={label ? undefined : true}
      focusable="false"
    >
      {PATHS[name]}
    </svg>
  );
}
