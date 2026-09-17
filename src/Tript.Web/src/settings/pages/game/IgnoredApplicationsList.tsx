// SPDX-License-Identifier: GPL-2.0-or-later

import { useDeferredValue, useState } from 'react';
import { Button, TextField } from '../../../components/ui/controls';

export function IgnoredApplicationsList({
  ignoredApplications,
  onRemove,
}: {
  ignoredApplications: string[];
  onRemove: (index: number) => void;
}) {
  const [filter, setFilter] = useState('');
  const deferredFilter = useDeferredValue(filter.trim().toLowerCase());
  const visible = ignoredApplications
    .map((executablePath, index) => ({ executablePath, index }))
    .filter(({ executablePath }) => executablePath.toLowerCase().includes(deferredFilter));

  return (
    <details className="settings-advanced ignored-applications">
      <summary>
        Ignored applications
        <span className="settings-advanced-marker">{ignoredApplications.length}</span>
      </summary>
      <div className="settings-advanced-body">
        <p className="muted small">These applications will not be suggested as custom games.</p>
        {ignoredApplications.length === 0 ? (
          <p className="muted small">No ignored applications.</p>
        ) : (
          <>
            <label className="field ignored-application-filter">
              <span className="field-label">Filter ignored applications</span>
              <TextField
                value={filter}
                onChange={setFilter}
                placeholder="Name or executable path"
              />
            </label>
            {visible.length === 0 ? (
              <p className="muted small">No ignored applications match this filter.</p>
            ) : (
              <div className="ignored-application-list">
                {visible.map(({ executablePath, index }) => (
                  <div className="ignored-application-row" key={`${executablePath}:${index}`}>
                    <div className="ignored-application-details">
                      <strong>{applicationName(executablePath)}</strong>
                      <code>{executablePath}</code>
                    </div>
                    <Button
                      variant="ghost"
                      onClick={() => onRemove(index)}
                      aria-label={`Remove ${applicationName(executablePath)} from ignored applications`}
                    >
                      Remove
                    </Button>
                  </div>
                ))}
              </div>
            )}
          </>
        )}
      </div>
    </details>
  );
}

function applicationName(path: string): string {
  return path.split(/[\\/]/).filter(Boolean).at(-1) || path;
}
