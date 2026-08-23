// SPDX-License-Identifier: GPL-2.0-or-later
//
// jsdom ships no matchMedia, so anything rendering a component that asks about viewport width
// throws on import. The compact-layout hook needs one; this stub reports "not compact", which makes
// the full-size player route the default under test. A test wanting the compact branch overrides
// window.matchMedia for its own case.

window.matchMedia = ((query: string): MediaQueryList => ({
  matches: false,
  media: query,
  onchange: null,
  addEventListener: () => {},
  removeEventListener: () => {},
  addListener: () => {},
  removeListener: () => {},
  dispatchEvent: () => false,
})) as typeof window.matchMedia;

// jsdom does not implement media playback, but player tests exercise pause transitions as part of
// normal UI behavior. Keep the test output focused on real failures instead of repeating jsdom's
// "Not implemented" warning for every player render.
HTMLMediaElement.prototype.pause = () => {};
HTMLMediaElement.prototype.play = () => Promise.resolve();
