import { useRef, useState, type PointerEvent } from 'react';
import type { TrainingEventDefinition } from '../ipc/protocol';
import { Button, SelectField } from './ui/controls';
import { boxFromPoints, moveBox, normalizedPoint, type TrainingPoint } from './trainingCoordinates';
import { useTrainingDialog } from './useTrainingDialog';

interface TrainingRegionEditorProps {
  event: TrainingEventDefinition;
  events: TrainingEventDefinition[];
  backgroundImage?: string;
  imageWidth?: number;
  imageHeight?: number;
  onCancel(): void;
  onSave(event: TrainingEventDefinition): void;
}

interface RegionBox {
  centerX: number;
  centerY: number;
  width: number;
  height: number;
}

type Gesture =
  | { kind: 'draw'; start: TrainingPoint; original: RegionBox | null }
  | { kind: 'move'; start: TrainingPoint; original: RegionBox }
  | { kind: 'resize'; original: RegionBox; handle: string };

function regionFromEvent(event: TrainingEventDefinition): RegionBox | null {
  const values = [event.screenRegionX, event.screenRegionY, event.screenRegionW, event.screenRegionH];
  if (values.some((value) => value == null || !Number.isFinite(value))) return null;
  const [x, y, width, height] = values as number[];
  if (width <= 0 || height <= 0 || x < 0 || y < 0 || x + width > 1 || y + height > 1) return null;
  return { centerX: x + width / 2, centerY: y + height / 2, width, height };
}

function eventWithRegion(event: TrainingEventDefinition, region: RegionBox | null): TrainingEventDefinition {
  if (!region) {
    return {
      ...event,
      screenRegionX: null,
      screenRegionY: null,
      screenRegionW: null,
      screenRegionH: null,
    };
  }
  return {
    ...event,
    screenRegionX: region.centerX - region.width / 2,
    screenRegionY: region.centerY - region.height / 2,
    screenRegionW: region.width,
    screenRegionH: region.height,
  };
}

function resizeRegion(box: RegionBox, point: TrainingPoint, handle: string): RegionBox {
  let left = box.centerX - box.width / 2;
  let right = box.centerX + box.width / 2;
  let top = box.centerY - box.height / 2;
  let bottom = box.centerY + box.height / 2;
  if (handle.includes('w')) left = Math.min(point.x, right - 0.001);
  if (handle.includes('e')) right = Math.max(point.x, left + 0.001);
  if (handle.includes('n')) top = Math.min(point.y, bottom - 0.001);
  if (handle.includes('s')) bottom = Math.max(point.y, top + 0.001);
  left = Math.max(0, left);
  top = Math.max(0, top);
  right = Math.min(1, right);
  bottom = Math.min(1, bottom);
  return { centerX: (left + right) / 2, centerY: (top + bottom) / 2, width: right - left, height: bottom - top };
}

export function TrainingRegionEditor({ event, events, backgroundImage, imageWidth = 16, imageHeight = 9, onCancel, onSave }: TrainingRegionEditorProps) {
  const [region, setRegion] = useState<RegionBox | null>(() => regionFromEvent(event));
  const [copySource, setCopySource] = useState('');
  const [gesture, setGesture] = useState<Gesture | null>(null);
  const dialogRef = useTrainingDialog<HTMLElement>(onCancel);
  const imageNode = useRef<HTMLDivElement>(null);

  const pointFor = (pointer: React.PointerEvent): TrainingPoint | null => {
    const bounds = imageNode.current?.getBoundingClientRect();
    return bounds ? normalizedPoint(pointer.nativeEvent, bounds) : null;
  };

  const updateGesture = (pointer: React.PointerEvent) => {
    if (!gesture) return;
    const point = pointFor(pointer);
    if (!point) return;
    if (gesture.kind === 'draw') {
      const box = boxFromPoints(gesture.start, point, 0);
      setRegion({ centerX: box.centerX, centerY: box.centerY, width: box.width, height: box.height });
    } else if (gesture.kind === 'move') {
      const moved = moveBox({ ...gesture.original, classId: 0 }, {
        x: point.x - gesture.start.x,
        y: point.y - gesture.start.y,
      });
      setRegion({ centerX: moved.centerX, centerY: moved.centerY, width: moved.width, height: moved.height });
    } else {
      setRegion(resizeRegion(gesture.original, point, gesture.handle));
    }
  };

  const beginDraw = (pointer: PointerEvent) => {
    if (pointer.target !== pointer.currentTarget && (pointer.target as HTMLElement).tagName !== 'IMG') return;
    const point = pointFor(pointer);
    if (!point) return;
    setRegion({ centerX: point.x, centerY: point.y, width: 0, height: 0 });
    setGesture({ kind: 'draw', start: point, original: region });
    pointer.currentTarget.setPointerCapture(pointer.pointerId);
  };

  const beginMove = (pointer: PointerEvent) => {
    if (!region) return;
    const point = pointFor(pointer);
    if (!point) return;
    setGesture({ kind: 'move', start: point, original: region });
    pointer.currentTarget.setPointerCapture(pointer.pointerId);
    pointer.stopPropagation();
  };

  const beginResize = (pointer: PointerEvent, handle: string) => {
    if (!region) return;
    setGesture({ kind: 'resize', original: region, handle });
    pointer.currentTarget.setPointerCapture(pointer.pointerId);
    pointer.stopPropagation();
  };

  const finishGesture = () => {
    if (gesture?.kind === 'draw' && (!region || region.width <= 0.001 || region.height <= 0.001)) setRegion(gesture.original);
    setGesture(null);
  };

  const cancelGesture = () => {
    if (gesture?.kind === 'draw' || gesture?.kind === 'move' || gesture?.kind === 'resize') setRegion(gesture.original);
    setGesture(null);
  };

  const copyRegion = () => {
    const source = events.find((candidate) => String(candidate.id) === copySource);
    if (source) setRegion(regionFromEvent(source));
  };

  return (
    <div className="training-region-overlay" role="presentation">
      <section ref={dialogRef} tabIndex={-1} className="training-region-dialog" role="dialog" aria-modal="true" aria-labelledby="training-region-title">
        <div className="training-palette-heading">
          <div>
            <p className="training-eyebrow">Screen region</p>
            <h3 id="training-region-title">{event.name}</h3>
          </div>
          <Button variant="ghost" size="small" onClick={onCancel}>Cancel</Button>
        </div>
        <p className="muted small">Draw the part of the screen this event should use. Coordinates are saved normalized to the full frame.</p>
        <div
          className="training-region-canvas"
          ref={imageNode}
          style={{ aspectRatio: `${imageWidth} / ${imageHeight}` }}
          onPointerDown={beginDraw}
          onPointerMove={updateGesture}
          onPointerUp={finishGesture}
          onPointerCancel={cancelGesture}
        >
          {backgroundImage && <img src={backgroundImage} alt="Training frame region background" draggable={false} />}
          {region && (
            <span
              className="training-region-box"
              style={{
                left: `${(region.centerX - region.width / 2) * 100}%`,
                top: `${(region.centerY - region.height / 2) * 100}%`,
                width: `${region.width * 100}%`,
                height: `${region.height * 100}%`,
              }}
              onPointerDown={beginMove}
            >
              {['nw', 'n', 'ne', 'e', 'se', 's', 'sw', 'w'].map((handle) => (
                <span key={handle} className="training-region-handle" data-handle={handle} onPointerDown={(pointer) => beginResize(pointer, handle)} />
              ))}
            </span>
          )}
        </div>
        <div className="training-region-tools">
          <SelectField
            aria-label="Copy region from event"
            value={copySource}
            onChange={setCopySource}
            options={[{ value: '', label: 'Copy region from event' }, ...events
              .filter((candidate) => candidate.id !== event.id && regionFromEvent(candidate))
              .map((candidate) => ({ value: String(candidate.id), label: candidate.name }))]}
          />
          <Button variant="ghost" onClick={copyRegion} disabled={!copySource}>Copy region</Button>
          <Button variant="ghost" onClick={() => setRegion(null)} disabled={!region}>Clear region</Button>
        </div>
        <div className="training-event-dialog-actions">
          <Button variant="ghost" onClick={onCancel}>Cancel</Button>
          <Button onClick={() => onSave(eventWithRegion(event, region))}>Save region</Button>
        </div>
      </section>
    </div>
  );
}
