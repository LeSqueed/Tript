import type { SVGProps } from 'react';

export interface TripwireMarkProps extends Omit<SVGProps<SVGSVGElement>, 'aria-label'> {
  label?: string;
  size?: number | string;
}

export function TripwireMark({ label, size = 32, className, ...props }: TripwireMarkProps) {
  const labelled = label !== undefined;
  return (
    <svg
      {...props}
      className={className ? `tripwire-mark ${className}` : 'tripwire-mark'}
      width={size}
      height={size}
      viewBox="0 0 64 64"
      role={labelled ? 'img' : undefined}
      aria-label={labelled ? label : undefined}
      aria-hidden={labelled ? undefined : true}
      focusable="false"
    >
      <g className="tripwire-viewfinder">
        <path d="M18 25V18H25M39 18H46V25M46 39V46H39M25 46H18V39" />
      </g>
      <path className="tripwire-crosshair" d="M25 32H39M32 25V39" />
      <path className="tripwire-axis" d="M32 14V18M32 46V50" />
    </svg>
  );
}
