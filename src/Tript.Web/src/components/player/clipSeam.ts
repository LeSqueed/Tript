// SPDX-License-Identifier: GPL-2.0-or-later

export interface TimelineRegion {
  id: string;
  start: number;
  end: number;
}

export interface RegionSelectionProps {
  regions: TimelineRegion[];
  selectedRegionId: string | null;
  onRegionSelect(region: TimelineRegion): void;
}
