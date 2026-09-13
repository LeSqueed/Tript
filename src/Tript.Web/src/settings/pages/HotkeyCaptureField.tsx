// SPDX-License-Identifier: GPL-2.0-or-later

import { useEffect, useState } from 'react';
import type { HotkeyBinding } from '../settingsModel';
import { Button } from '../../components/ui/controls';

const MODIFIER_CODES = new Set([
  'ControlLeft', 'ControlRight', 'ShiftLeft', 'ShiftRight', 'AltLeft', 'AltRight', 'MetaLeft', 'MetaRight',
]);

function describeKey(code: string): string {
  if (code.startsWith('Key')) return code.slice(3);
  if (code.startsWith('Digit')) return code.slice(5);
  if (code.startsWith('Numpad')) return `Num ${code.slice(6)}`;
  return code;
}

function formatBinding(binding: HotkeyBinding): string {
  if (!binding.key) {
    return 'Unbound';
  }
  return [...binding.modifiers, describeKey(binding.key)].join(' + ');
}

export function HotkeyCaptureField({
  binding,
  onChange,
  'aria-label': ariaLabel,
}: {
  binding: HotkeyBinding;
  onChange: (binding: HotkeyBinding) => void;
  'aria-label'?: string;
}) {
  const [recording, setRecording] = useState(false);

  useEffect(() => {
    if (!recording) {
      return;
    }

    function onKeyDown(event: KeyboardEvent) {
      event.preventDefault();
      event.stopPropagation();

      if (event.code === 'Escape') {
        setRecording(false);
        return;
      }
      if (event.code === 'Backspace' || event.code === 'Delete') {
        onChange({ modifiers: [], key: null });
        setRecording(false);
        return;
      }
      if (MODIFIER_CODES.has(event.code)) {
        return;
      }

      const modifiers: string[] = [];
      if (event.ctrlKey) modifiers.push('Control');
      if (event.shiftKey) modifiers.push('Shift');
      if (event.altKey) modifiers.push('Alt');
      if (event.metaKey) modifiers.push('Win');

      onChange({ modifiers, key: event.code });
      setRecording(false);
    }

    window.addEventListener('keydown', onKeyDown, true);
    return () => window.removeEventListener('keydown', onKeyDown, true);
  }, [recording, onChange]);

  return (
    <Button
      variant="ghost"
      className={recording ? 'hotkey-capture is-recording' : 'hotkey-capture'}
      onClick={() => setRecording(true)}
      aria-label={ariaLabel}
      title="Click, then press a key combination. Escape cancels, Backspace clears."
    >
      {recording ? 'Press a key…' : formatBinding(binding)}
    </Button>
  );
}
