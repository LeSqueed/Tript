// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.InteropServices;

namespace Tript.Settings;

// Enumerates the machine's active WASAPI endpoints into the settings model's AudioDeviceSetting
// list — the source of audio.devices, and what lets the routing attach a real microphone or
// speaker to a track. It is pure P/Invoke over the Core Audio COM surface
// (IMMDeviceEnumerator, IMMDeviceCollection, IMMDevice, IMMEndpoint, IPropertyStore; no external
// package): CoCreateInstance for CLSID_MMDeviceEnumerator, one data flow per call (eRender for
// outputs, eCapture for inputs), only STATE_ACTIVE endpoints, the friendly name from
// PKEY_Device_FriendlyName, and IMMDevice::GetId as the device id. That id string is exactly what
// the win-wasapi plugin's "device_id" property consumes (ObsAudioRoutingSink), so a saved
// selection reaches the capture source unchanged.
//
// The layer stays testable on a machine with no audio at all: non-Windows returns an empty list,
// and so does any COM failure — a device list is optional surface and must not take settings
// down with it.
public static class WasapiDeviceEnumerator
{
    // Inputs (mics and other capture devices): the eCapture data flow.
    public static IReadOnlyList<AudioDeviceSetting> EnumerateInputDevices() => Enumerate(DataFlow.Capture);

    // Outputs (speakers and other render devices): the eRender data flow.
    public static IReadOnlyList<AudioDeviceSetting> EnumerateOutputDevices() => Enumerate(DataFlow.Render);

    // The routing-side seam: a source kind maps to the data flow that captures it.
    public static IReadOnlyList<AudioDeviceSetting> Enumerate(AudioSourceKind kind) =>
        Enumerate(kind == AudioSourceKind.Input ? DataFlow.Capture : DataFlow.Render);

    private static IReadOnlyList<AudioDeviceSetting> Enumerate(DataFlow dataFlow)
    {
        if (!OperatingSystem.IsWindows())
            return [];

        // The CLR initializes COM only on the main thread; the settings push runs on an IPC
        // thread, where CoCreateInstance of the enumerator would otherwise fail with
        // CO_E_NOTINITIALIZED and this whole call would silently come back empty. Initialize the
        // calling thread's apartment here, and only uninitialize when this call was the one that
        // initialized it (S_OK): a thread already in an apartment (S_FALSE, or RPC_E_CHANGED_MODE
        // for a different mode) is left as it was found.
        var initResult = CoInitializeEx(IntPtr.Zero, CoInitializeFlagsMultithreaded);
        var initializedHere = initResult == 0;

        // Every RCW the enumeration creates, released once the device list is materialized. The
        // enumerator graph is a tree of separate COM objects, and dropping only the enumerator
        // would leak the collection, every device, its endpoint and its property store.
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

                // The data flow comes from the device itself rather than the enumerator's request,
                // so a device that reports the wrong flow is skipped rather than mislabelled.
                var endpoint = (IMMEndpoint)device;
                comObjects.Add(endpoint);
                endpoint.GetDataFlow(out var endpointFlow);
                if (endpointFlow != (int)dataFlow)
                    continue;

                devices.Add(new AudioDeviceSetting
                {
                    Id = device.GetId(),
                    Name = FriendlyName(device, comObjects),
                    Direction = dataFlow == DataFlow.Render ? AudioSourceKind.Output : AudioSourceKind.Input
                });
            }

            return devices;
        }
        catch (COMException)
        {
            // No audio service, no endpoint, a device that vanished mid-enumeration — none of it
            // is worth failing settings over.
            return [];
        }
        finally
        {
            foreach (var comObject in comObjects)
                Marshal.ReleaseComObject(comObject);
            if (initializedHere)
                CoUninitialize();
        }
    }

    // PKEY_Device_FriendlyName, read through the device's property store. The name is the
    // human-readable label the settings UI shows next to the saved id.
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
            // The string is CoTaskMem-allocated inside the PROPVARIANT; only the caller can free it.
            PropVariantClear(ref value);
        }
    }

    // ---- Core Audio COM surface (mmdeviceapi.h) ----

    private const uint DeviceFriendlyNamePid = 14;

    private static readonly Guid DeviceFriendlyNameFmtId = new("A45C254E-DF1C-4EFD-8020-67D146A850E0");

    private enum DataFlow
    {
        Render = 0,
        Capture = 1
    }

    // DEVICE_STATE_ACTIVE; the only state a capture source can attach to.
    private enum DeviceState
    {
        Active = 0x00000001
    }

    // STGM_READ, the access mode IPropertyStore::OpenPropertyStore takes.
    private enum StorageMode
    {
        Read = 0
    }

    // CLSID_MMDeviceEnumerator; instantiating the [ComImport] class performs CoCreateInstance.
    // Not sealed: the compiler's class→interface conversion for COM classes, which is what the
    // cast in Enumerate relies on, requires a non-sealed source class.
    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MmDeviceEnumeratorClass
    {
    }

    // Vtable slots after IUnknown: 3 EnumAudioEndpoints, 4 GetDefaultAudioEndpoint, 5 GetDevice,
    // 6 RegisterEndpointNotificationCallback, 7 UnregisterEndpointNotificationCallback.
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

    // Vtable slots after IUnknown: 3 GetCount, 4 Item.
    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        uint GetCount();

        IMMDevice Item(uint index);
    }

    // Vtable slots after IUnknown: 3 Activate, 4 OpenPropertyStore, 5 GetId, 6 GetState.
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

    // Vtable slot after IUnknown: 3 GetDataFlow. IID_IMMEndpoint from mmdeviceapi.h; the
    // QueryInterface for this interface fails (E_NOINTERFACE) when the IID is wrong, which
    // surfaces as an InvalidCastException on the RCW cast in Enumerate.
    [ComImport]
    [Guid("1BE09788-6894-4089-8586-9A2A6C265AC5")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMEndpoint
    {
        void GetDataFlow(out int dataFlow);
    }

    // Vtable slots after IUnknown: 3 GetCount, 4 GetAt, 5 GetValue, 6 SetValue, 7 Commit.
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

    // PROPERTYKEY: the format-id/pid pair that names a property (PKEY_Device_FriendlyName).
    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid fmtid;
        public uint pid;
    }

    // The PROPVARIANT surface GetValue writes into. The layout is the head of the native union —
    // the VARTYPE plus a single pointer that carries the string for VT_LPWSTR, which is what
    // PKEY_Device_FriendlyName yields. The string must be freed with PropVariantClear.
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public nint value;
    }

    // Frees whatever the PROPVARIANT holds (CoTaskMemFree for the friendly-name string).
    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int PropVariantClear(ref PropVariant value);

    // COINIT values for CoInitializeEx: MTA is 0x0.
    private const uint CoInitializeFlagsMultithreaded = 0x0;

    // Initializes COM on the calling thread. HRESULT: S_OK (0) when this call initialized the
    // apartment, S_FALSE (1) when it was already initialized, RPC_E_CHANGED_MODE when a different
    // apartment mode is already in force.
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(nint reserved, uint dwCoInit);

    // Uninitializes COM on the calling thread; only legal when this thread's CoInitializeEx
    // succeeded, and matched one-for-one with that call.
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();
}
