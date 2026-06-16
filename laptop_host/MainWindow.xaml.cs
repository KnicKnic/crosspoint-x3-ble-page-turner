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

        public string DetailText
        {
            get { return detailText; }
            private set { SetField(ref detailText, value, nameof(DetailText)); }
        }

        public void ToggleMuteFromUi()
        {
            HostLog.Write("Toggle mute requested.");
            RefreshTeamsPresence();
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
            RefreshTeamsPresence();
            MicrophoneText = "Muted";
            CameraText = "Off";
            DetailText = "Test status shown locally. BLE writes are disabled while test mode is on.";
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            HostLog.Write("Main window loaded. TestMode=" + IsTestMode);
            RefreshTeamsPresence();
            if (!IsTestMode)
            {
                connectionService.Start();
            }
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
            TeamsText = teamsController.IsTeamsRunning ? "Running" : "Not detected";
            CameraText = "Unknown";
            MicrophoneText = "Unknown";
        }

        private void OnStatusTimerTick(object sender, System.EventArgs e)
        {
            RefreshTeamsPresence();
            if (IsTestMode)
            {
                return;
            }

            _ = connectionService.SendHostStatusAsync(teamsController.IsTeamsRunning, CompanionTriState.Unknown,
                CompanionTriState.Unknown, teamsController.IsTeamsRunning ? "Teams running" : "Teams not found");
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
                    _ = connectionService.SendHostStatusAsync(teamsController.IsTeamsRunning, CompanionTriState.Unknown,
                        CompanionTriState.Unknown, teamsController.IsTeamsRunning ? "Teams running" : "Teams not found");
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
                connectionService.Stop();
                ConnectionText = "Test mode";
                TestModeText = "On";
                DetailText = "Test mode is active. BLE scanning is stopped; use Simulate X3 Press to test the host command path.";
                return;
            }

            TestModeText = "Off";
            ConnectionText = "Disconnected";
            DetailText = "Test mode disabled. Scanning for the X3 companion service.";
            connectionService.Start();
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
