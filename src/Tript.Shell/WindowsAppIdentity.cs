// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.InteropServices;
using System.Text;

namespace Tript.Shell;

// Windows resolves a running app's taskbar, jump list and notification identity through its
// AppUserModelID (set in Program.cs via NotificationRegistrationId) by looking for a Start Menu
// shortcut whose own AUMID property matches. When no such shortcut exists, Windows invents a
// placeholder entry instead, which can end up bound to a stale install path and renders the
// generic "no icon" placeholder on the taskbar no matter what the window itself reports through
// WM_SETICON.
//
// Tript ships as a portable zip with no installer, so nothing else ever creates that shortcut -
// the app has to keep its own current on each launch. This is the same thing Electron's
// setAppUserModelId helper and Discord's installer both do, and for the same reason.
internal static class WindowsAppIdentity
{
    internal const string AppUserModelId = "Tript";

    private static PropertyKey _appUserModelIdKey = new()
    {
        FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        PropertyId = 5,
    };

    // Rewrites the shortcut unconditionally rather than reading it back to compare first: a few COM
    // calls once per launch are cheap, and comparing would mean a second, more fragile read path
    // through the same interfaces purely to decide whether to write.
    internal static void EnsureStartMenuShortcut(string targetExecutablePath, string? iconPath)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(targetExecutablePath))
            return;

        try
        {
            var programsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            if (string.IsNullOrEmpty(programsDirectory))
                return;

            Directory.CreateDirectory(programsDirectory);
            var shortcutPath = Path.Combine(programsDirectory, "Tript.lnk");

            var link = (IShellLinkW)new ShellLink();
            link.SetPath(targetExecutablePath);
            link.SetWorkingDirectory(Path.GetDirectoryName(targetExecutablePath) ?? string.Empty);
            link.SetIconLocation(
                string.IsNullOrWhiteSpace(iconPath) ? targetExecutablePath : iconPath, 0);

            var propertyStore = (IPropertyStore)link;
            var value = PropVariant.FromString(AppUserModelId);
            try
            {
                propertyStore.SetValue(ref _appUserModelIdKey, ref value);
                propertyStore.Commit();
            }
            finally
            {
                value.Clear();
            }

            ((IPersistFile)link).Save(shortcutPath, true);
        }
        catch (Exception exception) when (exception is COMException or UnauthorizedAccessException
            or IOException or InvalidCastException)
        {
            Console.Error.WriteLine(
                $"Tript.Shell: could not update the Start Menu shortcut: {exception.Message}");
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    // Every method has to be declared, in this exact order, even the ones never called: COM
    // dispatch is by vtable slot, so omitting one would silently shift every method after it.
    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath,
            IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath,
            int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName,
            [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    [ComImport]
    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint propertyCount);
        void GetAt(uint propertyIndex, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public int PropertyId;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] private ushort _valueType;
        [FieldOffset(8)] private IntPtr _pointerValue;

        internal static PropVariant FromString(string value) => new()
        {
            _valueType = 31, // VT_LPWSTR
            _pointerValue = Marshal.StringToCoTaskMemUni(value),
        };

        internal void Clear() => PropVariantClear(ref this);

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant propertyVariant);
    }
}
