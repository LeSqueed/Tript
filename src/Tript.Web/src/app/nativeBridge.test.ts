import { describe, expect, it, vi } from 'vitest';
import { installPhotinoBridge } from './nativeBridge';

describe('Photino bridge', () => {
  it('translates the native Settings command into app navigation', () => {
    let receive: ((message: string) => void) | undefined;
    const onNavigate = vi.fn();
    window.addEventListener('tript:navigate', onNavigate);

    installPhotinoBridge({
      receiveMessage: (handler) => {
        receive = handler;
      },
    });
    receive?.('tript:navigate:settings');

    expect(onNavigate).toHaveBeenCalledOnce();
    expect(onNavigate.mock.calls[0][0]).toMatchObject({ detail: 'settings' });
    window.removeEventListener('tript:navigate', onNavigate);
  });
});
