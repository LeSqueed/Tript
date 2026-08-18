// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Obs;
using Tript.Settings;

namespace Tript.Recorder;

// The recorder's world: an existing OBS runtime, the source to record, and the ability to build the
// output that writes the recording. The session owns the runtime and the source (the app's), and is
// the factory for the output — the recorder owns whatever output the session hands back.
public interface IRecorderSession : IDisposable
{
    // Builds the output for a recording described by the given settings. The returned output is
    // wired — encoders created, bound to the runtime's mixes, attached to the output — and is the
    // caller's to own and dispose.
    IRecorderOutput CreateOutput(ResolvedRecorderSettings settings);

    // Puts the session's source on the recording channel so the started output has something to
    // write. Called by the recorder just before an output starts.
    void PlaceSourceOnChannel();

    // Clears the source from the recording channel. Called by the recorder after an output ends,
    // so a source that is no longer being recorded stops being rendered into the channel.
    void ClearSourceFromChannel();

    // The session owns a reference to the borrowed source; disposing drops it.
    new void Dispose();
}
