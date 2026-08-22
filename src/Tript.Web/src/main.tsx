// SPDX-License-Identifier: GPL-2.0-or-later

import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './app/App';
import './theme/theme.css';
import './theme/utilities.css';
import './app/app.css';
import './components/ui/controls.css';
import './components/ui/ui.css';
import './components/RecorderBar.css';
// LibraryView.css also carries the shared primitives (.panel/.btn/.muted) and the player overlay's
// chrome — the overlay is the library's own way of showing the player, so its styles live with it.
import './components/LibraryView.css';
import './components/PlayerView.css';
import './components/player/clipDialog.css';
import './components/trash/trashList.css';
import './components/SettingsView.css';
import './components/ErrorBanner.css';

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);

// Photino uses this bridge to tell the native shell that the document and React tree actually ran.
// The shell waits for this before hiding/minimizing a tray launch; hiding the native window earlier
// can prevent WebView2 from creating its renderer and leaves a white window on later restore.
type PhotinoWindow = Window & {
  external?: {
    sendMessage?: (message: string) => void;
  };
};

(window as PhotinoWindow).external?.sendMessage?.('tript:ready');
