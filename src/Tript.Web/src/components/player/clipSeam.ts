// SPDX-License-Identifier: GPL-2.0-or-later
//
// T9 seam — region-selection primitives for the clip dialog. T9 (the clipping UI, a
// later/concurrent task) builds the clip dialog on these.

export interface TimelineRegion {
  /** Stable id — T9 generates it when a clip is started. */
  id: string;
  /** Seconds offset into the session. */
  start: number;
  /** Seconds offset into the session. */
  end: number;
}

/**
 * The props the timeline surfaces to T9. `PlayerView` accepts them so the dialog can drive the
 * timeline from the outside; without them the player runs with no regions.
 */
export interface RegionSelectionProps {
  regions: TimelineRegion[];
  selectedRegionId: string | null;
  onRegionSelect(region: TimelineRegion): void;
}
