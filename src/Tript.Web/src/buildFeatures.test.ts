import { describe, expect, it } from 'vitest';
import { trainingEnabled } from './buildFeatures';

describe('build features', () => {
  it('matches the build-time training switch', () => {
    expect(trainingEnabled).toBe(import.meta.env.VITE_TRIPT_TRAINING === 'true');
  });
});
