// SPDX-License-Identifier: GPL-2.0-or-later

import type { PointerEvent as ReactPointerEvent } from 'react';

// Keyboard activation has no pointer-up event, so Tab navigation keeps its normal focus behavior.
export function releasePointerFocus(event: ReactPointerEvent<HTMLElement>) {
  event.currentTarget.blur();
}
