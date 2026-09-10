// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Settings;

namespace Tript.Recorder;

public interface IAudioRoutedSource
{
}

public interface IAudioTrackEncoder
{
}

public interface IAudioRoutingSink
{
    IAudioRoutedSource CreateCaptureSource(AudioSourceKind kind, string name, string? deviceId);

    void RouteSourceToMixer(IAudioRoutedSource source, int mixerIndex);

    void SetSourceVolume(IAudioRoutedSource source, float volume);

    void ActivateSource(IAudioRoutedSource source);

    void DeactivateSource(IAudioRoutedSource source);

    IAudioTrackEncoder CreateTrackEncoder(int mixerIndex, string name);

    void AssignEncoderToSlot(IAudioTrackEncoder encoder, int outputSlot);
}
