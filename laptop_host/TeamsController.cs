using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace X3LaptopCompanion
{
    public enum TeamsCommand
    {
        ToggleMute,
        ToggleSpeaker,
        ToggleHand,
        ToggleVideo
    }

    public sealed class TeamsMeetingSnapshot
    {
        public TeamsMeetingSnapshot(bool teamsDetected, bool meetingDetected, string meetingName,
            CompanionTriState microphone, CompanionTriState camera, CompanionTriState hand, string detail)
        {
            TeamsDetected = teamsDetected;
            MeetingDetected = meetingDetected;
            MeetingName = meetingName ?? string.Empty;
            Microphone = microphone;
            Camera = camera;
            Hand = hand;
            Detail = detail ?? string.Empty;
        }

        public bool TeamsDetected { get; }
        public bool MeetingDetected { get; }
        public string MeetingName { get; }
        public CompanionTriState Microphone { get; }
        public CompanionTriState Camera { get; }
        public CompanionTriState Hand { get; }
        public string Detail { get; }
    }

    public sealed class TeamsController
    {
        private const int MaxMeetingSearchDepth = 42;
        private const int MaxMeetingSearchNodes = 12000;

        private static readonly string[] MicrophoneButtonNames = { "Mute mic", "Unmute mic" };
        private static readonly string[] CameraButtonNames = { "Turn camera on", "Turn camera off" };
        private static readonly string[] HandButtonNames = { "Raise your hand", "Lower your hand" };

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
            if (command == TeamsCommand.ToggleSpeaker)
            {
                HostLog.Write("Teams command skipped. command=" + CommandName(command) +
                    " reason=no UIA button name is configured; hotkeys are disabled.");
                return false;
            }

            if (!TryFindMeetingWindow(audioProcessIds, explicitTargetProcessId, out var context))
            {
                HostLog.Write("Teams command failed; no Teams meeting window with UIA controls was found. command=" +
                    CommandName(command));
                return false;
            }

            HostLog.Write("Teams command UIA search. command=" + CommandName(command) +
                " target=" + DescribeTarget(context.Target) +
                " hwnd=0x" + context.Hwnd.ToInt64().ToString("X") +
                " title=\"" + GetWindowTitle(context.Hwnd) + "\"");

            // Teams is a WebView/Chromium surface and it rerenders the meeting toolbar often. A previously
            // discovered AutomationElement may still exist as a COM wrapper but no longer represent the live button,
            // so every command walks the current tree and invokes the freshly found control.
            var controls = ReadMeetingControls(context.Root);
            var button = GetCommandButton(controls, command);
            if (button == null)
            {
                HostLog.Write("Teams command failed; matching button was not found. command=" + CommandName(command) +
                    " controls=" + controls.DescribeState());
                return false;
            }

            HostLog.Write("Teams command invoking UIA button. command=" + CommandName(command) +
                " buttonName=\"" + button.Name + "\"" +
                " automationId=\"" + button.AutomationId + "\"" +
                " currentStateBeforeInvoke=" + controls.DescribeState());

            if (!TryInvokeButton(button.Element, button.Name))
            {
                HostLog.Write("Teams command failed; button did not support InvokePattern. command=" +
                    CommandName(command) + " buttonName=\"" + button.Name + "\"");
                return false;
            }

            HostLog.Write("Teams command invoked. command=" + CommandName(command) +
                " buttonName=\"" + button.Name + "\" target=" + DescribeTarget(context.Target));
            return true;
        }

        public TeamsMeetingSnapshot GetMeetingSnapshot(IReadOnlyCollection<int> audioProcessIds,
            int? explicitTargetProcessId)
        {
            var teamsDetected = IsTeamsRunning;
            if (!teamsDetected)
            {
                return new TeamsMeetingSnapshot(false, false, string.Empty, CompanionTriState.Unknown,
                    CompanionTriState.Unknown, CompanionTriState.Unknown, "Teams not running");
            }

            if (!TryFindMeetingWindow(audioProcessIds, explicitTargetProcessId, out var context))
            {
                return new TeamsMeetingSnapshot(true, false, string.Empty, CompanionTriState.Unknown,
                    CompanionTriState.Unknown, CompanionTriState.Unknown, "Teams running; meeting controls not found");
            }

            // The button names describe the next action, so they also tell us the current meeting state:
            // "Mute mic" means the mic is currently live, while "Unmute mic" means Teams is currently muted.
            var controls = ReadMeetingControls(context.Root);
            var microphone = GetMicrophoneState(controls);
            var camera = GetCameraState(controls);
            var hand = GetHandState(controls);
            var meetingName = ExtractMeetingName(controls.FirstWindowName);
            var meetingDetected = controls.HasMeetingControl || !string.IsNullOrWhiteSpace(meetingName);
            var detail = "target=" + DescribeTarget(context.Target) +
                " hwnd=0x" + context.Hwnd.ToInt64().ToString("X") +
                " meeting=\"" + meetingName + "\" " + controls.DescribeState();

            HostLog.Write("Teams UIA snapshot. " + detail);
            return new TeamsMeetingSnapshot(true, meetingDetected, meetingName, microphone, camera, hand, detail);
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

        private static ButtonInfo GetCommandButton(MeetingControls controls, TeamsCommand command)
        {
            switch (command)
            {
                case TeamsCommand.ToggleMute:
                    return controls.MuteMicButton ?? controls.UnmuteMicButton;
                case TeamsCommand.ToggleHand:
                    return controls.RaiseHandButton ?? controls.LowerHandButton;
                case TeamsCommand.ToggleVideo:
                    return controls.TurnCameraOnButton ?? controls.TurnCameraOffButton;
                default:
                    return null;
            }
        }

        private static CompanionTriState GetMicrophoneState(MeetingControls controls)
        {
            if (controls.MuteMicButton != null)
            {
                return CompanionTriState.On;
            }

            return controls.UnmuteMicButton != null ? CompanionTriState.Off : CompanionTriState.Unknown;
        }

        private static CompanionTriState GetCameraState(MeetingControls controls)
        {
            if (controls.TurnCameraOffButton != null)
            {
                return CompanionTriState.On;
            }

            return controls.TurnCameraOnButton != null ? CompanionTriState.Off : CompanionTriState.Unknown;
        }

        private static CompanionTriState GetHandState(MeetingControls controls)
        {
            if (controls.LowerHandButton != null)
            {
                return CompanionTriState.On;
            }

            return controls.RaiseHandButton != null ? CompanionTriState.Off : CompanionTriState.Unknown;
        }

        private static string ExtractMeetingName(string windowName)
        {
            if (string.IsNullOrWhiteSpace(windowName))
            {
                return string.Empty;
            }

            var pipeIndex = windowName.IndexOf('|');
            var value = pipeIndex >= 0 ? windowName.Substring(0, pipeIndex) : windowName;
            return value.Trim();
        }

        private static bool TryInvokeButton(AutomationElement element, string name)
        {
            try
            {
                if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern))
                {
                    ((InvokePattern)pattern).Invoke();
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                HostLog.Write("Teams UIA invoke failed. buttonName=\"" + name + "\" error=" + ex.Message);
                return false;
            }
        }

        private bool TryFindMeetingWindow(IReadOnlyCollection<int> audioProcessIds, int? explicitTargetProcessId,
            out MeetingWindowContext context)
        {
            context = null;
            var targets = BuildCommandTargets(audioProcessIds).ToList();
            if (explicitTargetProcessId.HasValue)
            {
                targets.Insert(0, BuildExplicitTarget(explicitTargetProcessId.Value));
            }

            HostLog.Write("Teams UIA target search. explicitPid=" +
                (explicitTargetProcessId.HasValue ? explicitTargetProcessId.Value.ToString(CultureInfo.InvariantCulture) : "(none)") +
                " audioPids=" + FormatIds(audioProcessIds) + " candidates=" +
                string.Join("; ", targets.Select(DescribeTarget)));

            MeetingWindowContext bestContext = null;
            var bestScore = 0;
            foreach (var target in targets)
            {
                foreach (var window in FindCandidateWindows(target.ProcessId))
                {
                    HostLog.Write("Teams UIA target probe. " + DescribeTarget(target) +
                        " hwnd=0x" + window.Hwnd.ToInt64().ToString("X") +
                        " title=\"" + window.Title + "\" hosted=" + window.HostsRequestedProcess + ".");
                    try
                    {
                        var root = AutomationElement.FromHandle(window.Hwnd);
                        if (root == null)
                        {
                            continue;
                        }

                        var controls = ReadMeetingControls(root);
                        var score = controls.Score;
                        HostLog.Write("Teams UIA target score. " + DescribeTarget(target) +
                            " hwnd=0x" + window.Hwnd.ToInt64().ToString("X") +
                            " score=" + score + " " + controls.DescribeState());

                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestContext = new MeetingWindowContext(target, window.Hwnd, root);
                        }

                        if (controls.HasMeetingControl)
                        {
                            context = new MeetingWindowContext(target, window.Hwnd, root);
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        HostLog.Write("Teams UIA target probe failed. " + DescribeTarget(target) +
                            " hwnd=0x" + window.Hwnd.ToInt64().ToString("X") + " error=" + ex.Message);
                    }
                }
            }

            if (bestContext != null && bestScore > 0)
            {
                context = bestContext;
                return true;
            }

            return false;
        }

        private static MeetingControls ReadMeetingControls(AutomationElement root)
        {
            var controls = new MeetingControls();
            var visited = new HashSet<string>();
            var count = 0;
            ReadMeetingControls(root, TreeWalker.RawViewWalker, controls, visited, 0, ref count);
            controls.NodesVisited = count;
            return controls;
        }

        private static void ReadMeetingControls(AutomationElement element, TreeWalker walker, MeetingControls controls,
            HashSet<string> visited, int depth, ref int count)
        {
            if (element == null || depth > MaxMeetingSearchDepth || count >= MaxMeetingSearchNodes)
            {
                return;
            }

            var runtimeId = GetRuntimeIdKey(element);
            if (!visited.Add(runtimeId))
            {
                return;
            }

            count++;
            ReadControl(element, controls);

            AutomationElement child;
            try
            {
                child = walker.GetFirstChild(element);
            }
            catch
            {
                return;
            }

            while (child != null && count < MaxMeetingSearchNodes)
            {
                ReadMeetingControls(child, walker, controls, visited, depth + 1, ref count);
                try
                {
                    child = walker.GetNextSibling(child);
                }
                catch
                {
                    return;
                }
            }
        }

        private static void ReadControl(AutomationElement element, MeetingControls controls)
        {
            var name = Safe(() => element.Current.Name);
            var automationId = Safe(() => element.Current.AutomationId);
            var controlType = Safe(() => element.Current.ControlType.ProgrammaticName);
            if (string.IsNullOrWhiteSpace(controls.FirstWindowName) &&
                controlType.IndexOf("Window", StringComparison.OrdinalIgnoreCase) >= 0 &&
                !string.IsNullOrWhiteSpace(name))
            {
                controls.FirstWindowName = name;
            }

            if (!IsButton(element, controlType))
            {
                return;
            }

            var button = new ButtonInfo(element, name, automationId);
            if (NameEqualsAny(name, MicrophoneButtonNames))
            {
                if (NameEquals(name, "Mute mic"))
                {
                    controls.MuteMicButton = controls.MuteMicButton ?? button;
                }
                else
                {
                    controls.UnmuteMicButton = controls.UnmuteMicButton ?? button;
                }

                return;
            }

            if (NameEqualsAny(name, CameraButtonNames))
            {
                if (NameEquals(name, "Turn camera on"))
                {
                    controls.TurnCameraOnButton = controls.TurnCameraOnButton ?? button;
                }
                else
                {
                    controls.TurnCameraOffButton = controls.TurnCameraOffButton ?? button;
                }

                return;
            }

            if (NameEqualsAny(name, HandButtonNames))
            {
                if (NameEquals(name, "Raise your hand"))
                {
                    controls.RaiseHandButton = controls.RaiseHandButton ?? button;
                }
                else
                {
                    controls.LowerHandButton = controls.LowerHandButton ?? button;
                }
            }
        }

        private static bool IsButton(AutomationElement element, string controlType)
        {
            try
            {
                if (element.Current.ControlType == ControlType.Button)
                {
                    return true;
                }
            }
            catch
            {
            }

            return controlType.IndexOf("Button", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool NameEqualsAny(string name, IEnumerable<string> expectedNames)
        {
            return expectedNames.Any(expected => NameEquals(name, expected));
        }

        private static bool NameEquals(string name, string expected)
        {
            var normalized = (name ?? string.Empty).Trim();
            return string.Equals(normalized, expected, StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith(expected + " ", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith(expected + "\r", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith(expected + "\n", StringComparison.OrdinalIgnoreCase);
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
                // The audio session is often owned by a hosted Teams/WebView process, while the meeting window sits
                // on its parent. Try that parent before falling back to the audio process itself.
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
                    HostLog.Write("Teams UIA parent lookup failed. audioPid=" + audioProcessId);
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

        private static IEnumerable<WindowCandidate> FindCandidateWindows(int processId)
        {
            var directWindows = GetTopLevelWindows()
                .Where(w => w.ProcessId == processId)
                .OrderByDescending(w => !string.IsNullOrWhiteSpace(w.Title))
                .ToList();

            foreach (var window in directWindows)
            {
                yield return window;
            }

            var directHandles = new HashSet<IntPtr>(directWindows.Select(w => w.Hwnd));
            foreach (var hostedWindow in FindWindowsHostingProcess(processId))
            {
                if (directHandles.Add(hostedWindow.Hwnd))
                {
                    yield return hostedWindow;
                }
            }
        }

        private static IEnumerable<WindowCandidate> FindWindowsHostingProcess(int processId)
        {
            var matches = new List<WindowCandidate>();
            foreach (var window in GetTopLevelWindows().Where(w => !string.IsNullOrWhiteSpace(w.Title)))
            {
                try
                {
                    var root = AutomationElement.FromHandle(window.Hwnd);
                    if (root != null && ContainsProcessId(root, processId, TreeWalker.RawViewWalker, 0, 0))
                    {
                        matches.Add(new WindowCandidate(window.Hwnd, window.ProcessId, window.ProcessName,
                            window.Title, true));
                    }
                }
                catch
                {
                }
            }

            return matches;
        }

        private static bool ContainsProcessId(AutomationElement element, int processId, TreeWalker walker, int depth,
            int nodeCount)
        {
            if (element == null || depth > MaxMeetingSearchDepth || nodeCount > MaxMeetingSearchNodes)
            {
                return false;
            }

            if (TryGetElementProcessId(element, out var elementProcessId) && elementProcessId == processId)
            {
                return true;
            }

            AutomationElement child;
            try
            {
                child = walker.GetFirstChild(element);
            }
            catch
            {
                return false;
            }

            while (child != null)
            {
                nodeCount++;
                if (ContainsProcessId(child, processId, walker, depth + 1, nodeCount))
                {
                    return true;
                }

                try
                {
                    child = walker.GetNextSibling(child);
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        private static bool TryGetElementProcessId(AutomationElement element, out int processId)
        {
            processId = 0;

            try
            {
                processId = element.Current.ProcessId;
                return processId > 0;
            }
            catch
            {
                processId = 0;
                return false;
            }
        }

        private static List<WindowCandidate> GetTopLevelWindows()
        {
            var windows = new List<WindowCandidate>();
            EnumWindows((hwnd, lParam) =>
            {
                if (!IsWindowVisible(hwnd))
                {
                    return true;
                }

                GetWindowThreadProcessId(hwnd, out var processId);
                var title = GetWindowTitle(hwnd);
                var processName = GetProcessName(processId);
                windows.Add(new WindowCandidate(hwnd, processId, processName, title, false));
                return true;
            }, IntPtr.Zero);
            return windows;
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

        private static string GetProcessName(int processId)
        {
            try
            {
                return Process.GetProcessById(processId).ProcessName;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetRuntimeIdKey(AutomationElement element)
        {
            try
            {
                return string.Join(".", element.GetRuntimeId() ?? Array.Empty<int>());
            }
            catch
            {
                return element.GetHashCode().ToString(CultureInfo.InvariantCulture);
            }
        }

        private static string Safe(Func<string> read)
        {
            try
            {
                return read() ?? string.Empty;
            }
            catch (Exception ex)
            {
                return "unavailable:" + ex.Message;
            }
        }

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

        private sealed class MeetingWindowContext
        {
            public MeetingWindowContext(CommandTarget target, IntPtr hwnd, AutomationElement root)
            {
                Target = target;
                Hwnd = hwnd;
                Root = root;
            }

            public CommandTarget Target { get; }
            public IntPtr Hwnd { get; }
            public AutomationElement Root { get; }
        }

        private sealed class WindowCandidate
        {
            public WindowCandidate(IntPtr hwnd, int processId, string processName, string title, bool hostsRequestedProcess)
            {
                Hwnd = hwnd;
                ProcessId = processId;
                ProcessName = processName ?? string.Empty;
                Title = title ?? string.Empty;
                HostsRequestedProcess = hostsRequestedProcess;
            }

            public IntPtr Hwnd { get; }
            public int ProcessId { get; }
            public string ProcessName { get; }
            public string Title { get; }
            public bool HostsRequestedProcess { get; }
        }

        private sealed class ButtonInfo
        {
            public ButtonInfo(AutomationElement element, string name, string automationId)
            {
                Element = element;
                Name = name ?? string.Empty;
                AutomationId = automationId ?? string.Empty;
            }

            public AutomationElement Element { get; }
            public string Name { get; }
            public string AutomationId { get; }
        }

        private sealed class MeetingControls
        {
            public string FirstWindowName { get; set; }
            public ButtonInfo MuteMicButton { get; set; }
            public ButtonInfo UnmuteMicButton { get; set; }
            public ButtonInfo TurnCameraOnButton { get; set; }
            public ButtonInfo TurnCameraOffButton { get; set; }
            public ButtonInfo RaiseHandButton { get; set; }
            public ButtonInfo LowerHandButton { get; set; }
            public int NodesVisited { get; set; }

            public bool HasMeetingControl
            {
                get
                {
                    return MuteMicButton != null ||
                        UnmuteMicButton != null ||
                        TurnCameraOnButton != null ||
                        TurnCameraOffButton != null ||
                        RaiseHandButton != null ||
                        LowerHandButton != null;
                }
            }

            public int Score
            {
                get
                {
                    var score = 0;
                    if (MuteMicButton != null || UnmuteMicButton != null)
                    {
                        score += 4;
                    }

                    if (TurnCameraOnButton != null || TurnCameraOffButton != null)
                    {
                        score += 3;
                    }

                    if (RaiseHandButton != null || LowerHandButton != null)
                    {
                        score += 2;
                    }

                    if (!string.IsNullOrWhiteSpace(FirstWindowName))
                    {
                        score += 1;
                    }

                    return score;
                }
            }

            public string DescribeState()
            {
                return "nodes=" + NodesVisited +
                    " firstWindow=\"" + (FirstWindowName ?? string.Empty) + "\"" +
                    " muteButton=\"" + ButtonName(MuteMicButton) + "\"" +
                    " unmuteButton=\"" + ButtonName(UnmuteMicButton) + "\"" +
                    " cameraOnButton=\"" + ButtonName(TurnCameraOnButton) + "\"" +
                    " cameraOffButton=\"" + ButtonName(TurnCameraOffButton) + "\"" +
                    " raiseHandButton=\"" + ButtonName(RaiseHandButton) + "\"" +
                    " lowerHandButton=\"" + ButtonName(LowerHandButton) + "\"";
            }

            private static string ButtonName(ButtonInfo button)
            {
                return button == null ? string.Empty : button.Name;
            }
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
