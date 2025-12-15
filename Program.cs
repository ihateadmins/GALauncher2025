// Program.cs - single file (C# 3.0 / .NET Framework 3.5 compatible)
// Agendorks Launcher 2025 - Final Working Version (December 15, 2025)

using GALauncher2025.Properties;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace GALauncher2025
{
    internal static class VLog
    {
        private static readonly object _lock = new object();
        private static StreamWriter _writer;
        private static long _seq;
        private static string _path;
        private static bool _ready;

        public static bool Ready { get { return _ready; } }
        public static string PathName { get { return _path; } }

        public static void Init(string launcherDir)
        {
            try
            {
                _path = Path.Combine(launcherDir, "log.txt");

                try
                {
                    if (File.Exists(_path))
                    {
                        string rotated = Path.Combine(launcherDir, "log_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
                        File.Move(_path, rotated);
                    }
                }
                catch { }

                FileStream fs = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                _writer = new StreamWriter(fs, Encoding.UTF8) { AutoFlush = true };

                _ready = true;

                Write("LOGGER", "Init OK");
                Write("LOGGER", "Exe=" + Application.ExecutablePath);
                Write("LOGGER", "StartupPath=" + Application.StartupPath);
                Write("LOGGER", "CWD=" + Environment.CurrentDirectory);
                string[] args = Environment.GetCommandLineArgs();
                Write("LOGGER", "Args=" + (args == null ? "" : string.Join(" ", args)));
            }
            catch { _ready = false; }
        }

        public static void Shutdown()
        {
            try
            {
                lock (_lock)
                {
                    if (_writer != null)
                    {
                        _writer.Flush();
                        _writer.Close();
                        _writer = null;
                    }
                }
            }
            catch { }
            _ready = false;
        }

        public static void Write(string tag, string message)
        {
            try
            {
                if (!_ready) return;

                long n = Interlocked.Increment(ref _seq);
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " | #" + n + " | T" + Thread.CurrentThread.ManagedThreadId + " | " + (tag ?? "LOG") + " | " + (message ?? "");

                lock (_lock)
                {
                    if (_writer != null) _writer.WriteLine(line);
                }
            }
            catch { }
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            VLog.Init(Application.StartupPath);

            AppDomain.CurrentDomain.ProcessExit += delegate { VLog.Write("APP", "ProcessExit fired"); };

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            Application.ThreadException += delegate (object sender, ThreadExceptionEventArgs e)
            {
                VLog.Write("EXCEPTION", "Application.ThreadException: " + (e.Exception?.ToString() ?? "(null)"));
                try { MessageBox.Show(e.Exception?.ToString() ?? "Unknown error", "Launcher error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                catch { }
            };

            AppDomain.CurrentDomain.UnhandledException += delegate (object sender, UnhandledExceptionEventArgs e)
            {
                Exception ex = e.ExceptionObject as Exception;
                VLog.Write("EXCEPTION", "AppDomain.UnhandledException: " + (ex?.ToString() ?? "(null)"));
                try { MessageBox.Show(ex?.ToString() ?? "Unknown error", "Unhandled error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                catch { }
            };

            Application.ApplicationExit += delegate
            {
                VLog.Write("APP", "ApplicationExit");
                NativeHandles.CleanupHandleLogs();
                VLog.Shutdown();
            };

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            VLog.Write("APP", "Starting MainForm");
            Application.Run(new MainForm());
        }
    }

    public sealed class MainForm : Form
    {
        private const string ARGAUTOSTART = "--autostart";

        private const string STEAMAPPID = "17020";

        private const string DISCORDAPPID = "942352872585191445";
        private const string RPCSTATE = "Playing Global Agenda - Agendorks";
        private const string RPCDETAILS = "Is playing via Multiloginscript-Launcher 2025...";
        private const string RPCLARGEIMAGEKEY = "gamelogo";
        private const string RPCLARGEIMAGETEXT = "Global Agenda - Agendorks active!";

        private const string DEFAULTGAMEARGS = "-host=agendorks.ydns.eu -hostdns=gaserveragendorks -seekfreeloading -nostartupmovies -nosplash -tcp=300";

        private const int HOTKEYIDF8 = 1;
        private const int WMHOTKEY = 0x0312;

        private readonly string launcherDir = Application.StartupPath;
        private string AccountsDbPath => Path.Combine(launcherDir, "accounts.db");
        private string ConfigIniPath => Path.Combine(launcherDir, "loginscriptconfig.ini");

        private TextBox txtGameExe;
        private Button btnBrowse;
        private TextBox txtUsername;
        private TextBox txtPassword;
        private CheckBox chkShowPassword;
        private TextBox txtClientAmount;
        private TextBox txtGameArgs;
        private CheckBox chkWindowed;
        private CheckBox chkLog;
        private CheckBox chkBotMode;
        private NumericUpDown numResX;
        private NumericUpDown numResY;
        private Button btnStart;
        private Button btnStop;
        private Button btnSave;
        private Button btnDiscord;
        private TextBox txtLog;
        private Panel pnlMenuBackground;

        private const string ICONFILE = "app.ico";
        private const string MENUBGFILE = "menubg.jpg";
        private const string DISCORDLOGOFILE = "Discord-Logo-Blurple.png";

        private readonly List<Process> clientList = new List<Process>();
        private readonly List<IntPtr> hwndList = new List<IntPtr>();
        private readonly Dictionary<int, string> pidAccountMap = new Dictionary<int, string>();
        private readonly HashSet<int> loggedInSet = new HashSet<int>();
        private readonly Dictionary<int, int> pidMapConfirm = new Dictionary<int, int>();
        private readonly object stateLock = new object();
        private volatile bool stopRequested;

        private string targetGameExe = "GlobalAgenda.exe";
        private string targetGameArgs = "";
        private bool setFpsForBots;
        private int botMaxSmoothedFrameRate = 1;

        private int screenWidth;
        private int screenHeight;

        private readonly int version = int.Parse(File.GetLastWriteTime(Application.ExecutablePath).ToString("yyyyMMdd"));
        private readonly bool autoStartRequested;

        private System.Windows.Forms.Timer discordRpcCbTimer;
        private int discordRpcCbReentry;
        private volatile bool closing;

        private long launchUnixStart;

        private Thread heartbeatThread;
        private long hbTick;

        private const int MAP_CONFIRM_NEEDED = 1;

        public MainForm()
        {
            autoStartRequested = HasLauncherFlag(ARGAUTOSTART);

            KeyPreview = true;
            KeyDown += MainFormKeyDown;

            InitializeControls();

            Log("FORM: ctor (autoStartRequested=" + autoStartRequested + ", version=" + version + ")");

            Load += delegate
            {
                Log("FORM: Load begin");

                screenWidth = Screen.PrimaryScreen.Bounds.Width;
                screenHeight = Screen.PrimaryScreen.Bounds.Height;

                // Load config first
                LoadLoginConfig();
                LoadSettingsToUi();

                // Then apply defaults only if fields are empty
                if (string.IsNullOrWhiteSpace(txtGameExe.Text)) txtGameExe.Text = "GlobalAgenda.exe";
                if (string.IsNullOrWhiteSpace(txtPassword.Text)) txtPassword.Text = "a";
                if (string.IsNullOrWhiteSpace(txtClientAmount.Text)) txtClientAmount.Text = "7";
                if (string.IsNullOrWhiteSpace(txtGameArgs.Text)) txtGameArgs.Text = DEFAULTGAMEARGS;

                SyncGameArgsFromUi();

                Log("FORM: Ready.");

                if (autoStartRequested)
                {
                    Log("FORM: Auto-start requested");
                    BeginInvoke((MethodInvoker)delegate { BtnStartClick(null, EventArgs.Empty); });
                }
            };

            FormClosing += delegate
            {
                Log("FORM: FormClosing");
                stopRequested = true;
                closing = true;

                try
                {
                    if (discordRpcCbTimer != null)
                    {
                        discordRpcCbTimer.Stop();
                        discordRpcCbTimer.Dispose();
                    }
                }
                catch (Exception ex) { Log("DISCORD: timer stop error: " + ex); }

                try { DiscordRpc.SafeClear(); DiscordRpc.Shutdown(); }
                catch (Exception ex) { Log("DISCORD: Shutdown error: " + ex); }

                try { SteamApi.Shutdown(); }
                catch (Exception ex) { Log("STEAM: Shutdown error: " + ex); }

                NativeHandles.CleanupHandleLogs();

                Log("FORM: FormClosing end");
            };
        }

        private void Log(string message)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " | T" + Thread.CurrentThread.ManagedThreadId + " | stop=" + stopRequested + " | " + message;

            VLog.Write("TRACE", line);

            SafeUi(delegate
            {
                try { txtLog?.AppendText(line + Environment.NewLine); }
                catch { }
            });
        }

        private void RequestClose(string reason)
        {
            Log("EXIT: " + reason);
            SafeUi(delegate { try { Close(); } catch { } });
        }

        private void DumpState(string tag)
        {
            lock (stateLock)
            {
                Log($"STATE: {tag} clients={clientList.Count} hwnds={hwndList.Count} pidMap={pidAccountMap.Count} logged={loggedInSet.Count} confirm={pidMapConfirm.Count}");
            }
        }

        private void InitializeControls()
        {
            Color bg = Color.FromArgb(26, 26, 30);
            Color fg = Color.Yellow;

            Text = "Agendorks Launcher 2025";
            Size = new Size(760, 650);
            StartPosition = FormStartPosition.CenterScreen;
            Padding = new Padding(10);
            BackColor = bg;
            ForeColor = fg;
            DoubleBuffered = true;

            try
            {
                string iconPath = Path.Combine(Application.StartupPath, ICONFILE);
                if (File.Exists(iconPath)) Icon = new Icon(iconPath);
                else Icon = Resources.app;
            }
            catch { }

            pnlMenuBackground = new Panel { Dock = DockStyle.Fill, BackColor = bg, BackgroundImageLayout = ImageLayout.Stretch };

            try
            {
                string imgPath = Path.Combine(Application.StartupPath, MENUBGFILE);
                if (File.Exists(imgPath)) pnlMenuBackground.BackgroundImage = Image.FromFile(imgPath);
                else pnlMenuBackground.BackgroundImage = Resources.menu_bg;
            }
            catch { }

            Controls.Add(pnlMenuBackground);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.BackColor = Color.Transparent;
            pnlMenuBackground.Controls.Add(root);

            var table = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, AutoSize = true, Padding = new Padding(0, 0, 0, 8) };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            table.BackColor = Color.Transparent;

            int row = 0;
            Func<string, Label> mkLabel = t => new Label { Text = t, AutoSize = true, ForeColor = fg, BackColor = Color.Transparent, Margin = new Padding(0, 6, 6, 6) };

            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(mkLabel("Game EXE:"), 0, row);
            txtGameExe = new TextBox { Width = 520 };
            btnBrowse = new Button { Text = "Browse", Width = 90, BackColor = Color.DarkGray };
            btnBrowse.Click += BtnBrowseClick;
            table.Controls.Add(txtGameExe, 1, row);
            table.Controls.Add(btnBrowse, 2, row);
            row++;

            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(mkLabel("Username:"), 0, row);
            txtUsername = new TextBox { Width = 520 };
            table.Controls.Add(txtUsername, 1, row);
            table.SetColumnSpan(txtUsername, 2);
            row++;

            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(mkLabel("Password:"), 0, row);
            var pwPanel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, BackColor = Color.Transparent };
            txtPassword = new TextBox { Width = 410, PasswordChar = '*' };
            chkShowPassword = new CheckBox { Text = "Show", AutoSize = true, Margin = new Padding(10, 6, 0, 0), ForeColor = fg, BackColor = Color.Transparent };
            chkShowPassword.CheckedChanged += delegate { txtPassword.PasswordChar = chkShowPassword.Checked ? '\0' : '*'; };
            pwPanel.Controls.Add(txtPassword);
            pwPanel.Controls.Add(chkShowPassword);
            table.Controls.Add(pwPanel, 1, row);
            table.SetColumnSpan(pwPanel, 2);
            row++;

            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(mkLabel("Client Amount:"), 0, row);
            txtClientAmount = new TextBox { Width = 120 };
            txtClientAmount.TextChanged += TxtClientAmount_TextChanged;
            table.Controls.Add(txtClientAmount, 1, row);
            table.SetColumnSpan(txtClientAmount, 2);
            row++;

            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(mkLabel("Game args:"), 0, row);
            txtGameArgs = new TextBox { Width = 520 };
            table.Controls.Add(txtGameArgs, 1, row);
            table.SetColumnSpan(txtGameArgs, 2);
            row++;

            var gbSettings = new GroupBox { Text = "SETTINGS", Dock = DockStyle.Top, AutoSize = true, ForeColor = fg, BackColor = Color.Transparent };
            var settingsTable = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8), ColumnCount = 6, BackColor = Color.Transparent };

            chkWindowed = new CheckBox { Text = "Windowed", Checked = true, ForeColor = fg, BackColor = Color.Transparent };
            chkLog = new CheckBox { Text = "Log", ForeColor = fg, BackColor = Color.Transparent };
            chkBotMode = new CheckBox { Text = "Botmode", ForeColor = fg, BackColor = Color.Transparent };

            chkWindowed.CheckedChanged += delegate { SyncGameArgsFromUi(); };
            chkLog.CheckedChanged += delegate { SyncGameArgsFromUi(); };
            chkBotMode.CheckedChanged += delegate { SyncGameArgsFromUi(); };

            settingsTable.Controls.Add(chkWindowed, 0, 0);
            settingsTable.Controls.Add(chkLog, 1, 0);
            settingsTable.Controls.Add(chkBotMode, 2, 0);

            settingsTable.Controls.Add(mkLabel("ResX:"), 0, 1);
            numResX = new NumericUpDown { Minimum = 64, Maximum = 7680, Value = 186, Width = 90 };
            settingsTable.Controls.Add(numResX, 1, 1);

            settingsTable.Controls.Add(mkLabel("ResY:"), 2, 1);
            numResY = new NumericUpDown { Minimum = 64, Maximum = 4320, Value = 91, Width = 90 };
            settingsTable.Controls.Add(numResY, 3, 1);

            numResX.ValueChanged += delegate { SyncGameArgsFromUi(); };
            numResY.ValueChanged += delegate { SyncGameArgsFromUi(); };

            gbSettings.Controls.Add(settingsTable);

            var buttonPanel = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, BackColor = Color.Transparent, FlowDirection = FlowDirection.LeftToRight, WrapContents = true };

            btnStart = new Button { Text = "Start", Width = 120, Height = 28, BackColor = Color.Green, FlatStyle = FlatStyle.Flat };
            btnStop = new Button { Text = "Stop (F8)", Width = 120, Height = 28, BackColor = Color.Red, FlatStyle = FlatStyle.Flat, Enabled = false };
            btnSave = new Button { Text = "Save", Width = 120, Height = 28, BackColor = Color.Blue, FlatStyle = FlatStyle.Flat };

            btnDiscord = new Button { Width = 300, Height = 40, Text = "", FlatStyle = FlatStyle.Flat, BackColor = Color.Black, ImageAlign = ContentAlignment.MiddleCenter, Margin = new Padding(6, 0, 0, 0), UseVisualStyleBackColor = false };
            btnDiscord.FlatAppearance.BorderSize = 0;

            try
            {
                string pngPath = Path.Combine(Application.StartupPath, DISCORDLOGOFILE);
                if (File.Exists(pngPath))
                {
                    using (var fs = new FileStream(pngPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var tmp = Image.FromStream(fs))
                        btnDiscord.Image = new Bitmap(tmp, new Size(20, 20));
                }
                else btnDiscord.Image = Resources.Discord_Logo_Blurple;
            }
            catch { }

            btnStart.Click += BtnStartClick;
            btnStop.Click += delegate { StopAutomationOnly(); };
            btnSave.Click += delegate { SyncGameArgsFromUi(); SaveLoginConfig(); SaveSettingsFromUi(); Log("UI: Saved config."); };
            btnDiscord.Click += BtnDiscordClick;

            buttonPanel.Controls.Add(btnStart);
            buttonPanel.Controls.Add(btnStop);
            buttonPanel.Controls.Add(btnSave);
            buttonPanel.Controls.Add(btnDiscord);

            var topPanel = new Panel { Dock = DockStyle.Top, AutoScroll = true, Height = 290, BackColor = Color.Transparent };
            topPanel.Controls.Add(buttonPanel);
            topPanel.Controls.Add(gbSettings);
            topPanel.Controls.Add(table);

            txtLog = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = false, BackColor = bg, ForeColor = fg };

            root.Controls.Add(topPanel, 0, 0);
            root.Controls.Add(txtLog, 0, 1);

            ApplyTheme(this, bg, fg);
        }

        private void TxtClientAmount_TextChanged(object sender, EventArgs e)
        {
            if (int.TryParse(txtClientAmount.Text, out int value) && value < 7)
            {
                MessageBox.Show("WARNING: DO NOT go below the default 7. I didn't choose it for no reason!", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void ApplyTheme(Control c, Color bg, Color fg)
        {
            if (c == null) return;
            c.ForeColor = fg;
            if (c is TextBoxBase || c is NumericUpDown || c is ComboBox) c.BackColor = bg;
            foreach (Control child in c.Controls) ApplyTheme(child, bg, fg);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { RegisterHotKey(Handle, HOTKEYIDF8, 0u, (uint)Keys.F8); } catch { }
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            try { UnregisterHotKey(Handle, HOTKEYIDF8); } catch { }
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WMHOTKEY && m.WParam.ToInt32() == HOTKEYIDF8)
            {
                Log("HOTKEY: F8");
                StopAutomationOnly();
            }
            base.WndProc(ref m);
        }

        private void MainFormKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F8)
            {
                Log("KEYDOWN: F8");
                StopAutomationOnly();
                e.Handled = true;
            }
        }

        private void BtnBrowseClick(object sender, EventArgs e)
        {
            using (var ofd = new OpenFileDialog { Filter = "Executable (*.exe)|*.exe|All files (*.*)|*.*", Title = "Select GlobalAgenda.exe" })
            {
                if (ofd.ShowDialog(this) == DialogResult.OK)
                {
                    txtGameExe.Text = ofd.FileName;
                    Log("UI: Selected EXE=" + ofd.FileName);
                }
            }
        }

        private void BtnStartClick(object sender, EventArgs e)
        {
            if (!btnStart.Enabled) return;

            SyncGameArgsFromUi();
            SaveLoginConfig();
            SaveSettingsFromUi();

            btnStart.Enabled = false;
            btnStop.Enabled = true;
            stopRequested = false;

            lock (stateLock)
            {
                clientList.Clear();
                hwndList.Clear();
                pidAccountMap.Clear();
                loggedInSet.Clear();
                pidMapConfirm.Clear();
            }

            DumpState("after reset");

            new Thread(MainLogic) { IsBackground = true }.Start();
        }

        private void BtnDiscordClick(object sender, EventArgs e)
        {
            try { Process.Start(new ProcessStartInfo { FileName = "https://discord.gg/EeHwBpCPsK", UseShellExecute = true }); }
            catch (Exception ex) { Log("UI: Discord open error: " + ex); }
        }

        private void StopAutomationOnly()
        {
            Log("UI: StopAutomationOnly");
            stopRequested = true;
            SafeUi(delegate { btnStart.Enabled = true; btnStop.Enabled = false; });
        }

        private void SyncGameArgsFromUi()
        {
            BuildArgsFromConfigAndUi();
            SafeUi(delegate { txtGameArgs.Text = targetGameArgs; });
            Log("ARGS: " + targetGameArgs);
        }

        private static string NormalizeArgs(string s) => Regex.Replace((s ?? "").Replace("\r", " ").Replace("\n", " ").Replace("\t", " "), @"\s+", " ").Trim();

        private static string RemoveToken(string args, string token) => NormalizeArgs(Regex.Replace(NormalizeArgs(args), @"(^|\s)" + Regex.Escape(token) + @"(?=\s|$)", " ", RegexOptions.IgnoreCase));

        private static string RemoveLogArg(string args) => NormalizeArgs(Regex.Replace(NormalizeArgs(args), @"(^|\s)-log(=[^\s]*)?(?=\s|$)", " ", RegexOptions.IgnoreCase));

        private static string SetOrAppendKeyValue(string args, string key, string value)
        {
            args = NormalizeArgs(args);
            string pattern = @"\b" + Regex.Escape(key) + @"=\d+\b";
            return Regex.IsMatch(args, pattern, RegexOptions.IgnoreCase)
                ? NormalizeArgs(Regex.Replace(args, pattern, key + "=" + value, RegexOptions.IgnoreCase))
                : NormalizeArgs(args + " " + key + "=" + value);
        }

        private void MainLogic()
        {
            Log("MAIN: entered");

            StartHeartbeat();

            try
            {
                bool elevated = IsElevated();
                Log("MAIN: IsElevated=" + elevated);

                if (!elevated)
                {
                    SaveLoginConfig();
                    SaveSettingsFromUi();
                    RestartAsAdminWithAutoStart();
                    RequestClose("Restarted as admin");
                    return;
                }

                launchUnixStart = UnixNow();

                NativeHandles.TryEnableSeDebugPrivilege();

                targetGameExe = SafeReadUi(() => txtGameExe.Text?.Trim() ?? "");
                if (string.IsNullOrWhiteSpace(targetGameExe)) targetGameExe = "GlobalAgenda.exe";

                ResolveGamePathOrSearch();

                TryInitSteamPresenceFromGameDir();
                TryInitDiscordPresenceViaRpcDll();

                BuildArgsFromConfigAndUi();
                Log("MAIN: Final args=" + targetGameArgs);

                WriteTgDevIni();
                PatchTgEngineIniIfNeeded();

                bool accountsDbAvailable;
                int requestedClientCount;
                var loginStrings = BuildLoginStrings(out accountsDbAvailable, out requestedClientCount);
                int clientCount = accountsDbAvailable ? loginStrings.Count : requestedClientCount;

                AddGameProcesses(clientCount);
                Thread.Sleep(5000 + 2000 * clientCount);

                MoveGameWindows();
                SendLoginsToWindows(loginStrings, accountsDbAvailable);
                StartLoginSpamThreads();

                MonitorUntilLoggedIn(accountsDbAvailable);
            }
            catch (Exception ex) { Log("FATAL: " + ex); }
            finally
            {
                SafeUi(delegate { btnStart.Enabled = true; btnStop.Enabled = false; });
            }
        }

        private void TryInitDiscordPresenceViaRpcDll()
        {
            try
            {
                DiscordRpc.Initialize(DISCORDAPPID,
                    u => Log("DISCORD: READY " + u),
                    (c, m) => Log("DISCORD: DISCONNECTED " + c + " " + m),
                    (c, m) => Log("DISCORD: ERROR " + c + " " + m));

                SafeUi(delegate
                {
                    if (discordRpcCbTimer != null)
                    {
                        discordRpcCbTimer.Stop();
                        discordRpcCbTimer.Dispose();
                    }

                    discordRpcCbTimer = new System.Windows.Forms.Timer { Interval = 200 };
                    discordRpcCbTimer.Tick += delegate
                    {
                        if (closing || Interlocked.Exchange(ref discordRpcCbReentry, 1) == 1) return;
                        try { DiscordRpc.RunCallbacks(); }
                        catch (Exception ex) { Log("DISCORD: RunCallbacks error: " + ex); }
                        finally { Interlocked.Exchange(ref discordRpcCbReentry, 0); }
                    };
                    discordRpcCbTimer.Start();

                    DiscordRpc.SetPresence(RPCSTATE, RPCDETAILS, launchUnixStart, RPCLARGEIMAGEKEY, RPCLARGEIMAGETEXT);
                });

                Log("DISCORD: Rich presence enabled");
            }
            catch (Exception ex) { Log("DISCORD: init error: " + ex); }
        }

        private void StartHeartbeat()
        {
            if (heartbeatThread != null) return;
            heartbeatThread = new Thread(() =>
            {
                while (!stopRequested)
                {
                    Interlocked.Increment(ref hbTick);
                    Thread.Sleep(1000);
                }
            })
            { IsBackground = true };
            heartbeatThread.Start();
        }

        private void BuildArgsFromConfigAndUi()
        {
            string args = SafeReadUi(() => txtGameArgs.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(args)) args = DEFAULTGAMEARGS;

            bool windowed = SafeReadUi(() => chkWindowed.Checked);
            bool log = SafeReadUi(() => chkLog.Checked);
            bool botMode = SafeReadUi(() => chkBotMode.Checked);
            int resX = (int)SafeReadUi(() => numResX.Value);
            int resY = (int)SafeReadUi(() => numResY.Value);

            args = NormalizeArgs(args);
            args = RemoveToken(args, "-windowed");
            if (windowed) args += " -windowed";
            args = RemoveLogArg(args);
            if (log) args += " -log";
            args = SetOrAppendKeyValue(args, "ResX", resX.ToString());
            args = SetOrAppendKeyValue(args, "ResY", resY.ToString());

            if (botMode && !Regex.IsMatch(args, @"\bmaxfps=\d+\b", RegexOptions.IgnoreCase))
                args += " maxfps=1";

            setFpsForBots = botMode || args.IndexOf("bot", StringComparison.OrdinalIgnoreCase) >= 0;

            Match m = Regex.Match(args, @"\bmaxfps=(\d+)\b", RegexOptions.IgnoreCase);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int fps)) botMaxSmoothedFrameRate = fps;

            targetGameArgs = NormalizeArgs(args);
        }

        private void TryInitSteamPresenceFromGameDir()
        {
            try
            {
                if (!IsProcessRunning("steam")) return;

                Environment.SetEnvironmentVariable("SteamAppId", STEAMAPPID, EnvironmentVariableTarget.Process);
                string dllPath = Path.Combine(Directory.GetCurrentDirectory(), "steam_api.dll");
                SteamApi.TryInitFromPath(dllPath);
            }
            catch (Exception ex) { Log("STEAM: init error: " + ex); }
        }

        private void AddGameProcesses(int count)
        {
            lock (stateLock) clientList.Clear();

            for (int i = 0; i < count && !stopRequested; i++)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = targetGameExe,
                    Arguments = targetGameArgs,
                    WorkingDirectory = Directory.GetCurrentDirectory(),
                    UseShellExecute = false,
                    CreateNoWindow = false
                };

                Process p = Process.Start(psi);
                if (p != null)
                {
                    try { p.WaitForInputIdle(5000); } catch { }
                    lock (stateLock) clientList.Add(p);
                    Log("PROC: Launched PID=" + p.Id);
                }
            }

            DumpState("after AddGameProcesses");
        }

        private void MoveGameWindows()
        {
            Log("WIN: MoveGameWindows begin");
            lock (stateLock) hwndList.Clear();

            var procs = new List<Process>(clientList);
            foreach (var p in procs)
            {
                if (stopRequested) return;
                var found = GetHwndsForPid(p.Id);
                lock (stateLock) hwndList.AddRange(found);
            }

            if (hwndList.Count == 0) return;

            GetWindowRect(hwndList[0], out RECT r);
            int w = Math.Max(1, r.right - r.left);
            int h = Math.Max(1, r.bottom - r.top);
            if (w < 50 || h < 50)
            {
                w = (int)SafeReadUi(() => numResX.Value);
                h = (int)SafeReadUi(() => numResY.Value);
            }

            int cols = Math.Max(1, screenWidth / w);
            int x = 0, y = 0;
            foreach (var hwnd in hwndList)
            {
                if (stopRequested) return;
                MoveWindow(hwnd, x * w, y * h, w, h, true);
                x++;
                if (x >= cols) { x = 0; y++; }
                Thread.Sleep(30);
            }

            DumpState("after MoveGameWindows");
        }

        private void SendLoginsToWindows(List<string> loginStrings, bool usingAccountsDb)
        {
            int index = 0;
            int count = loginStrings.Count;
            foreach (var hwnd in hwndList)
            {
                if (stopRequested) return;
                string login = usingAccountsDb ? loginStrings[index++ % count] : loginStrings[0];
                int pid = GetProcessIdFromHwnd(hwnd);
                PostTextToWindow(hwnd, login);
                if (pid != 0) lock (stateLock) pidAccountMap[pid] = login;
                Thread.Sleep(150 + 10 * hwndList.Count);
            }

            DumpState("after SendLoginsToWindows");
        }

        private void StartLoginSpamThreads()
        {
            foreach (var hwnd in hwndList)
            {
                if (stopRequested) return;
                new Thread(() => LoginSpam(hwnd)) { IsBackground = true }.Start();
            }
        }

        private void LoginSpam(IntPtr hwnd)
        {
            int pid = GetProcessIdFromHwnd(hwnd);
            var rand = new Random();
            while (!stopRequested)
            {
                lock (stateLock) { if (loggedInSet.Contains(pid)) return; }
                PressEnter(hwnd);
                Thread.Sleep((int)(rand.NextDouble() * 9800 + 5200));
            }
        }

        private bool PidIsInGameMap(int pid, out string matchedPath)
        {
            return NativeHandles.TryGetOpenFileMatch(pid, @"tggame\cookedpc\maps\", "login", out matchedPath);
        }

        private void MonitorUntilLoggedIn(bool usingAccountsDb)
        {
            Log("MON: MonitorUntilLoggedIn begin usingAccountsDb=" + usingAccountsDb);

            DateTime lastScan = DateTime.MinValue;

            while (!stopRequested)
            {
                lock (stateLock)
                {
                    if (hwndList.Count > 0 && loggedInSet.Count >= hwndList.Count)
                    {
                        Log("MON: finished");
                        return;
                    }
                }

                if ((DateTime.Now - lastScan).TotalSeconds >= 10)
                {
                    lastScan = DateTime.Now;
                    var localHwnds = new List<IntPtr>(hwndList);
                    foreach (var hwnd in localHwnds)
                    {
                        if (stopRequested) return;
                        int pid = GetProcessIdFromHwnd(hwnd);
                        if (pid == 0 || loggedInSet.Contains(pid)) continue;

                        string matched;
                        bool inMap = PidIsInGameMap(pid, out matched);

                        int confirm;
                        lock (stateLock)
                        {
                            int c = pidMapConfirm.TryGetValue(pid, out c) ? c : 0;
                            pidMapConfirm[pid] = inMap ? c + 1 : 0;
                            confirm = inMap ? c + 1 : 0;
                        }

                        if (inMap)
                            Log($"MAPCHK: PID {pid} opened map file: {matched}");

                        if (confirm >= MAP_CONFIRM_NEEDED)
                        {
                            lock (stateLock) loggedInSet.Add(pid);
                            Log($"MON: MAP CONFIRMED PID {pid}");

                            if (!usingAccountsDb)
                            {
                                foreach (var p in clientList)
                                    if (p.Id != pid) TryKillPid(p.Id);

                                Thread.Sleep(20000);
                                PressEnter(hwnd);
                                Thread.Sleep(250);
                                PostTextToWindow(hwnd, "/setres " + screenWidth + "x" + screenHeight + "b");
                                Thread.Sleep(150);
                                PressEnter(hwnd);
                                RequestClose("Single-account finished");
                                return;
                            }
                        }
                    }
                }

                Thread.Sleep(500);
            }
        }

        private void TryKillPid(int pid)
        {
            try { Process.GetProcessById(pid).Kill(); Log("KILL: Killed PID " + pid); }
            catch (Exception ex) { Log("KILL: Error killing PID " + pid + ": " + ex); }
        }

        private void PostTextToWindow(IntPtr hwnd, string text)
        {
            if (hwnd == IntPtr.Zero || string.IsNullOrWhiteSpace(text)) return;
            foreach (char ch in text)
            {
                if (ch == '\t') PressTab(hwnd);
                else if (ch == '\r' || ch == '\n') PressEnter(hwnd);
                else PostMessage(hwnd, WMCHAR, (int)ch, 0);
                Thread.Sleep(20);
            }
        }

        private void PressTab(IntPtr hwnd) { PostVirtualKey(hwnd, 0x09, false); Thread.Sleep(10); PostVirtualKey(hwnd, 0x09, true); }
        private void PressEnter(IntPtr hwnd) { PostVirtualKey(hwnd, 0x0D, false); PostMessage(hwnd, WMCHAR, '\r', 0); Thread.Sleep(10); PostVirtualKey(hwnd, 0x0D, true); }

        private void PostVirtualKey(IntPtr hwnd, int vk, bool up)
        {
            uint scan = MapVirtualKey((uint)vk, 0);
            int lParam = 1 | ((int)scan << 16) | (up ? ((1 << 30) | (1 << 31)) : 0);
            PostMessage(hwnd, up ? WMKEYUP : WMKEYDOWN, vk, lParam);
        }

        private const uint WMKEYDOWN = 0x0100;
        private const uint WMKEYUP = 0x0101;
        private const uint WMCHAR = 0x0102;

        private void WriteTgDevIni()
        {
            try
            {
                string path = Path.Combine("..", "tggame", "Config", "TgDev.ini");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, @"[TgGame.TgDebug]
AutomateLogin=True
SaveAccountInfo=True
");
                Log("CFG: Wrote TgDev.ini");
            }
            catch (Exception ex) { Log("CFG: TgDev.ini error: " + ex); }
        }

        private void PatchTgEngineIniIfNeeded()
        {
            if (!setFpsForBots) return;
            try
            {
                string path = Path.Combine("..", "tggame", "Config", "TgEngine.ini");
                if (!File.Exists(path)) return;
                var lines = new List<string>(File.ReadAllLines(path));
                for (int i = 0; i < lines.Count; i++)
                {
                    if (lines[i].StartsWith("MinSmoothedFrameRate=", StringComparison.OrdinalIgnoreCase))
                    {
                        lines[i] = "MinSmoothedFrameRate=1.000000";
                        if (i + 1 < lines.Count) lines[i + 1] = "MaxSmoothedFrameRate=" + botMaxSmoothedFrameRate + ".000000";
                        File.WriteAllLines(path, lines);
                        Log("CFG: Patched TgEngine.ini");
                        return;
                    }
                }
            }
            catch (Exception ex) { Log("CFG: TgEngine.ini error: " + ex); }
        }

        private List<string> BuildLoginStrings(out bool accountsDbAvailable, out int requestedClientCount)
        {
            accountsDbAvailable = false;
            requestedClientCount = 7;

            var accounts = ReadAccountsDb();
            if (accounts.Count > 0)
            {
                accountsDbAvailable = true;
                var list = new List<string>();
                foreach (var a in accounts) list.Add(a.User + "\t" + a.Pass);
                Log("ACCT: accounts.db loaded count=" + accounts.Count);
                return list;
            }

            string user = SafeReadUi(() => txtUsername.Text?.Trim() ?? "");
            string pass = SafeReadUi(() => txtPassword.Text ?? "");
            int n = int.TryParse(SafeReadUi(() => txtClientAmount.Text?.Trim()), out n) ? (n < 1 ? 1 : (n > 100 ? 100 : n)) : 7;
            requestedClientCount = n;

            Log("ACCT: single login mode user=" + user + " count=" + n);
            return new List<string> { user + "\t" + pass };
        }

        private struct Account { public string User; public string Pass; }

        private List<Account> ReadAccountsDb()
        {
            var list = new List<Account>();
            if (!File.Exists(AccountsDbPath)) return list;
            try
            {
                foreach (string line in File.ReadAllLines(AccountsDbPath))
                {
                    string l = line.Trim();
                    if (l.Length == 0 || l.StartsWith("#")) continue;
                    string[] parts = l.Split(new[] { ':' }, 2);
                    if (parts.Length == 2 && parts[0].Trim().Length > 0)
                        list.Add(new Account { User = parts[0].Trim(), Pass = parts[1] });
                }
            }
            catch (Exception ex) { Log("ACCT: error: " + ex); }
            return list;
        }

        private void SaveLoginConfig()
        {
            try
            {
                var ini = new IniFile(ConfigIniPath);
                ini.Write("LOGIN", "gameexe", txtGameExe.Text);
                ini.Write("LOGIN", "username", txtUsername.Text);
                ini.Write("LOGIN", "password", txtPassword.Text);
                ini.Write("LOGIN", "clientamount", txtClientAmount.Text);
                ini.Write("LOGIN", "gameargs", txtGameArgs.Text);
                Log("INI: Saved login config");
            }
            catch (Exception ex) { Log("INI: save error: " + ex); }
        }

        private void LoadLoginConfig()
        {
            try
            {
                var ini = new IniFile(ConfigIniPath);
                string gameexe = ini.Read("LOGIN", "gameexe");
                txtGameExe.Text = string.IsNullOrWhiteSpace(gameexe) ? "GlobalAgenda.exe" : gameexe;

                string user = ini.Read("LOGIN", "username");
                txtUsername.Text = string.IsNullOrWhiteSpace(user) ? "" : user;

                string pass = ini.Read("LOGIN", "password");
                txtPassword.Text = string.IsNullOrWhiteSpace(pass) ? "a" : pass;

                string amount = ini.Read("LOGIN", "clientamount");
                txtClientAmount.Text = string.IsNullOrWhiteSpace(amount) ? "7" : amount;

                string args = ini.Read("LOGIN", "gameargs");
                txtGameArgs.Text = string.IsNullOrWhiteSpace(args) ? DEFAULTGAMEARGS : args;

                Log("INI: Loaded login config");
            }
            catch (Exception ex) { Log("INI: load error: " + ex); }
        }

        private void LoadSettingsToUi()
        {
            var ini = new IniFile(ConfigIniPath);
            if (!File.Exists(ConfigIniPath))
            {
                ini.Write("SETTINGS", "windowed", "1");
                ini.Write("SETTINGS", "ResX", "186");
                ini.Write("SETTINGS", "ResY", "91");
                ini.Write("SETTINGS", "log", "0");
                ini.Write("SETTINGS", "botmode", "0");
            }

            chkWindowed.Checked = (ini.Read("SETTINGS", "windowed") ?? "1") == "1";
            numResX.Value = ManualClamp(ParseIntOrDefault(ini.Read("SETTINGS", "ResX"), 186), (int)numResX.Minimum, (int)numResX.Maximum);
            numResY.Value = ManualClamp(ParseIntOrDefault(ini.Read("SETTINGS", "ResY"), 91), (int)numResY.Minimum, (int)numResY.Maximum);
            chkLog.Checked = (ini.Read("SETTINGS", "log") ?? "0") == "1";
            chkBotMode.Checked = (ini.Read("SETTINGS", "botmode") ?? "0") == "1";

            Log("INI: Loaded settings");
        }

        private void SaveSettingsFromUi()
        {
            try
            {
                var ini = new IniFile(ConfigIniPath);
                ini.Write("SETTINGS", "windowed", chkWindowed.Checked ? "1" : "0");
                ini.Write("SETTINGS", "ResX", ((int)numResX.Value).ToString());
                ini.Write("SETTINGS", "ResY", ((int)numResY.Value).ToString());
                ini.Write("SETTINGS", "log", chkLog.Checked ? "1" : "0");
                ini.Write("SETTINGS", "botmode", chkBotMode.Checked ? "1" : "0");
                Log("INI: Saved settings");
            }
            catch (Exception ex) { Log("INI: save settings error: " + ex); }
        }

        private int ManualClamp(int value, int min, int max) => value < min ? min : (value > max ? max : value);

        private bool IsElevated() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

        private void RestartAsAdminWithAutoStart()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = Application.ExecutablePath,
                    Arguments = "--autostart",
                    UseShellExecute = true,
                    Verb = "runas"
                });
            }
            catch (Exception ex) { Log("ADMIN: restart error: " + ex); }
        }

        private bool HasLauncherFlag(string flag)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 1; i < args.Length; i++)
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private void ResolveGamePathOrSearch()
        {
            if (Path.IsPathRooted(targetGameExe) && File.Exists(targetGameExe))
            {
                Directory.SetCurrentDirectory(Path.GetDirectoryName(targetGameExe));
                targetGameExe = Path.GetFileName(targetGameExe);
                return;
            }

            if (File.Exists(targetGameExe)) return;

            string[] bases = { @"SteamLibrary", @"Program Files (x86)\Steam", @"Program Files\Steam" };
            for (char d = 'C'; d <= 'Z'; d++)
            {
                string drive = d + @":\";
                if (!Directory.Exists(drive)) continue;
                foreach (string b in bases)
                {
                    string p = Path.Combine(drive, b, "steamapps", "common", "Global Agenda Live", "Binaries", "GlobalAgenda.exe");
                    if (File.Exists(p))
                    {
                        Directory.SetCurrentDirectory(Path.GetDirectoryName(p));
                        targetGameExe = "GlobalAgenda.exe";
                        SafeUi(delegate { txtGameExe.Text = p; });
                        return;
                    }
                }
            }
            throw new FileNotFoundException("GlobalAgenda.exe not found");
        }

        private List<IntPtr> GetHwndsForPid(int pid)
        {
            var list = new List<IntPtr>();
            EnumWindows((h, l) =>
            {
                if (IsWindowVisible(h) && IsWindowEnabled(h))
                {
                    GetWindowThreadProcessId(h, out uint p);
                    if (p == pid) list.Add(h);
                }
                return true;
            }, IntPtr.Zero);
            return list;
        }

        private int GetProcessIdFromHwnd(IntPtr hwnd)
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            return (int)pid;
        }

        private static string GetWindowTextSafe(IntPtr h)
        {
            var sb = new StringBuilder(512);
            GetWindowText(h, sb, sb.Capacity);
            return sb.ToString();
        }

        private void SafeUi(MethodInvoker a)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                if (InvokeRequired) BeginInvoke(a);
                else a();
            }
            catch { }
        }

        private T SafeReadUi<T>(Func<T> f)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return f();
                if (InvokeRequired)
                {
                    T v = default;
                    Invoke((MethodInvoker)delegate { v = f(); });
                    return v;
                }
                return f();
            }
            catch { return default(T); }
        }

        private static bool IsNullOrWhiteSpace(string s) => string.IsNullOrEmpty(s?.Trim());

        private static int ParseIntOrDefault(string s, int def) => int.TryParse(s?.Trim(), out int n) ? n : def;

        private static long UnixNow() => (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;

        private static bool IsProcessRunning(string name) => Process.GetProcessesByName(name).Length > 0;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hWnd);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint Msg, int wParam, int lParam);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint uCode, uint uMapType);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left; public int top; public int right; public int bottom; }
    }

    public sealed class IniFile
    {
        public string Path;

        public IniFile(string iniPath) => Path = new FileInfo(iniPath).FullName;

        public string Read(string section, string key)
        {
            var sb = new StringBuilder(2048);
            GetPrivateProfileString(section, key, "", sb, sb.Capacity, Path);
            return sb.ToString();
        }

        public void Write(string section, string key, string value)
        {
            WritePrivateProfileString(section, key, value ?? "", Path);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern long WritePrivateProfileString(string section, string key, string val, string filePath);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetPrivateProfileString(string section, string key, string def, StringBuilder retVal, int size, string filePath);
    }

    internal static class SteamApi
    {
        private static IntPtr _dll = IntPtr.Zero;
        private static bool _initAttempted = false;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate bool SteamAPI_Init_Delegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SteamAPI_Shutdown_Delegate();

        private static SteamAPI_Shutdown_Delegate _shutdown;

        public static bool TryInitFromPath(string fullDllPath)
        {
            if (_initAttempted) return _dll != IntPtr.Zero;
            _initAttempted = true;

            if (string.IsNullOrEmpty(fullDllPath) || !File.Exists(fullDllPath)) return false;

            _dll = LoadLibrary(fullDllPath);
            if (_dll == IntPtr.Zero) return false;

            var pInit = GetProcAddress(_dll, "SteamAPI_Init");
            if (pInit == IntPtr.Zero) return false;

            var pShutdown = GetProcAddress(_dll, "SteamAPI_Shutdown");
            if (pShutdown != IntPtr.Zero) _shutdown = (SteamAPI_Shutdown_Delegate)Marshal.GetDelegateForFunctionPointer(pShutdown, typeof(SteamAPI_Shutdown_Delegate));

            var init = (SteamAPI_Init_Delegate)Marshal.GetDelegateForFunctionPointer(pInit, typeof(SteamAPI_Init_Delegate));
            try { return init(); }
            catch { return false; }
        }

        public static void Shutdown()
        {
            try { _shutdown?.Invoke(); } catch { }
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);
    }

    internal static class NativeHandles
    {
        private static readonly object _fileLogLock = new object();
        private static readonly Dictionary<int, StreamWriter> _pidWriters = new Dictionary<int, StreamWriter>();

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern uint GetFinalPathNameByHandle(IntPtr hFile, [Out] StringBuilder lpszFilePath, uint cchFilePath, uint dwFlags);

        private static string NormalizePathForMatch(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            if (s.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) s = @"\\" + s.Substring(8);
            if (s.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)) s = s.Substring(4);
            return s.Replace('/', '\\').ToLowerInvariant();
        }

        private static string TryGetFilePathFromHandle(IntPtr h)
        {
            try
            {
                var sb = new StringBuilder(2048);
                uint n = GetFinalPathNameByHandle(h, sb, (uint)sb.Capacity, 0);
                if (n == 0 || n >= sb.Capacity)
                {
                    sb = new StringBuilder((int)Math.Max(n + 2, 4096));
                    n = GetFinalPathNameByHandle(h, sb, (uint)sb.Capacity, 0);
                    if (n == 0) return null;
                }
                return sb.ToString();
            }
            catch { return null; }
        }

        private static StreamWriter GetOrCreatePidWriter(int pid)
        {
            lock (_fileLogLock)
            {
                if (_pidWriters.TryGetValue(pid, out var writer)) return writer;

                string filePath = Path.Combine(Application.StartupPath, $"pid{pid}_handles.txt");
                try
                {
                    var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
                    writer = new StreamWriter(fs, Encoding.UTF8) { AutoFlush = true };
                    writer.WriteLine($"# File handle dump for PID {pid} - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    writer.WriteLine("# Format: handle_value | path_type | path");
                    writer.WriteLine("#".PadRight(80, '-'));
                    _pidWriters[pid] = writer;
                    VLog.Write("HANDLE", $"Created handle log file: {filePath}");
                }
                catch (Exception ex)
                {
                    VLog.Write("HANDLE", $"Failed to create pid{pid}_handles.txt: {ex.Message}");
                    writer = null;
                }
                return writer;
            }
        }

        private static void LogHandleToFile(int pid, IntPtr handleValue, string pathType, string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            var writer = GetOrCreatePidWriter(pid);
            if (writer == null) return;
            try { writer.WriteLine($"0x{handleValue.ToInt64():X16} | {pathType.PadRight(12)} | {path}"); }
            catch { }
        }

        public static void CleanupHandleLogs()
        {
            lock (_fileLogLock)
            {
                foreach (var writer in _pidWriters.Values)
                {
                    try { writer.Close(); }
                    catch { }
                }
                _pidWriters.Clear();
            }
        }

        public static bool TryGetOpenFileMatch(int pid, string includeSubstringLower, string excludeSubstringLower, out string matchedName)
        {
            matchedName = null;
            if (pid <= 0) return false;

            string include = (includeSubstringLower ?? "").ToLowerInvariant().Replace('/', '\\');
            string exclude = (excludeSubstringLower ?? "").ToLowerInvariant();

            IntPtr hProcess = IntPtr.Zero;
            IntPtr buffer = IntPtr.Zero;
            IntPtr nameInfo = IntPtr.Zero;
            IntPtr typeInfo = IntPtr.Zero;

            try
            {
                hProcess = OpenProcess(PROCESS_DUP_HANDLE | PROCESS_QUERY_INFORMATION, false, pid);
                if (hProcess == IntPtr.Zero)
                {
                    VLog.Write("HANDLE", $"OpenProcess failed for PID {pid} (error {Marshal.GetLastWin32Error()})");
                    return false;
                }

                buffer = QuerySystemHandles();
                if (buffer == IntPtr.Zero)
                {
                    VLog.Write("HANDLE", $"QuerySystemHandles failed");
                    return false;
                }

                // Correct: NumberOfHandles is ULONG_PTR at offset 0
                ulong handleCount = (ulong)Marshal.ReadIntPtr(buffer).ToInt64();

                // Handles array starts after header (2 x IntPtr: NumberOfHandles + Reserved)
                long offset = IntPtr.Size * 2;
                long entrySize = Marshal.SizeOf(typeof(SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX));

                int nameInfoSize = 0x10000;
                nameInfo = Marshal.AllocHGlobal(nameInfoSize);

                int typeInfoSize = 0x1000;
                typeInfo = Marshal.AllocHGlobal(typeInfoSize);

                int scanned = 0;
                int matched = 0;

                VLog.Write("HANDLE", $"Scanning up to {handleCount} handles for PID {pid}...");

                for (ulong i = 0; i < handleCount; i++)
                {
                    IntPtr entryPtr = new IntPtr(buffer.ToInt64() + offset + (long)i * entrySize);
                    var e = (SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX)Marshal.PtrToStructure(entryPtr, typeof(SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX));

                    // EARLY FILTER: Skip if not our PID
                    if ((int)e.UniqueProcessId != pid) continue;

                    IntPtr dup = IntPtr.Zero;
                    if (!DuplicateHandle(hProcess, e.HandleValue, GetCurrentProcess(), out dup, 0, false, DUPLICATE_SAME_ACCESS))
                        continue;

                    try
                    {
                        // Query type
                        int returnLen = 0;
                        int status = NtQueryObject(dup, ObjectTypeInformation, typeInfo, typeInfoSize, ref returnLen);
                        if (status == STATUS_INFO_LENGTH_MISMATCH)
                        {
                            Marshal.FreeHGlobal(typeInfo);
                            typeInfoSize = returnLen + 1024;
                            typeInfo = Marshal.AllocHGlobal(typeInfoSize);
                            status = NtQueryObject(dup, ObjectTypeInformation, typeInfo, typeInfoSize, ref returnLen);
                        }
                        if (status < 0) continue;

                        var typeUs = Marshal.PtrToStructure<PUBLIC_OBJECT_TYPE_INFORMATION>(typeInfo).TypeName;
                        string handleType = Marshal.PtrToStringUni(typeUs.Buffer, typeUs.Length / 2);

                        // STRICT FILTER: Only "File" type
                        if (!string.Equals(handleType, "File", StringComparison.OrdinalIgnoreCase)) continue;

                        // Try GetFinalPathNameByHandle first (faster, gives dos path)
                        var sb = new StringBuilder(2048);
                        uint pathLen = GetFinalPathNameByHandle(dup, sb, (uint)sb.Capacity, 0);
                        string path = null;
                        if (pathLen > 0 && pathLen < sb.Capacity)
                        {
                            path = sb.ToString();
                        }
                        else
                        {
                            // Fallback to NtQueryObject ObjectNameInformation (NT path)
                            path = QueryObjectName(dup, ref nameInfo, ref nameInfoSize);
                        }

                        if (!string.IsNullOrEmpty(path))
                        {
                            scanned++;
                            LogHandleToFile(pid, e.HandleValue, "FILE", path);

                            string norm = NormalizePathForMatch(path);
                            if (!string.IsNullOrEmpty(norm) &&
                                norm.Contains(include) &&
                                (string.IsNullOrEmpty(exclude) || !norm.Contains(exclude)))
                            {
                                matchedName = path;
                                matched++;
                                VLog.Write("HANDLE", $"MATCHED (FILE) PID={pid}: {path}");
                                return true;
                            }
                        }
                    }
                    finally
                    {
                        CloseHandle(dup);
                    }
                }

                VLog.Write("HANDLE", $"PID {pid}: Scanned {scanned} file handles, {matched} matches found.");
                return false;
            }
            catch (Exception ex)
            {
                VLog.Write("HANDLE", $"Exception in TryGetOpenFileMatch PID {pid}: {ex}");
                return false;
            }
            finally
            {
                if (nameInfo != IntPtr.Zero) Marshal.FreeHGlobal(nameInfo);
                if (typeInfo != IntPtr.Zero) Marshal.FreeHGlobal(typeInfo);
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                if (hProcess != IntPtr.Zero) CloseHandle(hProcess);
            }
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PUBLIC_OBJECT_TYPE_INFORMATION
        {
            public UNICODE_STRING TypeName;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 22)]
            public int[] Reserved;
        }

        public static void TryEnableSeDebugPrivilege()
        {
            try
            {
                if (_debugPrivilegeAttempted) return;
                _debugPrivilegeAttempted = true;

                if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr hToken)) return;

                try
                {
                    if (!LookupPrivilegeValue(null, SE_DEBUG_NAME, out LUID luid)) return;

                    var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
                    AdjustTokenPrivileges(hToken, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
                }
                finally { CloseHandle(hToken); }
            }
            catch { }
        }

        private static bool _debugPrivilegeAttempted = false;

        private static IntPtr QuerySystemHandles()
        {
            int len = 0x10000;
            IntPtr ptr = IntPtr.Zero;
            while (true)
            {
                ptr = Marshal.AllocHGlobal(len);
                int retLen = 0;
                int status = NtQuerySystemInformation(SystemExtendedHandleInformation, ptr, len, ref retLen);
                if (status == STATUS_INFO_LENGTH_MISMATCH)
                {
                    Marshal.FreeHGlobal(ptr);
                    len = Math.Max(len * 2, retLen);
                    continue;
                }
                if (status < 0)
                {
                    Marshal.FreeHGlobal(ptr);
                    return IntPtr.Zero;
                }
                return ptr;
            }
        }

        private static string QueryObjectName(IntPtr handle, ref IntPtr buffer, ref int bufferSize)
        {
            int returnLen = 0;
            int status = NtQueryObject(handle, ObjectNameInformation, buffer, bufferSize, ref returnLen);
            if (status == STATUS_INFO_LENGTH_MISMATCH)
            {
                Marshal.FreeHGlobal(buffer);
                bufferSize = Math.Max(bufferSize * 2, returnLen + 1024);
                buffer = Marshal.AllocHGlobal(bufferSize);
                returnLen = 0;
                status = NtQueryObject(handle, ObjectNameInformation, buffer, bufferSize, ref returnLen);
            }
            if (status < 0) return null;

            var us = (UNICODE_STRING)Marshal.PtrToStructure(buffer, typeof(UNICODE_STRING));
            if (us.Buffer == IntPtr.Zero || us.Length == 0) return null;
            return Marshal.PtrToStringUni(us.Buffer, us.Length / 2);
        }

        private static long ReadIntPtrAsInt64(IntPtr p, int offset) => Marshal.ReadIntPtr(p, offset).ToInt64();

        private const int SystemExtendedHandleInformation = 0x40;
        private const int ObjectNameInformation = 1;
        private const int ObjectTypeInformation = 2;
        private const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);

        private const int PROCESS_DUP_HANDLE = 0x0040;
        private const int PROCESS_QUERY_INFORMATION = 0x0400;
        private const uint DUPLICATE_SAME_ACCESS = 0x00000002;

        private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        private const uint TOKEN_QUERY = 0x0008;
        private const string SE_DEBUG_NAME = "SeDebugPrivilege";
        private const uint SE_PRIVILEGE_ENABLED = 0x00000002;

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX
        {
            public IntPtr Object;
            public IntPtr UniqueProcessId;   // ULONG_PTR
            public IntPtr HandleValue;       // ULONG_PTR
            public uint GrantedAccess;
            public ushort CreatorBackTraceIndex;
            public ushort ObjectTypeIndex;
            public uint HandleAttributes;
            public uint Reserved;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct UNICODE_STRING
        {
            public ushort Length;
            public ushort MaximumLength;
            public IntPtr Buffer;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID { public uint LowPart; public int HighPart; }

        [StructLayout(LayoutKind.Sequential)]
        private struct TOKEN_PRIVILEGES
        {
            public uint PrivilegeCount;
            public LUID Luid;
            public uint Attributes;
        }

        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(int systemInformationClass, IntPtr systemInformation, int systemInformationLength, ref int returnLength);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryObject(IntPtr handle, int objectInformationClass, IntPtr objectInformation, int objectInformationLength, ref int returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int desiredAccess, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DuplicateHandle(IntPtr hSourceProcessHandle, IntPtr hSourceHandle, IntPtr hTargetProcessHandle, out IntPtr lpTargetHandle, uint dwDesiredAccess, bool bInheritHandle, uint dwOptions);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool LookupPrivilegeValue(string lpSystemName, string lpName, out LUID luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAllPrivileges, ref TOKEN_PRIVILEGES newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);
    }

    internal static class DiscordRpc
    {
        private static Native.ReadyCallback _ready;
        private static Native.DisconnectedCallback _disc;
        private static Native.ErroredCallback _err;

        private static Action<string> _onReady = delegate { };
        private static Action<int, string> _onDisconnected = delegate { };
        private static Action<int, string> _onError = delegate { };

        private static bool _initialized;

        private static PresenceAlloc _presenceAlloc = new PresenceAlloc();

        public static void Initialize(string appId, Action<string> onReady, Action<int, string> onDisconnected, Action<int, string> onError)
        {
            _onReady = onReady ?? delegate { };
            _onDisconnected = onDisconnected ?? delegate { };
            _onError = onError ?? delegate { };

            _ready = delegate (IntPtr userPtr)
            {
                try
                {
                    var u = (Native.DiscordUserPtr)Marshal.PtrToStructure(userPtr, typeof(Native.DiscordUserPtr));
                    _onReady(PtrToAnsi(u.username));
                }
                catch { _onReady("(unknown)"); }
            };

            _disc = delegate (int code, IntPtr msg) { _onDisconnected(code, PtrToAnsi(msg)); };
            _err = delegate (int code, IntPtr msg) { _onError(code, PtrToAnsi(msg)); };

            var handlers = new Native.DiscordEventHandlers
            {
                ready = _ready,
                disconnected = _disc,
                errored = _err
            };

            Native.Discord_Initialize(appId, ref handlers, 1, null);
            _initialized = true;
        }

        public static void SetPresence(string state, string details, long startTimestamp, string largeImageKey, string largeImageText)
        {
            if (!_initialized) return;

            _presenceAlloc.Replace(state, details, largeImageKey, largeImageText);

            var p = new Native.DiscordRichPresence
            {
                state = _presenceAlloc.State,
                details = _presenceAlloc.Details,
                startTimestamp = startTimestamp,
                largeImageKey = _presenceAlloc.LargeImageKey,
                largeImageText = _presenceAlloc.LargeImageText
            };

            Native.Discord_UpdatePresence(ref p);
        }

        public static void SafeClear()
        {
            if (_initialized) Native.Discord_ClearPresence();
        }

        public static void RunCallbacks()
        {
            if (_initialized) Native.Discord_RunCallbacks();
        }

        public static void Shutdown()
        {
            if (!_initialized) return;
            try { Native.Discord_ClearPresence(); } catch { }
            try { Native.Discord_Shutdown(); } catch { }
            _initialized = false;
            _presenceAlloc.FreeAll();
        }

        private static string PtrToAnsi(IntPtr p) => p == IntPtr.Zero ? "" : Marshal.PtrToStringAnsi(p);

        private sealed class PresenceAlloc
        {
            public IntPtr State = IntPtr.Zero;
            public IntPtr Details = IntPtr.Zero;
            public IntPtr LargeImageKey = IntPtr.Zero;
            public IntPtr LargeImageText = IntPtr.Zero;

            public void Replace(string state, string details, string largeKey, string largeText)
            {
                FreeAll();
                State = AllocAnsi(state);
                Details = AllocAnsi(details);
                LargeImageKey = AllocAnsi(largeKey);
                LargeImageText = AllocAnsi(largeText);
            }

            public void FreeAll()
            {
                Free(ref State);
                Free(ref Details);
                Free(ref LargeImageKey);
                Free(ref LargeImageText);
            }

            private static IntPtr AllocAnsi(string s) => string.IsNullOrEmpty(s) ? IntPtr.Zero : Marshal.StringToHGlobalAnsi(s);

            private static void Free(ref IntPtr p)
            {
                try { if (p != IntPtr.Zero) Marshal.FreeHGlobal(p); }
                catch { }
                p = IntPtr.Zero;
            }
        }
    }

    internal static class Native
    {
        private const string DLL = "discord-rpc.dll";

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void ReadyCallback(IntPtr userPtr);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void DisconnectedCallback(int errorCode, IntPtr message);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void ErroredCallback(int errorCode, IntPtr message);

        [StructLayout(LayoutKind.Sequential)]
        public struct DiscordEventHandlers
        {
            public ReadyCallback ready;
            public DisconnectedCallback disconnected;
            public ErroredCallback errored;
            public IntPtr joinGame;
            public IntPtr spectateGame;
            public IntPtr joinRequest;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DiscordUserPtr
        {
            public IntPtr userId;
            public IntPtr username;
            public IntPtr discriminator;
            public IntPtr avatar;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DiscordRichPresence
        {
            public IntPtr state;
            public IntPtr details;
            public long startTimestamp;
            public long endTimestamp;
            public IntPtr largeImageKey;
            public IntPtr largeImageText;
            public IntPtr smallImageKey;
            public IntPtr smallImageText;
            public IntPtr partyId;
            public int partySize;
            public int partyMax;
            public int partyPrivacy;
            public IntPtr matchSecret;
            public IntPtr joinSecret;
            public IntPtr spectateSecret;
            public sbyte instance;
        }

        [DllImport(DLL, EntryPoint = "Discord_Initialize", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern void Discord_Initialize(string applicationId, ref DiscordEventHandlers handlers, int autoRegister, string optionalSteamId);

        [DllImport(DLL, EntryPoint = "Discord_Shutdown", CallingConvention = CallingConvention.Cdecl)]
        public static extern void Discord_Shutdown();

        [DllImport(DLL, EntryPoint = "Discord_RunCallbacks", CallingConvention = CallingConvention.Cdecl)]
        public static extern void Discord_RunCallbacks();

        [DllImport(DLL, EntryPoint = "Discord_UpdatePresence", CallingConvention = CallingConvention.Cdecl)]
        public static extern void Discord_UpdatePresence(ref DiscordRichPresence presence);

        [DllImport(DLL, EntryPoint = "Discord_ClearPresence", CallingConvention = CallingConvention.Cdecl)]
        public static extern void Discord_ClearPresence();
    }
}