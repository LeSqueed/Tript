// SPDX-License-Identifier: GPL-2.0-or-later
//
// T9 seam — region-selection primitives for the clip dialog.
//
// T9 (the clipping UI, a later/concurrent task) builds the clip dialog on these. This module
// deliberately contains no dialog: it is the shape the player exposes so T9 can render and select
// regions on the zoomed timeline. Regions are independent of bookmarks — they can start and end
// anywhere, and a region is the unit T9 sends to CreateClip.
//
// The segment-looping affordance (clicking a segment repeats just that segment while the playhead
// is inside it) is likewise T9's; the timeline surfaces `onRegionSelect` for it.

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
