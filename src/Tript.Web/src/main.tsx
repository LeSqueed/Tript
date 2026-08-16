// SPDX-License-Identifier: GPL-2.0-or-later

import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './app/App';
import './theme/theme.css';
import './app/app.css';
import './components/RecorderBar.css';
import './components/LibraryView.css';
import './components/PlayerView.css';
import './components/player/clipDialog.css';
import './components/SettingsView.css';

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
