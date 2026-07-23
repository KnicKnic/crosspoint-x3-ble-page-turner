using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace X3LaptopCompanion
{
    public sealed class MediaStatusSensor
    {
        private const string WebcamConsentStoreKey =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam";

        public CompanionTriState GetMicrophoneState(IReadOnlyCollection<int> processIds)
        {
            try
            {
                return IsCaptureSessionActive(processIds) ? CompanionTriState.On : CompanionTriState.Off;
            }
            catch (Exception ex)
            {
                HostLog.Write("WASAPI microphone detection failed: " + ex.Message);
                return CompanionTriState.Unknown;
            }
        }

        public CompanionTriState GetCameraState()
        {
            try
            {
                return IsWebcamInUse() ? CompanionTriState.On : CompanionTriState.Off;
            }
            catch (Exception ex)
            {
                HostLog.Write("Webcam registry detection failed: " + ex.Message);
                return CompanionTriState.Unknown;
            }
        }

        private static bool IsWebcamInUse()
        {
            using (var key = Registry.LocalMachine.OpenSubKey(WebcamConsentStoreKey))
            {
                if (CheckRegForWebcamInUse(key))
                {
                    return true;
                }
            }

            using (var key = Registry.CurrentUser.OpenSubKey(WebcamConsentStoreKey))
            {
                return CheckRegForWebcamInUse(key);
            }
        }

        private static bool CheckRegForWebcamInUse(RegistryKey key)
        {
            if (key == null)
            {
                return false;
            }

            foreach (var subKeyName in key.GetSubKeyNames())
            {
                if (subKeyName == "NonPackaged")
                {
                    using (var nonPackagedKey = key.OpenSubKey(subKeyName))
                    {
                        if (nonPackagedKey == null)
                        {
                            continue;
                        }

                        foreach (var nonPackagedSubKeyName in nonPackagedKey.GetSubKeyNames())
                        {
                            using (var subKey = nonPackagedKey.OpenSubKey(nonPackagedSubKeyName))
                            {
                                if (HasActiveLastUsedTimeStop(subKey))
                                {
                                    return true;
                                }
                            }
                        }
                    }
                }
                else
                {
                    using (var subKey = key.OpenSubKey(subKeyName))
                    {
                        if (HasActiveLastUsedTimeStop(subKey))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private static bool HasActiveLastUsedTimeStop(RegistryKey key)
        {
            if (key == null || !key.GetValueNames().Contains("LastUsedTimeStop"))
            {
                return false;
            }

            return key.GetValue("LastUsedTimeStop") is long endTime && endTime <= 0;
        }

        private static bool IsCaptureSessionActive(IReadOnlyCollection<int> processIds)
        {
            var processSet = new HashSet<int>(processIds ?? Array.Empty<int>());
            object enumeratorObject = null;
            IMMDeviceCollection devices = null;

            try
            {
                enumeratorObject = new MMDeviceEnumerator();
                var enumerator = (IMMDeviceEnumerator)enumeratorObject;
                Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(EDataFlow.Capture, DeviceState.Active, out devices));
                Marshal.ThrowExceptionForHR(devices.GetCount(out var deviceCount));

                for (uint i = 0; i < deviceCount; i++)
                {
                    IMMDevice device = null;
                    object sessionManagerObject = null;

                    try
                    {
                        Marshal.ThrowExceptionForHR(devices.Item(i, out device));
                        var sessionManagerId = typeof(IAudioSessionManager2).GUID;
                        Marshal.ThrowExceptionForHR(device.Activate(ref sessionManagerId, ClsCtxAll, IntPtr.Zero, out sessionManagerObject));

                        if (SessionManagerHasActiveCapture((IAudioSessionManager2)sessionManagerObject, processSet))
                        {
                            return true;
                        }
                    }
                    finally
                    {
                        ReleaseComObject(sessionManagerObject);
                        ReleaseComObject(device);
                    }
                }

                return false;
            }
            finally
            {
                ReleaseComObject(devices);
                ReleaseComObject(enumeratorObject);
            }
        }

        private static bool SessionManagerHasActiveCapture(IAudioSessionManager2 sessionManager, HashSet<int> processIds)
        {
            IAudioSessionEnumerator sessionEnumerator = null;

            try
            {
                Marshal.ThrowExceptionForHR(sessionManager.GetSessionEnumerator(out sessionEnumerator));
                Marshal.ThrowExceptionForHR(sessionEnumerator.GetCount(out var sessionCount));

                for (var i = 0; i < sessionCount; i++)
                {
                    IAudioSessionControl sessionControl = null;

                    try
                    {
                        Marshal.ThrowExceptionForHR(sessionEnumerator.GetSession(i, out sessionControl));
                        Marshal.ThrowExceptionForHR(sessionControl.GetState(out var state));
                        if (state != AudioSessionState.Active)
                        {
                            continue;
                        }

                        if (processIds.Count == 0)
                        {
                            return true;
                        }

                        var sessionControl2 = sessionControl as IAudioSessionControl2;
                        if (sessionControl2 == null)
                        {
                            continue;
                        }

                        Marshal.ThrowExceptionForHR(sessionControl2.GetProcessId(out var processId));
                        if (processIds.Contains((int)processId) || SessionDisplayNameLooksLikeTeams(sessionControl))
                        {
                            return true;
                        }
                    }
                    finally
                    {
                        ReleaseComObject(sessionControl);
                    }
                }

                return false;
            }
            finally
            {
                ReleaseComObject(sessionEnumerator);
            }
        }

        private static void ReleaseComObject(object value)
        {
            if (value != null && Marshal.IsComObject(value))
            {
                Marshal.ReleaseComObject(value);
            }
        }

        private static bool SessionDisplayNameLooksLikeTeams(IAudioSessionControl sessionControl)
        {
            IntPtr displayNamePtr = IntPtr.Zero;

            try
            {
                Marshal.ThrowExceptionForHR(sessionControl.GetDisplayName(out displayNamePtr));
                var displayName = Marshal.PtrToStringUni(displayNamePtr) ?? string.Empty;
                return displayName.IndexOf("Teams", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       displayName.IndexOf("ms-teams", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (displayNamePtr != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(displayNamePtr);
                }
            }
        }

        private const uint ClsCtxAll = 23;

        private enum EDataFlow
        {
            Render,
            Capture,
            All
        }

        private enum ERole
        {
            Console,
            Multimedia,
            Communications
        }

        [Flags]
        private enum DeviceState : uint
        {
            Active = 0x00000001
        }

        private enum AudioSessionState
        {
            Inactive = 0,
            Active = 1,
            Expired = 2
        }

        [ComImport]
        [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumerator
        {
        }

        [ComImport]
        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints(EDataFlow dataFlow, DeviceState dwStateMask, out IMMDeviceCollection ppDevices);
            int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppEndpoint);
            int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
            int RegisterEndpointNotificationCallback(IntPtr pClient);
            int UnregisterEndpointNotificationCallback(IntPtr pClient);
        }

        [ComImport]
        [Guid("0BD7A1BE-7A1A-44DB-8397-C0F5B184F278")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceCollection
        {
            int GetCount(out uint pcDevices);
            int Item(uint nDevice, out IMMDevice ppDevice);
        }

        [ComImport]
        [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            int Activate(ref Guid iid, uint dwClsCtx, IntPtr pActivationParams,
                [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
            int OpenPropertyStore(uint stgmAccess, IntPtr ppProperties);
            int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
            int GetState(out DeviceState pdwState);
        }

        [ComImport]
        [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionManager2
        {
            int GetAudioSessionControl(IntPtr audioSessionGuid, uint streamFlags, out IAudioSessionControl sessionControl);
            int GetSimpleAudioVolume(IntPtr audioSessionGuid, uint streamFlags, out IntPtr audioVolume);
            int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
            int RegisterSessionNotification(IntPtr sessionNotification);
            int UnregisterSessionNotification(IntPtr sessionNotification);
            int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr duckNotification);
            int UnregisterDuckNotification(IntPtr duckNotification);
        }

        [ComImport]
        [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionEnumerator
        {
            int GetCount(out int sessionCount);
            int GetSession(int sessionCount, out IAudioSessionControl session);
        }

        [ComImport]
        [Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl
        {
            int GetState(out AudioSessionState state);
            int GetDisplayName(out IntPtr displayName);
            int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
            int GetIconPath(out IntPtr iconPath);
            int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
            int GetGroupingParam(out Guid groupingId);
            int SetGroupingParam(ref Guid groupingId, ref Guid eventContext);
            int RegisterAudioSessionNotification(IntPtr client);
            int UnregisterAudioSessionNotification(IntPtr client);
        }

        [ComImport]
        [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl2 : IAudioSessionControl
        {
            int GetSessionIdentifier(out IntPtr retVal);
            int GetSessionInstanceIdentifier(out IntPtr retVal);
            int GetProcessId(out uint retVal);
            int IsSystemSoundsSession();
            int SetDuckingPreference(bool optOut);
        }
    }
}
