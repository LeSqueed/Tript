// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.App.Content;

internal readonly record struct VolumeSpace(string RootPath, long FreeBytes, long TotalBytes);

internal interface IStorageProbe
{
    VolumeSpace? Measure(string path);
}

internal sealed class DriveInfoStorageProbe : IStorageProbe
{
    public VolumeSpace? Measure(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            var drive = new DriveInfo(Path.GetFullPath(path));
            if (!drive.IsReady)
                return null;

            return new VolumeSpace(drive.RootDirectory.FullName, drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                             or ArgumentException or NotSupportedException
                                             or PathTooLongException)
        {
            return null;
        }
    }
}
