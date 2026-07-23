using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace X3LaptopCompanion
{
    public enum TeamsCommand
    {
        ToggleMute,
        ToggleSpeaker,
        ToggleHand,
        ToggleVideo
    }

    public sealed class TeamsController
    {
        private const uint WM_KEYDOWN = 0x0100;
        private const uint WM_KEYUP = 0x0101;
        private const uint MAPVK_VK_TO_VSC = 0;
        private const ushort VK_CONTROL = 0x11;
        private const ushort VK_SHIFT = 0x10;
        private const ushort VK_M = 0x4D;
        private const ushort VK_U = 0x55;
        private const ushort VK_K = 0x4B;
        private const ushort VK_O = 0x4F;

        public bool IsTeamsRunning
        {
            get { return FindTeamsProcess() != null; }
        }

        public IReadOnlyCollection<int> TeamsProcessIds
        {
            get { return FindTeamsProcesses().Select(p => p.Id).ToList(); }
        }

        public bool TryToggleMute()
        {
            return TrySendCommand(TeamsCommand.ToggleMute);
        }

        public bool TrySendCommand(TeamsCommand command)
        {
            var process = FindTeamsProcess();
            if (process == null)
            {
                return false;
            }

            var windowHandle = FindTeamsWindowHandle(process.Id);
            if (windowHandle == IntPtr.Zero)
            {
                return false;
            }

            PostCtrlShiftShortcut(windowHandle, CommandKey(command));
            return true;
        }

        public static string CommandName(TeamsCommand command)
        {
            switch (command)
            {
                case TeamsCommand.ToggleMute:
                    return "Toggle mute";
                case TeamsCommand.ToggleSpeaker:
                    return "Toggle speaker";
                case TeamsCommand.ToggleHand:
                    return "Raise/lower hand";
                case TeamsCommand.ToggleVideo:
                    return "Toggle video";
                default:
                    return command.ToString();
            }
        }

        public static string CommandShortcut(TeamsCommand command)
        {
            return "Ctrl+Shift+" + (char)CommandKey(command);
        }

        private static List<Process> FindTeamsProcesses()
        {
            return Process.GetProcesses()
                .Where(p =>
                {
                    try
                    {
                        return IsTeamsProcessName(p.ProcessName ?? string.Empty);
                    }
                    catch
                    {
                        return false;
                    }
                })
                .ToList();
        }

        private static Process FindTeamsProcess()
        {
            var candidates = FindTeamsProcesses();

            return candidates.FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero) ?? candidates.FirstOrDefault();
        }

        private static bool IsTeamsProcessName(string name)
        {
            return name.IndexOf("Teams", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("ms-teams", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static IntPtr FindTeamsWindowHandle(int processId)
        {
            var process = Process.GetProcessById(processId);
            if (process.MainWindowHandle != IntPtr.Zero)
            {
                return process.MainWindowHandle;
            }

            var found = IntPtr.Zero;
            EnumWindows((hwnd, lParam) =>
            {
                GetWindowThreadProcessId(hwnd, out var windowProcessId);
                if (windowProcessId == processId && IsWindowVisible(hwnd))
                {
                    found = hwnd;
                    return false;
                }

                return true;
            }, IntPtr.Zero);

            return found;
        }

        private static ushort CommandKey(TeamsCommand command)
        {
            switch (command)
            {
                case TeamsCommand.ToggleMute:
                    return VK_M;
                case TeamsCommand.ToggleSpeaker:
                    return VK_U;
                case TeamsCommand.ToggleHand:
                    return VK_K;
                case TeamsCommand.ToggleVideo:
                    return VK_O;
                default:
                    throw new ArgumentOutOfRangeException(nameof(command), command, null);
            }
        }

        private static void PostCtrlShiftShortcut(IntPtr hwnd, ushort key)
        {
            PostKey(hwnd, VK_CONTROL, false);
            PostKey(hwnd, VK_SHIFT, false);
            PostKey(hwnd, key, false);
            PostKey(hwnd, key, true);
            PostKey(hwnd, VK_SHIFT, true);
            PostKey(hwnd, VK_CONTROL, true);
        }

        private static void PostKey(IntPtr hwnd, ushort key, bool keyUp)
        {
            PostMessage(hwnd, keyUp ? WM_KEYUP : WM_KEYDOWN, new IntPtr(key), CreateKeyLParam(key, keyUp));
        }

        private static IntPtr CreateKeyLParam(ushort key, bool keyUp)
        {
            var scanCode = MapVirtualKey(key, MAPVK_VK_TO_VSC);
            var value = 1 | (scanCode << 16);
            if (keyUp)
            {
                value |= 1u << 30;
                value |= 1u << 31;
            }

            return new IntPtr(unchecked((int)value));
        }

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(ushort uCode, uint uMapType);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    }
}
