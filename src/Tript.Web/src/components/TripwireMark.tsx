import type { SVGProps } from 'react';

export interface TripwireMarkProps extends Omit<SVGProps<SVGSVGElement>, 'aria-label'> {
  label?: string;
  size?: number | string;
}

/** The Tript mark: a taut line releases and settles again without a bright flash. */
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
      <circle className="tripwire-anchor" cx="10" cy="32" r="4" />
      <circle className="tripwire-anchor" cx="54" cy="32" r="4" />
      <path className="tripwire-line tripwire-line-left" d="M14 32H31" />
      <path className="tripwire-line tripwire-line-right" d="M33 32H50" />
      <path className="tripwire-spark" d="M32 26V38M26 32H38" />
    </svg>
  );
}
