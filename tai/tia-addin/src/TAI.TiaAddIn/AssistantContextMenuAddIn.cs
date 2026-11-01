using System;
using System.Globalization;
using System.IO;
using Siemens.Engineering;
using Siemens.Engineering.AddIn;
using Siemens.Engineering.AddIn.Menu;
using AddInProcess = Siemens.Engineering.AddIn.Utilities.Process;
using AddInProcessStartInfo = Siemens.Engineering.AddIn.Utilities.ProcessStartInfo;

namespace TAI.TiaAddIn
{
    /// <summary>
    /// Optional fallback context menu "TAI 工程助手" -> "打开助手".
    /// Launches the separately installed TAI desktop host with "--embedded --owner &lt;TIA pid&gt;".
    /// The primary entry is the original Copilot icon, handled by the desktop watcher.
    /// </summary>
    public sealed class AssistantContextMenuAddIn : ContextMenuAddIn
    {
        public const string MenuRootLabel = "TAI 工程助手";
        public const string OpenAssistantLabel = "打开助手";
        public const string NotificationTitle = "TAI 工程助手";

        private readonly TiaPortal _tiaPortal;

        public AssistantContextMenuAddIn(TiaPortal tiaPortal)
            : base(MenuRootLabel)
        {
            _tiaPortal = tiaPortal;
        }

        protected override void BuildContextMenuItems(ContextMenuAddInRoot addInRoot)
        {
            // IEngineeringObject: shown for any project tree selection (project, device, block...).
            addInRoot.Items.AddActionItem<IEngineeringObject>(
                OpenAssistantLabel,
                OnOpenAssistant,
                OnUpdateStatus);
        }

        private static MenuStatus OnUpdateStatus(MenuSelectionProvider<IEngineeringObject> selection)
        {
            return MenuStatus.Enabled;
        }

        private void OnOpenAssistant(MenuSelectionProvider<IEngineeringObject> selection)
        {
            string exePath = DesktopLocator.GetDesktopExecutablePath();
            // "--embedded --owner <pid>": the desktop host embeds TAI as a child window inside that
            // TIA Portal main window (same panel the original Copilot icon toggles).
            if (exePath == null || !File.Exists(exePath))
            {
                ShowNotification(NotificationIcon.Error,
                    "未找到 TAI 桌面程序，请先安装 TAI。\n预期路径: " + (exePath ?? "%LOCALAPPDATA%\\Programs\\TAI\\TAI.Desktop.exe"));
                return;
            }

            int? ownerPid = TryGetOwnerProcessId();
            if (!ownerPid.HasValue)
            {
                // Embedding needs the TIA process to locate its main window. Never fall back to a
                // standalone assistant window; the login watcher (--watch) remains the primary path.
                ShowNotification(NotificationIcon.Error,
                    "无法获取 TIA Portal 进程 ID，未启动嵌入式助手。请使用 TIA 中原 Copilot 图标打开 TAI。");
                return;
            }

            var startInfo = new AddInProcessStartInfo
            {
                FileName = exePath,
                Arguments = "--embedded --owner " + ownerPid.Value.ToString(CultureInfo.InvariantCulture),
                WorkingDirectory = Path.GetDirectoryName(exePath),
                UseShellExecute = false,
            };

            try
            {
                using (var process = new AddInProcess())
                {
                    process.StartInfo = startInfo;
                    process.Start();
                    // The desktop host is independent: do not wait, do not keep a handle.
                }
            }
            catch (Exception ex)
            {
                ShowNotification(NotificationIcon.Error, "启动 TAI 桌面程序失败: " + ex.Message);
            }
        }

        /// <summary>
        /// Owner = the TIA Portal process this add-in is connected to, as reported by the
        /// public API (TiaPortal.GetCurrentProcess()). If the API cannot supply it, nothing
        /// is launched rather than guessing.
        /// </summary>
        private int? TryGetOwnerProcessId()
        {
            if (_tiaPortal == null)
            {
                return null;
            }

            try
            {
                TiaPortalProcess current = _tiaPortal.GetCurrentProcess();
                if (current != null && current.Id > 0)
                {
                    return current.Id;
                }
            }
            catch (Exception)
            {
                // Not available in this context: caller shows an error and launches nothing.
            }

            return null;
        }

        private void ShowNotification(NotificationIcon icon, string message)
        {
            try
            {
                MessageBoxProvider messageBox = _tiaPortal?.GetService<MessageBoxProvider>();
                messageBox?.ShowNotification(icon, NotificationTitle, message);
            }
            catch (Exception)
            {
                // No UI channel available; nothing else can be done safely from the add-in.
            }
        }
    }
}
