import { useState, type DragEvent, type ReactNode } from 'react';
import type { TrainingEventDefinition, TrainingRegionGroup } from '../ipc/protocol';
import { Button } from './ui/controls';

const EVENT_DRAG_TYPE = 'application/x-tript-training-event-id';

interface TrainingEventTreeProps {
  events: TrainingEventDefinition[];
  groups: TrainingRegionGroup[];
  activeClassId?: number;
  compact?: boolean;
  eventMeta?(event: TrainingEventDefinition): ReactNode;
  onSelect?(event: TrainingEventDefinition): void;
  onEdit(event: TrainingEventDefinition): void;
  onRegion(event: TrainingEventDefinition): void;
  onDelete?(event: TrainingEventDefinition): void;
  onAddFixedLabel?(event: TrainingEventDefinition): void;
  canAddFixedLabel?(event: TrainingEventDefinition): boolean;
  onMove(eventId: number, groupId: number | null): void;
  onRenameGroup?(group: TrainingRegionGroup): void;
  onRegionGroup?(group: TrainingRegionGroup): void;
  onDeleteGroup?(group: TrainingRegionGroup): void;
}

export function TrainingEventTree({
  events,
  groups,
  activeClassId,
  compact = false,
  eventMeta,
  onSelect,
  onEdit,
  onRegion,
  onDelete,
  onAddFixedLabel,
  canAddFixedLabel,
  onMove,
  onRenameGroup,
  onRegionGroup,
  onDeleteGroup,
}: TrainingEventTreeProps) {
  const [draggingEventId, setDraggingEventId] = useState<number | null>(null);
  const [dropGroupId, setDropGroupId] = useState<number | null | undefined>(undefined);

  const beginDrag = (dragEvent: DragEvent, eventId: number) => {
    dragEvent.dataTransfer.effectAllowed = 'move';
    dragEvent.dataTransfer.setData(EVENT_DRAG_TYPE, String(eventId));
    setDraggingEventId(eventId);
  };

  const drop = (dragEvent: DragEvent, groupId: number | null) => {
    dragEvent.preventDefault();
    const payload = dragEvent.dataTransfer.getData(EVENT_DRAG_TYPE).trim();
    const eventId = Number(payload);
    if (payload && Number.isInteger(eventId) && events.some((event) => event.id === eventId)) {
      onMove(eventId, groupId);
    }
    setDraggingEventId(null);
    setDropGroupId(undefined);
  };

  const folders: Array<{ group: TrainingRegionGroup | null; members: TrainingEventDefinition[] }> = [
    ...groups.map((group) => ({
      group,
      members: events.filter((event) => event.regionGroupId === group.id),
    })),
    {
      group: null,
      members: events.filter((event) => event.regionGroupId == null
        || !groups.some((group) => group.id === event.regionGroupId)),
    },
  ];

  return (
    <div className={`training-event-tree${compact ? ' compact' : ''}`}>
      {folders.map(({ group, members }) => {
        const groupId = group?.id ?? null;
        const isDropTarget = dropGroupId === groupId;
        return (
          <section
            className={`training-event-folder${isDropTarget ? ' drop-target' : ''}`}
            key={group?.id ?? 'ungrouped'}
            onDragEnter={(event) => { event.preventDefault(); setDropGroupId(groupId); }}
            onDragOver={(event) => { event.preventDefault(); event.dataTransfer.dropEffect = 'move'; }}
            onDragLeave={(event) => {
              if (!event.currentTarget.contains(event.relatedTarget as Node | null)) setDropGroupId(undefined);
            }}
            onDrop={(event) => drop(event, groupId)}
          >
            <header className="training-event-folder-header">
              <span className="training-folder-icon" aria-hidden="true" />
              <span className="training-folder-name">
                <strong>{group?.name ?? 'Ungrouped'}</strong>
                <small>{members.length} event{members.length === 1 ? '' : 's'}</small>
              </span>
              {group && (
                <span className="training-folder-actions">
                  {onRenameGroup && <Button variant="ghost" size="small" onClick={() => onRenameGroup(group)}>Rename</Button>}
                  {onRegionGroup && <Button variant="ghost" size="small" onClick={() => onRegionGroup(group)}>Region</Button>}
                  {onDeleteGroup && <Button variant="ghost" size="small" onClick={() => onDeleteGroup(group)}>Delete</Button>}
                </span>
              )}
            </header>
            <div className="training-folder-events" aria-label={`${group?.name ?? 'Ungrouped'} events`}>
              {members.length === 0 && (
                <span className="training-folder-empty">Drop events here</span>
              )}
              {members.map((event) => (
                <div
                  className={`training-tree-event${activeClassId === event.classId ? ' active' : ''}${draggingEventId === event.id ? ' dragging' : ''}`}
                  draggable
                  key={event.id}
                  onDragStart={(dragEvent) => beginDrag(dragEvent, event.id)}
                  onDragEnd={() => { setDraggingEventId(null); setDropGroupId(undefined); }}
                >
                  <span className="training-drag-handle" title="Drag to another folder" aria-hidden="true">::</span>
                  {onSelect ? (
                    <Button
                      variant="ghost"
                      className="training-tree-event-main"
                      onClick={() => onSelect(event)}
                    >
                      <span className="training-event-swatch">
                        {event.detectionKind === 'Ocr' ? 'OCR' : event.classId}
                      </span>
                      <span className="training-tree-event-copy">
                        <strong>{event.name}</strong>
                        <small>{eventMeta?.(event) ?? (event.detectionKind === 'Ocr' ? `OCR · ${event.type}` : event.type)}</small>
                      </span>
                    </Button>
                  ) : (
                    <span className="training-tree-event-main">
                      <span className="training-event-swatch">
                        {event.detectionKind === 'Ocr' ? 'OCR' : event.classId}
                      </span>
                      <span className="training-tree-event-copy">
                        <strong>{event.name}</strong>
                        <small>{eventMeta?.(event) ?? (event.detectionKind === 'Ocr' ? `OCR · ${event.type}` : event.type)}</small>
                      </span>
                    </span>
                  )}
                  <div className="training-tree-event-actions">
                    {event.fixedPosition && onAddFixedLabel && (
                      <Button
                        variant="ghost"
                        size="small"
                        aria-label={`Add fixed label for ${event.name}`}
                        onClick={() => onAddFixedLabel(event)}
                        disabled={canAddFixedLabel ? !canAddFixedLabel(event) : false}
                      >+</Button>
                    )}
                    <Button variant="ghost" size="small" onClick={() => onEdit(event)}>Edit</Button>
                    <Button variant="ghost" size="small" onClick={() => onRegion(event)}>Region</Button>
                    {onDelete && <Button variant="ghost" size="small" onClick={() => onDelete(event)}>Delete</Button>}
                  </div>
                </div>
              ))}
            </div>
          </section>
        );
      })}
    </div>
  );
}
