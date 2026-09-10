// SPDX-License-Identifier: GPL-2.0-or-later

import type { TimelineRegion } from './clipSeam';

export const LOOP_CROSS_STEP_SECONDS = 2;

export interface LoopDecision {
  region: TimelineRegion | null;
  shouldLoopBack: boolean;
}

export function computeLoopDecision(
  currentTime: number,
  previousTime: number,
  playing: boolean,
  regions: TimelineRegion[],
  selectedRegionId: string | null,
): LoopDecision {
  if (!playing || selectedRegionId === null) {
    return { region: null, shouldLoopBack: false };
  }
  const selected = regions.find((region) => region.id === selectedRegionId);
  if (!selected) {
    return { region: null, shouldLoopBack: false };
  }
  const crossedEnd =
    previousTime < selected.end &&
    currentTime >= selected.end &&
    currentTime - previousTime < LOOP_CROSS_STEP_SECONDS;
  const inside = currentTime > selected.start && currentTime < selected.end;
  if (!crossedEnd && !inside) {
    return { region: null, shouldLoopBack: false };
  }
  return { region: selected, shouldLoopBack: crossedEnd };
}

export const LOOP_EDIT_EPSILON_SECONDS = 1e-4;

export function computeEditSeek(
  previous: TimelineRegion | null,
  next: TimelineRegion | null,
  currentTime: number,
): number | null {
  if (!previous || !next || previous.id !== next.id) {
    return null;
  }
  const startDelta = next.start - previous.start;
  const endDelta = next.end - previous.end;
  const startMoved = Math.abs(startDelta) > LOOP_EDIT_EPSILON_SECONDS;
  const endMoved = Math.abs(endDelta) > LOOP_EDIT_EPSILON_SECONDS;
  if (!startMoved && !endMoved) {
    return null;
  }
  const wasInLoop = currentTime >= previous.start && currentTime <= previous.end;
  if (!wasInLoop) {
    return null;
  }
  if (endMoved && currentTime >= next.end) {
    return next.start;
  }
  const slid = startMoved && endMoved && Math.abs(startDelta - endDelta) <= LOOP_EDIT_EPSILON_SECONDS;
  if (!slid && startDelta > LOOP_EDIT_EPSILON_SECONDS && currentTime < next.start) {
    return next.start;
  }
  return null;
}
