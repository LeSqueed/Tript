// SPDX-License-Identifier: GPL-2.0-or-later

import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './app/App';
import { ErrorBoundary } from './app/ErrorBoundary';
import { installGlobalErrorHandlers } from './app/errorReporting';
import { installPhotinoBridge, type PhotinoExternal } from './app/nativeBridge';
import './theme/theme.css';
import './theme/utilities.css';
import './app/app.css';
import './components/ui/controls.css';
import './components/ui/ui.css';
import './components/ui/toast/toast.css';
import './components/RecorderBar.css';
import './components/LibraryView.css';
import './components/PlayerView.css';
import './components/player/clipDialog.css';
import './components/trash/trashList.css';
import './components/SettingsView.css';
import './components/StreamerView.css';
import './components/storage/storage.css';
import './components/TrainingView.css';

installGlobalErrorHandlers();

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <ErrorBoundary>
      <App />
    </ErrorBoundary>
  </StrictMode>,
);

type PhotinoWindow = Window & {
  external?: PhotinoExternal & {
    sendMessage?: (message: string) => void;
  };
};

const photinoWindow = window as PhotinoWindow;
installPhotinoBridge(photinoWindow.external);
