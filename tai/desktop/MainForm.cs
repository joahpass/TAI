using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace TAI.Desktop
{
    /// <summary>
    /// Standalone WinForms window hosting the local TAI web app in WebView2.
    /// Assistant mode shows /workspace; embedded mode keeps the compact /assistant page.
    /// Console mode shows / (configuration and diagnostics).
    /// </summary>
    internal sealed class MainForm : Form
    {
        private const string WebView2Download = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

        // Logical (96 DPI) sizes; scaled to the target monitor in PlaceInitialWindow.
        private static readonly Size AssistantSize = new Size(520, 860);
        private static readonly Size AssistantMinimum = new Size(380, 480);
        private static readonly Size ConsoleSize = new Size(1200, 820);
        private static readonly Size ConsoleMinimum = new Size(720, 520);
        private const int AssistantEdgeMargin = 8;

        private readonly string WindowTitle;
        private readonly HostOptions options;
        private readonly ServerLauncher launcher;
        private readonly CancellationTokenSource closing = new CancellationTokenSource();

        private readonly WebView2 web = new WebView2();
        private readonly Panel statusPanel = new Panel();
        private readonly Label statusTitle = new Label();
        private readonly Label statusDetail = new Label();
        private readonly FlowLayoutPanel buttons = new FlowLayoutPanel();
        private readonly Button retryButton = new Button();
        private readonly Button downloadButton = new Button();
        private readonly Button logButton = new Button();
        private readonly Button closeButton = new Button();

        private bool webInitialized;
        private bool starting;

        // Embedded (WS_CHILD inside TIA Portal) mode only.
        private readonly bool embedded;
        private Panel embeddedBar;
        private Panel embeddedGrip;
        private int gripLastScreenX;
        private bool gripDragging;

        /// <summary>Embedded mode: the panel asks its host to hide it (the original TIA icon shows it again).</summary>
        public event EventHandler HideRequested;
        /// <summary>Embedded mode: left-edge drag, in screen pixels (negative = dragged left = wider).</summary>
        public event EventHandler<int> WidthDragged;

        public MainForm(HostOptions options) : this(options, false)
        {
        }

        /// <summary>
        /// embedded=true: non-top-level, borderless form that EmbeddedHost parents (SetParent, WS_CHILD) into the
        /// TIA Portal main window. It never appears in the taskbar and is never shown as a top-level window.
        /// </summary>
        public MainForm(HostOptions options, bool embedded)
        {
            this.options = options;
            this.embedded = embedded;
            launcher = new ServerLauncher(options.Root);
            WindowTitle = HostModes.Title(options.Mode);

            Text = WindowTitle;
            // Bounds are computed in PlaceInitialWindow (physical pixels, per target monitor).
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.Dpi;
            MinimumSize = IsCompactAssistant ? AssistantMinimum : ConsoleMinimum;
            Size = IsCompactAssistant ? AssistantSize : ConsoleSize;
            BackColor = Color.FromArgb(246, 247, 249);
            Font = new Font("Microsoft YaHei UI", 9F);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            BuildStatusPanel();

            web.Dock = DockStyle.Fill;
            web.DefaultBackgroundColor = BackColor;
            web.Visible = false;
            web.AccessibleName = WindowTitle + "内容";

            Controls.Add(web);
            Controls.Add(statusPanel);

            if (embedded) ConfigureEmbedded();
        }

        private void ConfigureEmbedded()
        {
            TopLevel = false;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            ControlBox = false;
            MinimizeBox = false;
            MaximizeBox = false;
            MinimumSize = Size.Empty;

            // Thin native strip: title + hide. The web UI below stays the single /assistant page.
            embeddedBar = new Panel { Dock = DockStyle.Top, Height = 28, BackColor = Color.FromArgb(236, 239, 243) };
            var title = new Label
            {
                Text = WindowTitle,
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 0, 0),
                ForeColor = Color.FromArgb(28, 33, 40),
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold)
            };
            var hide = new Button
            {
                Text = "隐藏",
                Dock = DockStyle.Right,
                Width = 56,
                FlatStyle = FlatStyle.Flat,
                TabStop = false,
                AccessibleName = "隐藏 TAI 工程助手面板"
            };
            hide.FlatAppearance.BorderSize = 0;
            hide.Click += (s, e) => RaiseHide();
            embeddedBar.Controls.Add(title);
            embeddedBar.Controls.Add(hide);
            embeddedBar.Paint += (s, e) =>
            {
                using (var pen = new Pen(Color.FromArgb(205, 210, 217)))
                    e.Graphics.DrawLine(pen, 0, embeddedBar.Height - 1, embeddedBar.Width, embeddedBar.Height - 1);
            };

            // Left-edge resize grip (only our window changes size; TIA children are never resized).
            embeddedGrip = new Panel { Dock = DockStyle.Left, Width = 5, Cursor = Cursors.SizeWE, BackColor = Color.FromArgb(205, 210, 217) };
            embeddedGrip.MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                gripDragging = true;
                gripLastScreenX = Cursor.Position.X;
                embeddedGrip.Capture = true;
            };
            embeddedGrip.MouseMove += (s, e) =>
            {
                if (!gripDragging) return;
                int x = Cursor.Position.X;
                int delta = x - gripLastScreenX;
                if (delta == 0) return;
                gripLastScreenX = x;
                var h = WidthDragged;
                if (h != null) h(this, delta);
            };
            embeddedGrip.MouseUp += (s, e) => { gripDragging = false; embeddedGrip.Capture = false; };

            // Added after the Fill controls so they dock first (outermost).
            Controls.Add(embeddedBar);
            Controls.Add(embeddedGrip);
        }

        private void RaiseHide()
        {
            var h = HideRequested;
            if (h != null) h(this, EventArgs.Empty);
        }

        /// <summary>Embedded mode: begins server check + WebView2 load (OnShown is not used for child forms).</summary>
        public void StartEmbedded()
        {
            StartAsync();
        }

        /// <summary>Embedded mode: moves keyboard focus into the page after the panel is shown.</summary>
        public void FocusContent()
        {
            try
            {
                if (web.Visible) web.Focus();
            }
            catch (Exception ex)
            {
                HostLog.Write("Focus content failed: " + ex.Message);
            }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                if (embedded)
                {
                    // Child style from the very first CreateWindowEx: never a popup/top-level/taskbar window.
                    cp.Style = (cp.Style | 0x40000000 /*WS_CHILD*/ | 0x04000000 /*WS_CLIPSIBLINGS*/ | 0x02000000 /*WS_CLIPCHILDREN*/) &
                               ~(unchecked((int)0x80000000) /*WS_POPUP*/ | 0x00C00000 /*WS_CAPTION*/ | 0x00040000 /*WS_THICKFRAME*/ | 0x00080000 /*WS_SYSMENU*/);
                    cp.ExStyle &= ~0x00040000; // WS_EX_APPWINDOW
                    cp.ExStyle |= 0x00010000;  // WS_EX_CONTROLPARENT
                }
                return cp;
            }
        }

        private bool IsAssistant { get { return options.Mode == HostMode.Assistant; } }
        private bool IsCompactAssistant { get { return embedded && IsAssistant; } }

        /// <summary>
        /// Sets the initial bounds in physical pixels (the process is PerMonitorV2 aware).
        /// Standalone workspace and console: centered on the owner's monitor, or the monitor under the cursor.
        /// Embedded panels retain their compact sizes and are laid out by EmbeddedSession.
        /// Must be called before the form is shown.
        /// </summary>
        public void PlaceInitialWindow(IntPtr owner)
        {
            try
            {
                Screen screen = owner != IntPtr.Zero ? Screen.FromHandle(owner) : Screen.FromPoint(Cursor.Position);
                Rectangle wa = screen.WorkingArea;
                float scale = DisplayScale.ForPoint(new Point(
                    screen.Bounds.Left + screen.Bounds.Width / 2, screen.Bounds.Top + screen.Bounds.Height / 2));

                Size min = ScaleLogical(IsCompactAssistant ? AssistantMinimum : ConsoleMinimum, scale);
                MinimumSize = new Size(Math.Min(min.Width, wa.Width), Math.Min(min.Height, wa.Height));

                Rectangle bounds = IsCompactAssistant ? AssistantBounds(owner, wa, scale) : ConsoleBounds(wa, scale);
                Bounds = bounds;
                HostLog.Write(options.Mode + " bounds " + bounds + " in work area " + wa + " scale " + scale);
            }
            catch (Exception ex)
            {
                HostLog.Write("PlaceInitialWindow failed: " + ex.Message);
                StartPosition = FormStartPosition.CenterScreen;
            }
        }

        private static Rectangle AssistantBounds(IntPtr owner, Rectangle wa, float scale)
        {
            int margin = (int)Math.Round(AssistantEdgeMargin * scale);
            Size size = ScaleLogical(AssistantSize, scale);

            // Reference area: the visible part of the TIA window, if it is at least as wide as the assistant.
            Rectangle reference = wa;
            NativeMethods.RECT r;
            if (owner != IntPtr.Zero && !NativeMethods.IsIconic(owner) && NativeMethods.GetWindowRect(owner, out r))
            {
                Rectangle ownerRect = Rectangle.Intersect(wa, Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom));
                if (ownerRect.Width >= size.Width + 2 * margin && ownerRect.Height >= size.Height / 2) reference = ownerRect;
            }

            int width = Math.Min(size.Width, wa.Width - 2 * margin);
            int height = Math.Min(size.Height, reference.Height - 2 * margin);
            if (height < size.Height / 2) height = Math.Min(size.Height, wa.Height - 2 * margin);
            int x = reference.Right - width - margin;
            int y = reference.Top + Math.Max(margin, (reference.Height - height) / 2);
            return ClampInto(new Rectangle(x, y, width, height), wa);
        }

        private static Rectangle ConsoleBounds(Rectangle wa, float scale)
        {
            Size size = ScaleLogical(ConsoleSize, scale);
            int width = Math.Min(size.Width, (int)(wa.Width * 0.94));
            int height = Math.Min(size.Height, (int)(wa.Height * 0.94));
            return new Rectangle(wa.Left + (wa.Width - width) / 2, wa.Top + (wa.Height - height) / 2, width, height);
        }

        private static Rectangle ClampInto(Rectangle r, Rectangle wa)
        {
            int w = Math.Min(r.Width, wa.Width), h = Math.Min(r.Height, wa.Height);
            int x = Math.Max(wa.Left, Math.Min(r.Left, wa.Right - w));
            int y = Math.Max(wa.Top, Math.Min(r.Top, wa.Bottom - h));
            return new Rectangle(x, y, w, h);
        }

        private static Size ScaleLogical(Size logical, float scale)
        {
            return new Size((int)Math.Round(logical.Width * scale), (int)Math.Round(logical.Height * scale));
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Lets a second launch of the same mode find exactly this window. The embedded child is never
            // marked, so "--assistant" activation can never target a window inside TIA.
            if (!embedded) WindowMarker.Mark(Handle, options.Mode);
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (!embedded) WindowMarker.Unmark(Handle);
            base.OnHandleDestroyed(e);
        }

        private void BuildStatusPanel()
        {
            statusPanel.Dock = DockStyle.Fill;
            statusPanel.Padding = new Padding(IsAssistant ? 20 : 48);
            statusPanel.BackColor = BackColor;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                BackColor = BackColor
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 30F));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 70F));

            statusTitle.AutoSize = true;
            statusTitle.Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Bold);
            statusTitle.ForeColor = Color.FromArgb(28, 33, 40);
            statusTitle.Margin = new Padding(0, 0, 0, 12);
            statusTitle.Text = WindowTitle;

            statusDetail.AutoSize = true;
            statusDetail.MaximumSize = new Size(IsAssistant ? 440 : 760, 0);
            statusDetail.Font = new Font("Microsoft YaHei UI", 10F);
            statusDetail.ForeColor = Color.FromArgb(80, 88, 99);
            statusDetail.Margin = new Padding(0, 0, 0, 20);
            statusDetail.Text = "正在准备…";

            buttons.AutoSize = true;
            buttons.FlowDirection = FlowDirection.LeftToRight;
            buttons.WrapContents = true;
            buttons.Margin = new Padding(0);

            ConfigureButton(retryButton, "重试(&R)", (s, e) => StartAsync());
            ConfigureButton(downloadButton, "下载 WebView2 运行时(&D)", (s, e) => OpenExternal(WebView2Download));
            ConfigureButton(logButton, "打开日志目录(&L)", (s, e) => OpenLogFolder());
            ConfigureButton(closeButton, "关闭(&C)", (s, e) => { if (embedded) RaiseHide(); else Close(); });

            buttons.Controls.AddRange(new Control[] { retryButton, downloadButton, logButton, closeButton });
            SetButtons(false, false);

            layout.Controls.Add(new Panel { Height = 1, Dock = DockStyle.Fill, BackColor = BackColor }, 0, 0);
            layout.Controls.Add(statusTitle, 0, 1);
            var detailBlock = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Margin = new Padding(0)
            };
            detailBlock.Controls.Add(statusDetail);
            detailBlock.Controls.Add(buttons);
            layout.Controls.Add(detailBlock, 0, 2);
            statusPanel.Controls.Add(layout);
        }

        private static void ConfigureButton(Button b, string text, EventHandler click)
        {
            b.Text = text;
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.Padding = new Padding(10, 4, 10, 4);
            b.Margin = new Padding(0, 0, 10, 0);
            b.UseVisualStyleBackColor = true;
            b.Click += click;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (!embedded) StartAsync();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Only this window goes away. The shared TAI server is intentionally left running.
            closing.Cancel();
            base.OnFormClosing(e);
        }

        private async void StartAsync()
        {
            if (starting || IsDisposed) return;
            starting = true;
            try
            {
                ShowStatus(WindowTitle, "正在准备…", false, false);

                string runtimeError = CheckWebView2Runtime();
                if (runtimeError != null)
                {
                    ShowStatus("缺少 Microsoft Edge WebView2 运行时", runtimeError, true, true);
                    return;
                }

                var token = closing.Token;
                LaunchResult result = await Task.Run(() => launcher.EnsureRunning(
                    message => Ui(() => { if (!webInitialized || !web.Visible) statusDetail.Text = message; }),
                    token));
                if (IsDisposed || token.IsCancellationRequested) return;
                if (!result.Ok)
                {
                    HostLog.Write("Launch failed: " + result.Title + " | " + result.Detail.Replace("\r\n", " "));
                    ShowStatus(result.Title, result.Detail, true, false);
                    return;
                }

                statusDetail.Text = "正在加载界面…";
                await InitializeWebViewAsync();
                if (IsDisposed) return;
                web.CoreWebView2.Navigate(HostModes.StartUrl(options.Mode, embedded));
            }
            catch (WebView2RuntimeNotFoundException ex)
            {
                HostLog.Write("WebView2 runtime not found: " + ex.Message);
                ShowStatus("缺少 Microsoft Edge WebView2 运行时", WebView2MissingText(), true, true);
            }
            catch (Exception ex)
            {
                HostLog.Write("Startup error: " + ex);
                ShowStatus(WindowTitle + "启动失败", ex.Message + "\r\n\r\n详细信息已写入日志：\r\n" +
                    Path.Combine(HostLog.Directory, "desktop.log"), true, false);
            }
            finally
            {
                starting = false;
            }
        }

        private static string CheckWebView2Runtime()
        {
            try
            {
                string version = CoreWebView2Environment.GetAvailableBrowserVersionString();
                if (string.IsNullOrEmpty(version)) return WebView2MissingText();
                HostLog.Write("WebView2 runtime " + version);
                return null;
            }
            catch (WebView2RuntimeNotFoundException)
            {
                return WebView2MissingText();
            }
            catch (Exception ex)
            {
                // Typically WebView2Loader.dll missing next to the exe.
                HostLog.Write("WebView2 check failed: " + ex);
                return "无法加载 WebView2 组件：" + ex.Message + "\r\n\r\n请确认安装目录中包含 WebView2Loader.dll 等组件，或重新安装 TAI 工程助手。";
            }
        }

        private static string WebView2MissingText()
        {
            return "本窗口需要 Microsoft Edge WebView2 运行时（Evergreen）才能显示界面。\r\n\r\n" +
                   "请点击“下载 WebView2 运行时”安装后点击“重试”。Windows 11 通常已自带该组件。";
        }

        private async Task InitializeWebViewAsync()
        {
            if (webInitialized) return;

            // The embedded helper (system-DPI-aware, hosted inside TIA) and the standalone/console windows
            // (per-monitor DPI) must not share one WebView2 browser process: a second process attaching to a
            // browser created under a different DPI awareness fails CreateCoreWebView2Controller with
            // HRESULT 0x8007139F. Separate user data folders give each its own browser process. Backend config
            // and conversations live on the local TAI server, so they stay shared.
            string userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TAI",
                embedded ? "WebView2Embedded" : "WebView2");
            Directory.CreateDirectory(userData);

            var envOptions = new CoreWebView2EnvironmentOptions();
            CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, userData, envOptions);
            await web.EnsureCoreWebView2Async(env);

            CoreWebView2 core = web.CoreWebView2;
            CoreWebView2Settings settings = core.Settings;
            settings.AreDevToolsEnabled = options.DevTools;
            settings.AreDefaultContextMenusEnabled = true;
            settings.IsStatusBarEnabled = false;
            settings.IsZoomControlEnabled = true;
            settings.AreHostObjectsAllowed = false;
            settings.IsWebMessageEnabled = false;
            settings.IsGeneralAutofillEnabled = false;
            settings.IsPasswordAutosaveEnabled = false;

            core.NavigationStarting += OnNavigationStarting;
            core.FrameNavigationStarting += OnFrameNavigationStarting;
            core.NewWindowRequested += OnNewWindowRequested;
            core.NavigationCompleted += OnNavigationCompleted;
            core.ProcessFailed += OnProcessFailed;
            core.DocumentTitleChanged += (s, e) => UpdateTitle();

            webInitialized = true;
        }

        /// <summary>Only the local TAI origin may be shown inside this window.</summary>
        internal static bool IsAllowedUri(string uri)
        {
            Uri u;
            if (!Uri.TryCreate(uri, UriKind.Absolute, out u)) return false;
            if (u.Scheme == "about" && uri == "about:blank") return true;
            if (u.Scheme != Uri.UriSchemeHttp) return false;
            if (u.Port != ServerLauncher.Port) return false;
            return u.Host == "127.0.0.1" || string.Equals(u.Host, "localhost", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsExternalWebUri(string uri)
        {
            Uri u;
            return Uri.TryCreate(uri, UriKind.Absolute, out u) &&
                   (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) &&
                   !IsAllowedUri(uri);
        }

        private void OnNavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (IsAllowedUri(e.Uri)) return;
            // Plain link clicks / redirects never leave the app and never auto-open a browser.
            e.Cancel = true;
            HostLog.Write("Blocked navigation: " + e.Uri);
        }

        private void OnFrameNavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (IsAllowedUri(e.Uri) || e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return;
            e.Cancel = true;
            HostLog.Write("Blocked frame navigation: " + e.Uri);
        }

        /// <summary>True for the local console root ("/" on the TAI origin, query/fragment ignored).</summary>
        internal static bool IsLocalConsoleRoot(string uri)
        {
            Uri u;
            return IsAllowedUri(uri) && Uri.TryCreate(uri, UriKind.Absolute, out u) &&
                   u.Scheme == Uri.UriSchemeHttp && u.AbsolutePath == "/";
        }

        /// <summary>Match a local TAI page without discarding its query or fragment when opening it.</summary>
        internal static bool IsLocalPage(string uri, string path)
        {
            Uri u;
            return IsAllowedUri(uri) && Uri.TryCreate(uri, UriKind.Absolute, out u) &&
                   u.Scheme == Uri.UriSchemeHttp && u.AbsolutePath == path;
        }

        /// <summary>
        /// Popup policy. No popup WebView windows are ever created; every request is handled here:
        /// 1. Embedded workspace links, user-initiated: system browser with the full design URL.
        /// 2. Local configuration links ("/" or "/settings"), user-initiated: assistant starts or activates
        ///    the separate console; console navigates in place.
        /// 3. Other local TAI URLs: console navigates in place; assistant blocks them.
        /// 4. External http(s), user-initiated: system browser. Everything else: blocked and logged.
        /// </summary>
        private void OnNewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            e.Handled = true;
            string uri = e.Uri;
            bool user = e.IsUserInitiated;

            if (embedded && IsLocalPage(uri, "/workspace"))
            {
                if (user) OpenExternal(uri);
                else HostLog.Write("Blocked non-user-initiated workspace popup: " + uri);
                return;
            }
            if (IsLocalConsoleRoot(uri) || IsLocalPage(uri, "/settings"))
            {
                if (!user)
                {
                    HostLog.Write("Blocked non-user-initiated console popup: " + uri);
                    return;
                }
                if (IsAssistant) OpenConsoleWindow();
                else web.CoreWebView2.Navigate(uri);
                return;
            }
            if (IsAllowedUri(uri) && !uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsAssistant)
                {
                    web.CoreWebView2.Navigate(uri);
                    return;
                }
                HostLog.Write("Blocked local popup in assistant mode: " + uri);
                return;
            }
            if (user && IsExternalWebUri(uri))
            {
                OpenExternal(uri);
                return;
            }
            HostLog.Write("Blocked new window: " + uri + " (userInitiated=" + user + ")");
        }

        /// <summary>
        /// Starts this same exe in default (console) mode. The console's own mutex makes a second
        /// start activate the existing console window. Never owned by TIA, never an external browser.
        /// </summary>
        private void OpenConsoleWindow()
        {
            try
            {
                // Already open: activate exactly the console window, not an arbitrary TAI window.
                IntPtr existing = WindowMarker.Find(HostMode.Console);
                if (existing != IntPtr.Zero)
                {
                    WindowMarker.Activate(existing);
                    HostLog.Write("Activated existing console window");
                    return;
                }

                string exe = Application.ExecutablePath;
                string args = "";
                if (options.RootOverridden) args += "--root " + QuoteArg(options.Root.TrimEnd('\\')) + " ";
                if (options.DevTools) args += "--devtools";
                var psi = new ProcessStartInfo(exe, args.Trim())
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(exe)
                };
                using (Process p = Process.Start(psi))
                {
                    // The new process (or the existing console it activates) may take the foreground.
                    if (p != null) NativeMethods.AllowSetForegroundWindow(p.Id);
                    HostLog.Write("Started console window process " + (p != null ? p.Id.ToString() : "?"));
                }
            }
            catch (Exception ex)
            {
                HostLog.Write("Start console failed: " + ex);
                MessageBox.Show(this, "无法打开“" + HostModes.ConsoleTitle + "”窗口：\r\n" + ex.Message,
                    WindowTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private static string QuoteArg(string s)
        {
            return "\"" + s.Replace("\"", "") + "\"";
        }

        private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (e.IsSuccess)
            {
                if (!web.Visible)
                {
                    web.Visible = true;
                    statusPanel.Visible = false;
                    web.BringToFront();
                    web.Focus();
                }
                UpdateTitle();
                return;
            }
            if (e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled) return;
            HostLog.Write("Navigation failed: " + e.WebErrorStatus);
            ShowStatus("无法加载 TAI 界面",
                "连接本地服务 " + ServerLauncher.BaseUrl + " 失败（" + e.WebErrorStatus + "）。\r\n\r\n" +
                "服务可能已停止，点击“重试”将重新检查并在需要时启动服务。", true, false);
        }

        private void OnProcessFailed(object sender, CoreWebView2ProcessFailedEventArgs e)
        {
            HostLog.Write("WebView2 process failed: " + e.ProcessFailedKind + " " + e.Reason);
            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
            {
                // The controller is unusable now; a fresh WebView would require recreating the control.
                ShowStatus("界面进程已退出",
                    "WebView2 浏览器进程意外退出（" + e.Reason + "）。请关闭并重新打开“" + WindowTitle + "”。", false, false);
                return;
            }
            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited ||
                e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
            {
                ShowStatus("界面已停止响应", "页面进程异常（" + e.ProcessFailedKind + "），点击“重试”重新加载。", true, false);
            }
        }

        private void UpdateTitle()
        {
            // Keep the product name prominent regardless of the page title.
            Text = WindowTitle;
        }

        private void ShowStatus(string title, string detail, bool canRetry, bool showDownload)
        {
            Ui(() =>
            {
                statusTitle.Text = title;
                statusDetail.Text = detail;
                SetButtons(canRetry, showDownload);
                statusPanel.Visible = true;
                web.Visible = false;
                statusPanel.BringToFront();
                if (canRetry) retryButton.Focus();
            });
        }

        private void SetButtons(bool canRetry, bool showDownload)
        {
            retryButton.Visible = canRetry;
            downloadButton.Visible = showDownload;
            logButton.Visible = canRetry || showDownload;
            closeButton.Visible = canRetry || showDownload;
        }

        private void Ui(Action action)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(action); } catch (ObjectDisposedException) { } catch (InvalidOperationException) { }
                return;
            }
            action();
        }

        private static void OpenExternal(string uri)
        {
            try
            {
                Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                HostLog.Write("Open external failed: " + uri + " " + ex.Message);
            }
        }

        private static void OpenLogFolder()
        {
            try
            {
                Directory.CreateDirectory(HostLog.Directory);
                Process.Start(new ProcessStartInfo(HostLog.Directory) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                HostLog.Write("Open log folder failed: " + ex.Message);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                closing.Dispose();
                web.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
