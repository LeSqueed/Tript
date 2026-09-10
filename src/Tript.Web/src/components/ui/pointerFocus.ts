// SPDX-License-Identifier: GPL-2.0-or-later

import type { PointerEvent as ReactPointerEvent } from 'react';

export function releasePointerFocus(event: ReactPointerEvent<HTMLElement>) {
  event.currentTarget.blur();
}
