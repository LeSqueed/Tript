// SPDX-License-Identifier: GPL-2.0-or-later

import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './app/App';
import './theme/theme.css';
import './app/app.css';
import './components/RecorderBar.css';
// LibraryView.css also carries the shared primitives (.panel/.btn/.muted) and the player overlay's
// chrome — the overlay is the library's own way of showing the player, so its styles live with it.
import './components/LibraryView.css';
import './components/PlayerView.css';
import './components/player/clipDialog.css';
import './components/SettingsView.css';
import './components/ErrorBanner.css';

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
