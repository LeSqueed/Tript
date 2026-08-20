import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { TripwireMark } from './TripwireMark';

describe('TripwireMark', () => {
  it('renders as decorative by default', () => {
    const { container } = render(<TripwireMark />);
    const mark = container.querySelector('svg');
    expect(mark?.getAttribute('aria-hidden')).toBe('true');
    expect(mark?.classList.contains('tripwire-mark')).toBe(true);
  });

  it('provides an accessible label when used as an identity mark', () => {
    render(<TripwireMark label="Tript" />);
    expect(screen.getByRole('img', { name: 'Tript' })).toBeTruthy();
  });

  it('accepts a custom display size', () => {
    const { container } = render(<TripwireMark size={48} />);
    expect(container.querySelector('svg')?.getAttribute('width')).toBe('48');
    expect(container.querySelectorAll('.tripwire-viewfinder')).toHaveLength(1);
    expect(container.querySelector('.tripwire-event-dot')).toBeNull();
  });
});
