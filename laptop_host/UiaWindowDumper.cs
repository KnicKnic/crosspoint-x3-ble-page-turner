using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;

namespace X3LaptopCompanion
{
    public sealed class UiaWindowDumper
    {
        private const int MaxDepth = 9;
        private const int MaxNodes = 1200;

        public bool DumpWindow(string target)
        {
            if (!TryResolveWindow(target, out var hwnd, out var resolvedBy))
            {
                HostLog.Write("UIA dump target not found. target=\"" + (target ?? string.Empty) + "\"");
                return false;
            }

            HostLog.Write("UIA dump begin. target=\"" + target + "\" resolvedBy=\"" + resolvedBy +
                "\" hwnd=0x" + hwnd.ToInt64().ToString("X") + " title=\"" + GetWindowTitle(hwnd) + "\"");

            try
            {
                var root = AutomationElement.FromHandle(hwnd);
                if (root == null)
                {
                    HostLog.Write("UIA dump failed: AutomationElement.FromHandle returned null.");
                    return false;
                }

                var count = 0;
                DumpElement(root, 0, ref count);
                HostLog.Write("UIA dump end. nodes=" + count + ".");
                return true;
            }
            catch (Exception ex)
            {
                HostLog.Write("UIA dump failed: " + ex);
                return false;
            }
        }

        public void ListWindows(string filter)
        {
            var windows = GetTopLevelWindows()
                .Where(w => string.IsNullOrWhiteSpace(filter) || WindowMatchesFilter(w, filter))
                .ToList();

            HostLog.Write("UIA window list begin. filter=\"" + (filter ?? string.Empty) + "\" count=" + windows.Count);
            foreach (var window in windows)
            {
                HostLog.Write("UIA window hwnd=0x" + window.Hwnd.ToInt64().ToString("X") +
                    " pid=" + window.ProcessId +
                    " process=\"" + window.ProcessName + "\"" +
                    " title=\"" + window.Title + "\"");
            }

            HostLog.Write("UIA window list end.");
        }

        private static void DumpElement(AutomationElement element, int depth, ref int count)
        {
            if (element == null || count >= MaxNodes)
            {
                return;
            }

            HostLog.Write("UIA " + new string(' ', depth * 2) + DescribeElement(element));
            count++;

            if (depth >= MaxDepth || count >= MaxNodes)
            {
                return;
            }

            AutomationElement child = null;
            try
            {
                child = TreeWalker.ControlViewWalker.GetFirstChild(element);
            }
            catch (Exception ex)
            {
                HostLog.Write("UIA " + new string(' ', (depth + 1) * 2) + "children unavailable: " + ex.Message);
                return;
            }

            while (child != null && count < MaxNodes)
            {
                DumpElement(child, depth + 1, ref count);
                try
                {
                    child = TreeWalker.ControlViewWalker.GetNextSibling(child);
                }
                catch (Exception ex)
                {
                    HostLog.Write("UIA " + new string(' ', (depth + 1) * 2) + "sibling unavailable: " + ex.Message);
                    return;
                }
            }
        }

        private static string DescribeElement(AutomationElement element)
        {
            var builder = new StringBuilder();
            builder.Append("name=\"").Append(Safe(() => element.Current.Name)).Append("\"");
            builder.Append(" automationId=\"").Append(Safe(() => element.Current.AutomationId)).Append("\"");
            builder.Append(" controlType=\"").Append(Safe(() => element.Current.ControlType.ProgrammaticName)).Append("\"");
            builder.Append(" class=\"").Append(Safe(() => element.Current.ClassName)).Append("\"");
            builder.Append(" framework=\"").Append(Safe(() => element.Current.FrameworkId)).Append("\"");
            builder.Append(" pid=").Append(Safe(() => element.Current.ProcessId.ToString(CultureInfo.InvariantCulture)));
            builder.Append(" hwnd=0x").Append(Safe(() => element.Current.NativeWindowHandle.ToString("X")));
            builder.Append(" enabled=").Append(Safe(() => element.Current.IsEnabled.ToString()));
            builder.Append(" offscreen=").Append(Safe(() => element.Current.IsOffscreen.ToString()));
            builder.Append(" rect=\"").Append(Safe(() => FormatRect(element.Current.BoundingRectangle))).Append("\"");
            builder.Append(" patterns=\"").Append(GetPatternNames(element)).Append("\"");
            return builder.ToString();
        }

        private static string GetPatternNames(AutomationElement element)
        {
            try
            {
                return string.Join(",", element.GetSupportedPatterns().Select(p => p.ProgrammaticName));
            }
            catch (Exception ex)
            {
                return "unavailable:" + ex.Message;
            }
        }

        private static string FormatRect(Rect rect)
        {
            return rect.X.ToString("0", CultureInfo.InvariantCulture) + "," +
                rect.Y.ToString("0", CultureInfo.InvariantCulture) + "," +
                rect.Width.ToString("0", CultureInfo.InvariantCulture) + "x" +
                rect.Height.ToString("0", CultureInfo.InvariantCulture);
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

        private static bool TryResolveWindow(string target, out IntPtr hwnd, out string resolvedBy)
        {
            hwnd = IntPtr.Zero;
            resolvedBy = string.Empty;
            target = (target ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(target))
            {
                return false;
            }

            if (target.StartsWith("hwnd:", StringComparison.OrdinalIgnoreCase))
            {
                return TryParseHwnd(target.Substring(5), out hwnd, out resolvedBy);
            }

            if (target.StartsWith("pid:", StringComparison.OrdinalIgnoreCase))
            {
                return TryResolvePid(target.Substring(4), out hwnd, out resolvedBy);
            }

            if (target.StartsWith("title:", StringComparison.OrdinalIgnoreCase))
            {
                return TryResolveTitle(target.Substring(6), out hwnd, out resolvedBy);
            }

            if (target.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return TryParseHwnd(target, out hwnd, out resolvedBy);
            }

            if (int.TryParse(target, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                hwnd = new IntPtr(number);
                if (IsWindow(hwnd))
                {
                    resolvedBy = "decimal hwnd";
                    return true;
                }

                return TryResolvePid(target, out hwnd, out resolvedBy);
            }

            return TryResolveTitle(target, out hwnd, out resolvedBy);
        }

        private static bool TryParseHwnd(string value, out IntPtr hwnd, out string resolvedBy)
        {
            hwnd = IntPtr.Zero;
            resolvedBy = string.Empty;
            value = (value ?? string.Empty).Trim();
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(2);
            }

            if (!long.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var numeric))
            {
                return false;
            }

            hwnd = new IntPtr(numeric);
            if (!IsWindow(hwnd))
            {
                return false;
            }

            resolvedBy = "hwnd";
            return true;
        }

        private static bool TryResolvePid(string value, out IntPtr hwnd, out string resolvedBy)
        {
            hwnd = IntPtr.Zero;
            resolvedBy = string.Empty;
            if (!int.TryParse((value ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out var pid))
            {
                return false;
            }

            var window = GetTopLevelWindows()
                .FirstOrDefault(w => w.ProcessId == pid && !string.IsNullOrWhiteSpace(w.Title));
            if (window == null)
            {
                window = GetTopLevelWindows().FirstOrDefault(w => w.ProcessId == pid);
            }

            if (window == null)
            {
                return false;
            }

            hwnd = window.Hwnd;
            resolvedBy = "pid";
            return true;
        }

        private static bool TryResolveTitle(string value, out IntPtr hwnd, out string resolvedBy)
        {
            hwnd = IntPtr.Zero;
            resolvedBy = string.Empty;
            value = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var window = GetTopLevelWindows()
                .FirstOrDefault(w => w.Title.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0);
            if (window == null)
            {
                return false;
            }

            hwnd = window.Hwnd;
            resolvedBy = "title";
            return true;
        }

        private static bool WindowMatchesFilter(WindowInfo window, string filter)
        {
            filter = filter.Trim();
            return window.Title.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                window.ProcessName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                window.ProcessId.ToString(CultureInfo.InvariantCulture).Equals(filter, StringComparison.OrdinalIgnoreCase) ||
                ("0x" + window.Hwnd.ToInt64().ToString("X")).Equals(filter, StringComparison.OrdinalIgnoreCase);
        }

        private static List<WindowInfo> GetTopLevelWindows()
        {
            var windows = new List<WindowInfo>();
            EnumWindows((hwnd, lParam) =>
            {
                if (!IsWindowVisible(hwnd))
                {
                    return true;
                }

                GetWindowThreadProcessId(hwnd, out var processId);
                var title = GetWindowTitle(hwnd);
                var processName = GetProcessName(processId);
                windows.Add(new WindowInfo(hwnd, processId, processName, title));
                return true;
            }, IntPtr.Zero);
            return windows;
        }

        private static string GetWindowTitle(IntPtr hwnd)
        {
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

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, [Out] char[] lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private sealed class WindowInfo
        {
            public WindowInfo(IntPtr hwnd, int processId, string processName, string title)
            {
                Hwnd = hwnd;
                ProcessId = processId;
                ProcessName = processName ?? string.Empty;
                Title = title ?? string.Empty;
            }

            public IntPtr Hwnd { get; }
            public int ProcessId { get; }
            public string ProcessName { get; }
            public string Title { get; }
        }
    }
}
