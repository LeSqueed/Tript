// SPDX-License-Identifier: GPL-2.0-or-later

import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { Button, Checkbox, SelectField, Slider } from './controls';

afterEach(cleanup);

describe('pointer focus release', () => {
  it('releases a pointer-activated button without affecting keyboard focus', () => {
    render(<Button onClick={vi.fn()}>Action</Button>);
    const button = screen.getByRole('button', { name: 'Action' });

    button.focus();
    fireEvent.pointerUp(button);
    expect(document.activeElement).not.toBe(button);

    button.focus();
    fireEvent.click(button);
    expect(document.activeElement).toBe(button);
  });

  it('releases sliders and checkboxes after pointer interaction', () => {
    render(
      <>
        <Slider aria-label="Volume" value={0.5} onChange={vi.fn()} />
        <Checkbox aria-label="Favorite" checked={false} onChange={vi.fn()} />
      </>,
    );
    const slider = screen.getByLabelText('Volume');
    const checkbox = screen.getByLabelText('Favorite');

    slider.focus();
    fireEvent.pointerUp(slider);
    expect(document.activeElement).not.toBe(slider);

    checkbox.focus();
    fireEvent.pointerUp(checkbox);
    expect(document.activeElement).not.toBe(checkbox);
  });

  it('releases a pointer-used select only after its choice is made', () => {
    render(
      <SelectField
        aria-label="Playback speed"
        value="1"
        options={[{ value: '1', label: '1x' }, { value: '2', label: '2x' }]}
        onChange={vi.fn()}
      />,
    );
    const select = screen.getByLabelText('Playback speed');
    select.focus();
    fireEvent.pointerDown(select);
    expect(document.activeElement).toBe(select);
    fireEvent.change(select, { target: { value: '2' } });
    expect(document.activeElement).not.toBe(select);
  });
});
