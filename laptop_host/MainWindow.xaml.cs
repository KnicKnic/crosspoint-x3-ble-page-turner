using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace X3LaptopCompanion
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private readonly TeamsController teamsController = new TeamsController();
        private readonly CompanionConnectionService connectionService = new CompanionConnectionService();
        private readonly DispatcherTimer statusTimer = new DispatcherTimer();

        private string connectionText = "Disconnected";
        private string teamsText = "Waiting for Teams";
        private string microphoneText = "Unknown";
        private string cameraText = "Unknown";
        private bool isTestMode;
        private string testModeText = "Off";
        private bool testTeamsDetected = true;
        private CompanionTriState testMicrophone = CompanionTriState.Unknown;
        private CompanionTriState testCamera = CompanionTriState.Unknown;
        private string testMessage = "Test status";
        private string testTeamsToggleText = "Detected";
        private string testMicrophoneToggleText = "Unknown";
        private string testCameraToggleText = "Unknown";
        private bool isTeamsDryRun;
        private string teamsModeText = "Live Teams";
        private string detailText =
            "Open Laptop Companion on the X3 Home screen, then pair/connect over BLE once the firmware service is wired in.";

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;
            HostLog.Write("Main window created.");
            connectionService.StatusChanged += OnConnectionStatusChanged;
            connectionService.DeviceCommandReceived += OnDeviceCommandReceived;
            statusTimer.Interval = System.TimeSpan.FromSeconds(2);
            statusTimer.Tick += OnStatusTimerTick;
            Loaded += OnLoaded;
            Closing += OnClosing;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public string ConnectionText
        {
            get { return connectionText; }
            private set { SetField(ref connectionText, value, nameof(ConnectionText)); }
        }

        public string TeamsText
        {
            get { return teamsText; }
            private set { SetField(ref teamsText, value, nameof(TeamsText)); }
        }

        public string MicrophoneText
        {
            get { return microphoneText; }
            private set { SetField(ref microphoneText, value, nameof(MicrophoneText)); }
        }

        public string CameraText
        {
            get { return cameraText; }
            private set { SetField(ref cameraText, value, nameof(CameraText)); }
        }

        public bool IsTestMode
        {
            get { return isTestMode; }
            set
            {
                if (SetField(ref isTestMode, value, nameof(IsTestMode)))
                {
                    ApplyTestMode();
                }
            }
        }

        public string TestModeText
        {
            get { return testModeText; }
            private set { SetField(ref testModeText, value, nameof(TestModeText)); }
        }

        public bool TestTeamsDetected
        {
            get { return testTeamsDetected; }
            set
            {
                if (SetField(ref testTeamsDetected, value, nameof(TestTeamsDetected)))
                {
                    TestTeamsToggleText = testTeamsDetected ? "Detected" : "Not detected";
                    OnTestStatusChanged("teams");
                }
            }
        }

        public string TestMessage
        {
            get { return testMessage; }
            set
            {
                if (SetField(ref testMessage, value, nameof(TestMessage)) && IsTestMode)
                {
                    DetailText = "Test message updated. Press Send Test Status to push it to the X3.";
                }
            }
        }

        public string TestTeamsToggleText
        {
            get { return testTeamsToggleText; }
            private set { SetField(ref testTeamsToggleText, value, nameof(TestTeamsToggleText)); }
        }

        public string TestMicrophoneToggleText
        {
            get { return testMicrophoneToggleText; }
            private set { SetField(ref testMicrophoneToggleText, value, nameof(TestMicrophoneToggleText)); }
        }

        public string TestCameraToggleText
        {
            get { return testCameraToggleText; }
            private set { SetField(ref testCameraToggleText, value, nameof(TestCameraToggleText)); }
        }

        public bool IsTeamsDryRun
        {
            get { return isTeamsDryRun; }
            set
            {
                if (SetField(ref isTeamsDryRun, value, nameof(IsTeamsDryRun)))
                {
                    ApplyTeamsDryRun();
                }
            }
        }

        public string TeamsModeText
        {
            get { return teamsModeText; }
            private set { SetField(ref teamsModeText, value, nameof(TeamsModeText)); }
        }

        public string DetailText
        {
            get { return detailText; }
            private set { SetField(ref detailText, value, nameof(DetailText)); }
        }

        public void ToggleMuteFromUi()
        {
            HostLog.Write("Toggle mute requested.");
            RefreshTeamsPresence();
            if (IsTeamsDryRun)
            {
                TeamsText = "Dry run";
                MicrophoneText = "Toggle received";
                CameraText = "Unchanged";
                HostLog.Write("Toggle mute dry-run completed; Teams was not touched.");
                DetailText = "Dry run: X3 mute command received over BLE. Teams was not focused or controlled.";
                _ = connectionService.SendHostStatusAsync(true, CompanionTriState.Off, CompanionTriState.Unknown,
                    "Dry-run mute received");
                return;
            }

            if (!teamsController.TryToggleMute())
            {
                HostLog.Write("Toggle mute failed; Teams not found.");
                DetailText = "Teams was not found. Start or join a Teams meeting, then try again.";
                _ = connectionService.SendHostStatusAsync(false, CompanionTriState.Unknown, CompanionTriState.Unknown, "Teams not found");
                return;
            }

            MicrophoneText = "Toggle sent";
            HostLog.Write("Toggle mute sent to Teams.");
            DetailText =
                "Mute toggle sent with Ctrl+Shift+M. State detection will become authoritative once Teams status detection is implemented.";
            _ = connectionService.SendHostStatusAsync(true, CompanionTriState.Unknown, CompanionTriState.Unknown, "Mute toggle sent");
        }

        public void SimulateX3MutePress()
        {
            HostLog.Write("Simulate X3 mute press requested.");
            if (!IsTestMode)
            {
                DetailText = "Enable test mode before simulating X3 commands.";
                return;
            }

            OnDeviceCommandReceived(this, CompanionDeviceCommand.ToggleMute);
        }

        public void SendTestStatus()
        {
            HostLog.Write("Test status requested.");
            if (IsTestMode)
            {
                SendTestHostStatus("manual");
                return;
            }

            if (!IsTeamsDryRun)
            {
                RefreshTeamsPresence();
            }
            else
            {
                TeamsText = "Dry run";
            }

            MicrophoneText = "Muted";
            CameraText = "Off";
            if (IsTestMode)
            {
                DetailText = "Test status shown locally. BLE writes are disabled while test mode is on.";
                return;
            }

            DetailText = "Test status sent over BLE without interacting with Teams.";
            _ = connectionService.SendHostStatusAsync(true, CompanionTriState.Off, CompanionTriState.Off,
                "BLE test status");
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            HostLog.Write("Main window loaded. TestMode=" + IsTestMode);
            RefreshTeamsPresence();
            connectionService.Start();
            statusTimer.Start();
        }

        private void OnClosing(object sender, CancelEventArgs e)
        {
            HostLog.Write("Main window close requested; hiding to tray.");
            e.Cancel = true;
            Hide();
        }

        private void ToggleMute_Click(object sender, RoutedEventArgs e)
        {
            ToggleMuteFromUi();
        }

        private void SimulateX3Mute_Click(object sender, RoutedEventArgs e)
        {
            SimulateX3MutePress();
        }

        private void SendTestStatus_Click(object sender, RoutedEventArgs e)
        {
            SendTestStatus();
        }

        private void CycleTestMicrophone_Click(object sender, RoutedEventArgs e)
        {
            testMicrophone = NextTriState(testMicrophone);
            TestMicrophoneToggleText = TriStateText(testMicrophone, "Muted", "Live");
            OnTestStatusChanged("microphone");
        }

        private void CycleTestCamera_Click(object sender, RoutedEventArgs e)
        {
            testCamera = NextTriState(testCamera);
            TestCameraToggleText = TriStateText(testCamera, "Off", "Active");
            OnTestStatusChanged("camera");
        }

        private void OpenLog_Click(object sender, RoutedEventArgs e)
        {
            OpenLog();
        }

        private void Hide_Click(object sender, RoutedEventArgs e)
        {
            Hide();
        }

        public void OpenLog()
        {
            HostLog.Write("Open log requested.");
            var logPath = HostLog.LogPath;
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "/select,\"" + logPath + "\"",
                UseShellExecute = true
            });
        }

        private void RefreshTeamsPresence()
        {
            if (IsTeamsDryRun)
            {
                TeamsText = "Dry run";
                CameraText = "Unknown";
                MicrophoneText = "Unknown";
                return;
            }

            TeamsText = teamsController.IsTeamsRunning ? "Running" : "Not detected";
            CameraText = "Unknown";
            MicrophoneText = "Unknown";
        }

        private void OnStatusTimerTick(object sender, System.EventArgs e)
        {
            if (IsTestMode)
            {
                ApplyTestStatusToUi();
                SendTestHostStatus("timer");
                return;
            }

            RefreshTeamsPresence();
            SendCurrentHostStatus();
        }

        private void OnConnectionStatusChanged(object sender, CompanionConnectionStatus status)
        {
            Dispatcher.Invoke(() =>
            {
                HostLog.Write("UI status received. connected=" + status.IsConnected + " message=" + status.Message);
                ConnectionText = status.IsConnected ? "Connected" : "Disconnected";
                DetailText = status.Message;
                if (status.IsConnected)
                {
                    SendCurrentHostStatus();
                }
            });
        }

        private void OnDeviceCommandReceived(object sender, CompanionDeviceCommand command)
        {
            HostLog.Write("UI device command received. command=" + command);
            if (command != CompanionDeviceCommand.ToggleMute)
            {
                return;
            }

            Dispatcher.Invoke(ToggleMuteFromUi);
        }

        private void ApplyTestMode()
        {
            HostLog.Write("Apply test mode. enabled=" + IsTestMode);
            if (IsTestMode)
            {
                TestModeText = "On";
                ApplyTestStatusToUi();
                DetailText = "Test mode is active. BLE stays connected and sends the simulated status to the X3.";
                connectionService.Start();
                SendTestHostStatus("enabled");
                return;
            }

            TestModeText = "Off";
            ConnectionText = "Disconnected";
            DetailText = "Test mode disabled. Scanning for the X3 companion service.";
            connectionService.Start();
        }

        private void ApplyTeamsDryRun()
        {
            HostLog.Write("Apply Teams dry run. enabled=" + IsTeamsDryRun);
            TeamsModeText = IsTeamsDryRun ? "Dry run" : "Live Teams";
            RefreshTeamsPresence();
            DetailText = IsTeamsDryRun
                ? "Teams dry run is active. BLE stays connected, but Teams will not be focused or controlled."
                : "Teams dry run disabled. X3 mute commands will control Teams.";
            if (!IsTestMode)
            {
                SendCurrentHostStatus();
            }
        }

        private void SendCurrentHostStatus()
        {
            if (IsTestMode)
            {
                SendTestHostStatus("current");
                return;
            }

            if (IsTeamsDryRun)
            {
                _ = connectionService.SendHostStatusAsync(true, CompanionTriState.Unknown, CompanionTriState.Unknown,
                    "Teams dry run");
                return;
            }

            _ = connectionService.SendHostStatusAsync(teamsController.IsTeamsRunning, CompanionTriState.Unknown,
                CompanionTriState.Unknown, teamsController.IsTeamsRunning ? "Teams running" : "Teams not found");
        }

        private void OnTestStatusChanged(string reason)
        {
            if (!IsTestMode)
            {
                return;
            }

            SendTestHostStatus(reason);
        }

        private void SendTestHostStatus(string reason)
        {
            ApplyTestStatusToUi();
            var message = string.IsNullOrWhiteSpace(TestMessage) ? "Test status" : TestMessage;
            DetailText = "Test mode sent " + reason + ": teams=" + TestTeamsToggleText +
                ", mic=" + TestMicrophoneToggleText + ", camera=" + TestCameraToggleText + ".";
            _ = connectionService.SendHostStatusAsync(TestTeamsDetected, testMicrophone, testCamera, message);
        }

        private void ApplyTestStatusToUi()
        {
            TeamsText = TestTeamsDetected ? "Detected (test)" : "Not detected (test)";
            MicrophoneText = TestMicrophoneToggleText + " (test)";
            CameraText = TestCameraToggleText + " (test)";
        }

        private static CompanionTriState NextTriState(CompanionTriState value)
        {
            if (value == CompanionTriState.Unknown)
            {
                return CompanionTriState.Off;
            }

            return value == CompanionTriState.Off ? CompanionTriState.On : CompanionTriState.Unknown;
        }

        private static string TriStateText(CompanionTriState value, string offText, string onText)
        {
            if (value == CompanionTriState.Off)
            {
                return offText;
            }

            return value == CompanionTriState.On ? onText : "Unknown";
        }

        private bool SetField(ref string field, string value, string propertyName)
        {
            if (field == value)
            {
                return false;
            }

            field = value;
            if (PropertyChanged != null)
            {
                PropertyChanged(this, new PropertyChangedEventArgs(propertyName));
            }

            return true;
        }

        private bool SetField(ref bool field, bool value, string propertyName)
        {
            if (field == value)
            {
                return false;
            }

            field = value;
            if (PropertyChanged != null)
            {
                PropertyChanged(this, new PropertyChangedEventArgs(propertyName));
            }

            return true;
        }
    }
}
