// SPDX-License-Identifier: GPL-2.0-or-later

import type { TrainingEpochPoint } from './trainingMetrics';

const WIDTH = 100;
const HEIGHT = 40;
const PAD = 4;

export function TrainingSparkline({ points, totalEpochs }: { points: TrainingEpochPoint[]; totalEpochs: number }) {
  const xFor = (epoch: number) => (totalEpochs <= 1 ? WIDTH / 2 : ((epoch - 1) / (totalEpochs - 1)) * WIDTH);
  const seriesFor = (pick: (point: TrainingEpochPoint) => number | null) => {
    const series = points.filter((point) => pick(point) != null);
    if (series.length === 0) return null;
    const values = series.map((point) => pick(point) as number);
    const min = Math.min(...values);
    const span = Math.max(...values) - min;
    const toCoord = (point: TrainingEpochPoint) => {
      const normalized = span === 0 ? 0.5 : ((pick(point) as number) - min) / span;
      return { x: xFor(point.epoch), y: PAD + (1 - normalized) * (HEIGHT - PAD * 2) };
    };
    const coords = series.map(toCoord);
    const drawn = coords.length === 1
      ? [{ x: coords[0].x - 1.5, y: coords[0].y }, { x: coords[0].x + 1.5, y: coords[0].y }]
      : coords;
    return {
      line: drawn.map((c) => `${c.x.toFixed(2)},${c.y.toFixed(2)}`).join(' '),
      first: coords[0],
      last: coords[coords.length - 1],
    };
  };
  const loss = seriesFor((point) => point.loss);
  const map = seriesFor((point) => point.map50);
  if (!loss && !map) return null;
  const grid = [0.25, 0.5, 0.75].map((f) => PAD + f * (HEIGHT - PAD * 2));
  const lossArea = loss
    ? `${loss.first.x.toFixed(2)},${HEIGHT - PAD} ${loss.line} ${loss.last.x.toFixed(2)},${HEIGHT - PAD}`
    : null;
  return (
    <svg
      className="training-sparkline"
      viewBox={`0 0 ${WIDTH} ${HEIGHT}`}
      preserveAspectRatio="none"
      role="img"
      aria-label="Training metrics per epoch"
    >
      {grid.map((y) => <line key={y} className="training-sparkline-grid" x1={0} x2={WIDTH} y1={y} y2={y} />)}
      <line className="training-sparkline-grid training-sparkline-baseline" x1={0} x2={WIDTH} y1={HEIGHT - PAD} y2={HEIGHT - PAD} />
      {lossArea && <polygon className="training-sparkline-fill" points={lossArea} />}
      {map && <polyline className="training-sparkline-map" points={map.line} />}
      {loss && <polyline className="training-sparkline-loss" points={loss.line} />}
      {map && (
        <line
          className="training-sparkline-tick training-sparkline-map"
          x1={map.last.x} x2={map.last.x} y1={map.last.y - 2} y2={map.last.y + 2}
        />
      )}
      {loss && (
        <line
          className="training-sparkline-tick training-sparkline-loss"
          x1={loss.last.x} x2={loss.last.x} y1={loss.last.y - 2} y2={loss.last.y + 2}
        />
      )}
    </svg>
  );
}
