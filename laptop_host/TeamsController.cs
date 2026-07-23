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
            return TrySendCommand(command, Array.Empty<int>());
        }

        public bool TrySendCommand(TeamsCommand command, IReadOnlyCollection<int> audioProcessIds)
        {
            return TrySendCommand(command, audioProcessIds, null);
        }

        public bool TrySendCommand(TeamsCommand command, IReadOnlyCollection<int> audioProcessIds,
            int? explicitTargetProcessId)
        {
            var targets = BuildCommandTargets(audioProcessIds).ToList();
            if (explicitTargetProcessId.HasValue)
            {
                targets.Insert(0, BuildExplicitTarget(explicitTargetProcessId.Value));
            }

            HostLog.Write("Teams command target search. command=" + CommandName(command) +
                " explicitPid=" + (explicitTargetProcessId.HasValue ? explicitTargetProcessId.Value.ToString() : "(none)") +
                " audioPids=" + FormatIds(audioProcessIds) + " candidates=" +
                string.Join("; ", targets.Select(DescribeTarget)));

            foreach (var target in targets)
            {
                var windowHandle = FindTeamsWindowHandle(target.ProcessId);
                HostLog.Write("Teams command target probe. " + DescribeTarget(target) +
                    " hwnd=0x" + windowHandle.ToInt64().ToString("X") +
                    " windowTitle=\"" + GetWindowTitle(windowHandle) + "\".");
                if (windowHandle == IntPtr.Zero)
                {
                    continue;
                }

                var posted = PostCtrlShiftShortcut(windowHandle, CommandKey(command));
                HostLog.Write("Teams command post complete. command=" + CommandName(command) +
                    " shortcut=" + CommandShortcut(command) + " target=" + DescribeTarget(target) +
                    " hwnd=0x" + windowHandle.ToInt64().ToString("X") + " posted=" + posted + ".");
                if (posted)
                {
                    return true;
                }
            }

            HostLog.Write("Teams command failed; no target accepted PostMessage. command=" + CommandName(command));
            return false;
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

        private static IEnumerable<CommandTarget> BuildCommandTargets(IReadOnlyCollection<int> audioProcessIds)
        {
            var yielded = new HashSet<int>();
            foreach (var audioProcessId in audioProcessIds ?? Array.Empty<int>())
            {
                if (TryGetParentProcessId(audioProcessId, out var parentProcessId))
                {
                    var parent = TryGetProcess(parentProcessId);
                    if (parent != null && yielded.Add(parent.Id))
                    {
                        yield return new CommandTarget(parent.Id, SafeProcessName(parent),
                            "parent of audio pid " + audioProcessId);
                    }
                }
                else
                {
                    HostLog.Write("Teams command parent lookup failed. audioPid=" + audioProcessId);
                }

                var audioProcess = TryGetProcess(audioProcessId);
                if (audioProcess != null && yielded.Add(audioProcess.Id))
                {
                    yield return new CommandTarget(audioProcess.Id, SafeProcessName(audioProcess), "audio session pid");
                }
            }

            foreach (var teamsProcess in FindTeamsProcesses()
                         .OrderByDescending(p => SafeMainWindowHandle(p) != IntPtr.Zero))
            {
                if (yielded.Add(teamsProcess.Id))
                {
                    yield return new CommandTarget(teamsProcess.Id, SafeProcessName(teamsProcess), "teams process");
                }
            }
        }

        private static CommandTarget BuildExplicitTarget(int processId)
        {
            var process = TryGetProcess(processId);
            return new CommandTarget(processId, process == null ? "(not running)" : SafeProcessName(process),
                "explicit target pid");
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

        private static bool PostCtrlShiftShortcut(IntPtr hwnd, ushort key)
        {
            var posted = true;
            posted &= PostKey(hwnd, VK_CONTROL, false);
            posted &= PostKey(hwnd, VK_SHIFT, false);
            posted &= PostKey(hwnd, key, false);
            posted &= PostKey(hwnd, key, true);
            posted &= PostKey(hwnd, VK_SHIFT, true);
            posted &= PostKey(hwnd, VK_CONTROL, true);
            return posted;
        }

        private static bool PostKey(IntPtr hwnd, ushort key, bool keyUp)
        {
            var message = keyUp ? WM_KEYUP : WM_KEYDOWN;
            var lParam = CreateKeyLParam(key, keyUp);
            var posted = PostMessage(hwnd, message, new IntPtr(key), lParam);
            var error = posted ? 0 : Marshal.GetLastWin32Error();
            HostLog.Write("Teams command PostMessage. hwnd=0x" + hwnd.ToInt64().ToString("X") +
                " msg=0x" + message.ToString("X") + " vk=0x" + key.ToString("X") +
                " keyUp=" + keyUp + " lParam=0x" + lParam.ToInt64().ToString("X") +
                " posted=" + posted + " error=" + error + ".");
            return posted;
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

        private static Process TryGetProcess(int processId)
        {
            try
            {
                return Process.GetProcessById(processId);
            }
            catch
            {
                return null;
            }
        }

        private static string SafeProcessName(Process process)
        {
            try
            {
                return process.ProcessName ?? string.Empty;
            }
            catch
            {
                return "(unavailable)";
            }
        }

        private static IntPtr SafeMainWindowHandle(Process process)
        {
            try
            {
                return process.MainWindowHandle;
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        private static string FormatIds(IReadOnlyCollection<int> ids)
        {
            return ids == null || ids.Count == 0 ? "(none)" : string.Join(",", ids);
        }

        private static string DescribeTarget(CommandTarget target)
        {
            return "pid=" + target.ProcessId + " name=" + target.ProcessName + " reason=" + target.Reason;
        }

        private static bool TryGetParentProcessId(int processId, out int parentProcessId)
        {
            parentProcessId = 0;
            var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snapshot == InvalidHandleValue)
            {
                return false;
            }

            try
            {
                var entry = new PROCESSENTRY32();
                entry.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));
                if (!Process32First(snapshot, ref entry))
                {
                    return false;
                }

                do
                {
                    if (entry.th32ProcessID == (uint)processId)
                    {
                        parentProcessId = (int)entry.th32ParentProcessID;
                        return parentProcessId > 0;
                    }
                }
                while (Process32Next(snapshot, ref entry));

                return false;
            }
            finally
            {
                CloseHandle(snapshot);
            }
        }

        private static string GetWindowTitle(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                return string.Empty;
            }

            var length = GetWindowTextLength(hwnd);
            if (length <= 0)
            {
                return string.Empty;
            }

            var buffer = new char[length + 1];
            var copied = GetWindowText(hwnd, buffer, buffer.Length);
            return copied <= 0 ? string.Empty : new string(buffer, 0, copied);
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(ushort uCode, uint uMapType);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, [Out] char[] lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private const uint TH32CS_SNAPPROCESS = 0x00000002;
        private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private sealed class CommandTarget
        {
            public CommandTarget(int processId, string processName, string reason)
            {
                ProcessId = processId;
                ProcessName = processName;
                Reason = reason;
            }

            public int ProcessId { get; }
            public string ProcessName { get; }
            public string Reason { get; }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }
    }
}
