// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.InteropServices;

namespace Tript.Settings;

public static class WasapiDeviceEnumerator
{
    public static IReadOnlyList<AudioDeviceSetting> EnumerateInputDevices() => Enumerate(DataFlow.Capture);

    public static IReadOnlyList<AudioDeviceSetting> EnumerateOutputDevices() => Enumerate(DataFlow.Render);

    public static IReadOnlyList<AudioDeviceSetting> Enumerate(AudioSourceKind kind) =>
        Enumerate(kind == AudioSourceKind.Input ? DataFlow.Capture : DataFlow.Render);

    public static bool TryEnumerate(AudioSourceKind kind, out IReadOnlyList<AudioDeviceSetting> devices) =>
        TryEnumerate(kind == AudioSourceKind.Input ? DataFlow.Capture : DataFlow.Render, out devices);

    public static bool TryEnumerateDevices(out IReadOnlyList<AudioDeviceSetting> devices)
    {
        if (!TryEnumerate(DataFlow.Capture, out var inputs) ||
            !TryEnumerate(DataFlow.Render, out var outputs))
        {
            devices = [];
            return false;
        }

        devices = inputs.Concat(outputs).ToList();
        return true;
    }

    private static IReadOnlyList<AudioDeviceSetting> Enumerate(DataFlow dataFlow) =>
        TryEnumerate(dataFlow, out var devices) ? devices : [];

    private static bool TryEnumerate(DataFlow dataFlow, out IReadOnlyList<AudioDeviceSetting> result)
    {
        if (!OperatingSystem.IsWindows())
        {
            result = [];
            return true;
        }

        var initResult = CoInitializeEx(IntPtr.Zero, CoInitializeFlagsMultithreaded);
        var initializedHere = initResult == 0;

        var comObjects = new List<object>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MmDeviceEnumeratorClass();
            comObjects.Add(enumerator);

            var collection = enumerator.EnumAudioEndpoints((int)dataFlow, (uint)DeviceState.Active);
            comObjects.Add(collection);

            var devices = new List<AudioDeviceSetting>();
            var count = collection.GetCount();
            for (uint index = 0; index < count; index++)
            {
                var device = collection.Item(index);
                comObjects.Add(device);

                int endpointFlow;
                try
                {
                    var endpoint = (IMMEndpoint)device;

                    if (!ReferenceEquals(endpoint, device))
                        comObjects.Add(endpoint);

                    endpoint.GetDataFlow(out endpointFlow);
                }
                catch (InvalidCastException)
                {
                    continue;
                }

                if (endpointFlow != (int)dataFlow)
                    continue;

                devices.Add(new AudioDeviceSetting
                {
                    Id = device.GetId(),
                    Name = FriendlyName(device, comObjects),
                    Direction = dataFlow == DataFlow.Render ? AudioSourceKind.Output : AudioSourceKind.Input
                });
            }

            result = devices;
            return true;
        }
        catch (COMException)
        {
            result = [];
            return false;
        }
        finally
        {
            foreach (var comObject in comObjects)
                Marshal.ReleaseComObject(comObject);
            if (initializedHere)
                CoUninitialize();
        }
    }

    private static string FriendlyName(IMMDevice device, List<object> comObjects)
    {
        var store = device.OpenPropertyStore((uint)StorageMode.Read);
        comObjects.Add(store);

        var key = new PropertyKey { fmtid = DeviceFriendlyNameFmtId, pid = DeviceFriendlyNamePid };
        var value = new PropVariant();
        store.GetValue(ref key, out value);

        try
        {
            return value.vt == (ushort)VarEnum.VT_LPWSTR && value.value != nint.Zero
                ? Marshal.PtrToStringUni(value.value) ?? string.Empty
                : string.Empty;
        }
        finally
        {
            PropVariantClear(ref value);
        }
    }

    private const uint DeviceFriendlyNamePid = 14;

    private static readonly Guid DeviceFriendlyNameFmtId = new("A45C254E-DF1C-4EFD-8020-67D146A850E0");

    private enum DataFlow
    {
        Render = 0,
        Capture = 1
    }

    private enum DeviceState
    {
        Active = 0x00000001
    }

    private enum StorageMode
    {
        Read = 0
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MmDeviceEnumeratorClass
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        IMMDeviceCollection EnumAudioEndpoints(int dataFlow, uint stateMask);

        void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);

        IMMDevice GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id);

        void RegisterEndpointNotificationCallback(IntPtr callback);

        void UnregisterEndpointNotificationCallback(IntPtr callback);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        uint GetCount();

        IMMDevice Item(uint index);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        void Activate([MarshalAs(UnmanagedType.LPStruct)] Guid iid, uint classContext, IntPtr activationParams, out IntPtr interfacePtr);

        IPropertyStore OpenPropertyStore(uint storageMode);

        [return: MarshalAs(UnmanagedType.LPWStr)]
        string GetId();

        uint GetState();
    }

    [ComImport]
    [Guid("1BE09788-6894-4089-8586-9A2A6C265AC5")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMEndpoint
    {
        void GetDataFlow(out int dataFlow);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        uint GetCount();

        void GetAt(uint index, out PropertyKey key);

        void GetValue(ref PropertyKey key, out PropVariant value);

        void SetValue(ref PropertyKey key, ref PropVariant value);

        void Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public nint value;
    }

    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int PropVariantClear(ref PropVariant value);

    private const uint CoInitializeFlagsMultithreaded = 0x0;

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(nint reserved, uint dwCoInit);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();
}
