// SPDX-License-Identifier: GPL-2.0-or-later

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
  | 'pencil'
  | 'folder'
  | 'close'
  | 'monitor'
  | 'checkCircle'
  | 'infoCircle'
  | 'errorCircle'
  | 'alertTriangle';

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
  pencil: (
    <>
      <path d="M4 20l4.2-1 10.6-10.6a2.1 2.1 0 0 0-3-3L5.2 16z" />
      <path d="M13.8 7.4l3 3M4 20h5" />
    </>
  ),
  folder: <path d="M3.5 7.5v10a2 2 0 0 0 2 2h13a2 2 0 0 0 2-2v-8a2 2 0 0 0-2-2h-7l-2-3h-4a2 2 0 0 0-2 2z" />,
  close: <path strokeWidth={2} d="M5.5 5.5l13 13M18.5 5.5l-13 13" />,
  monitor: (
    <>
      <rect x="3" y="4.5" width="18" height="12.5" rx="2" />
      <path d="M9 20.5h6M12 17v3.5" />
    </>
  ),
  checkCircle: (
    <>
      <circle cx="12" cy="12" r="8.5" />
      <path d="M8.4 12.4l2.4 2.4 4.8-5.3" />
    </>
  ),
  infoCircle: (
    <>
      <circle cx="12" cy="12" r="8.5" />
      <path d="M12 11.3V16" />
      <path d="M12 8.2v.01" />
    </>
  ),
  errorCircle: (
    <>
      <circle cx="12" cy="12" r="8.5" />
      <path d="M12 7.8V13" />
      <path d="M12 16.2v.01" />
    </>
  ),
  alertTriangle: (
    <>
      <path d="M12 5L21 19.5H3z" />
      <path d="M12 10.5V14" />
      <path d="M12 16.8v.01" />
    </>
  ),
};

export interface IconProps extends Omit<SVGProps<SVGSVGElement>, 'name'> {
  name: IconName;
  size?: number;
  label?: string;
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
