export type PhotinoExternal = {
  receiveMessage?: (handler: (message: string) => void) => void;
};

export function installPhotinoBridge(external: PhotinoExternal | undefined): void {
  external?.receiveMessage?.((message) => {
    if (message === 'tript:navigate:settings') {
      window.dispatchEvent(new CustomEvent('tript:navigate', { detail: 'settings' }));
    }
  });
}
