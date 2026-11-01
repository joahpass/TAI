using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;

namespace TAI.Desktop
{
    internal enum ProbeState
    {
        /// <summary>/api/status answered and identified app TAI.</summary>
        Tai,
        /// <summary>Nothing listens on the port.</summary>
        Free,
        /// <summary>Something listens but is not the TAI server.</summary>
        Foreign
    }

    internal sealed class LaunchResult
    {
        public bool Ok;
        public string Title;
        public string Detail;
        public bool StartedByUs;

        public static LaunchResult Fail(string title, string detail)
        {
            return new LaunchResult { Ok = false, Title = title, Detail = detail };
        }
    }

    /// <summary>
    /// Ensures the shared local TAI server is reachable. The server is only launched when nothing
    /// answers on the port; it is never stopped by this host (other windows / browsers may use it).
    /// </summary>
    internal sealed class ServerLauncher
    {
        public const int Port = 17821;
        public static readonly string BaseUrl = "http://127.0.0.1:" + Port + "/";
        private static readonly Regex TaiApp = new Regex("\"app\"\\s*:\\s*\"TAI\"", RegexOptions.CultureInvariant);
        private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(20);

        private readonly string root;

        public ServerLauncher(string root)
        {
            this.root = root;
        }

        public string NodePath { get { return Path.Combine(root, "runtime", "node.exe"); } }
        public string AppDir { get { return Path.Combine(root, "app"); } }
        public string ServerScript { get { return Path.Combine(AppDir, "server", "index.mjs"); } }
        public string FrontendIndex { get { return Path.Combine(AppDir, "frontend", "dist", "index.html"); } }
        public string McpPath { get { return Path.Combine(root, "mcp", "TiaMcpServer.exe"); } }

        /// <summary>Blocking; call off the UI thread. progress receives short Chinese status lines.</summary>
        public LaunchResult EnsureRunning(Action<string> progress, CancellationToken cancel)
        {
            progress("正在检查本地 TAI 服务…");
            ProbeState state = Probe();
            if (state == ProbeState.Tai)
            {
                HostLog.Write("Existing TAI server detected on port " + Port);
                return new LaunchResult { Ok = true };
            }
            if (state == ProbeState.Foreign) return PortOccupied();

            // Nothing listening: validate the packaged layout before launching.
            if (!File.Exists(NodePath))
                return LaunchResult.Fail("缺少 Node.js 运行时",
                    "未找到内置运行时：\r\n" + NodePath + "\r\n\r\n安装可能不完整，请重新安装 TAI 工程助手。");
            if (!File.Exists(ServerScript))
                return LaunchResult.Fail("缺少 TAI 服务程序",
                    "未找到服务端入口：\r\n" + ServerScript + "\r\n\r\n安装可能不完整，请重新安装 TAI 工程助手。");
            if (!File.Exists(FrontendIndex))
                return LaunchResult.Fail("缺少 TAI 界面文件",
                    "未找到前端页面：\r\n" + FrontendIndex + "\r\n\r\n安装可能不完整，请重新安装 TAI 工程助手。");
            if (!File.Exists(McpPath) &&
                !File.Exists(Path.Combine(root, "mcp", "v19", "TiaMcpServer.exe")) &&
                !File.Exists(Path.Combine(root, "mcp", "v21", "TiaMcpServer.exe")))
                HostLog.Write("Warning: no packaged MCP backend found (TIA 工具将不可用)");

            progress("正在启动本地 TAI 服务…");
            Process process;
            try
            {
                process = StartServer();
            }
            catch (Exception ex)
            {
                HostLog.Write("Start node failed: " + ex);
                return LaunchResult.Fail("无法启动 TAI 服务", "启动 " + NodePath + " 失败：\r\n" + ex.Message);
            }

            HostLog.Write("Started node pid " + process.Id);
            var watch = Stopwatch.StartNew();
            try
            {
                while (watch.Elapsed < ReadyTimeout)
                {
                    if (cancel.IsCancellationRequested) return LaunchResult.Fail("已取消", "启动已取消。");
                    state = Probe();
                    if (state == ProbeState.Tai)
                    {
                        HostLog.Write("TAI server ready after " + watch.ElapsedMilliseconds + " ms");
                        return new LaunchResult { Ok = true, StartedByUs = true };
                    }
                    if (state == ProbeState.Foreign)
                    {
                        TryKill(process);
                        return PortOccupied();
                    }
                    if (process.HasExited)
                    {
                        // Another TAI instance may have won the port race; check once more.
                        if (Probe() == ProbeState.Tai) return new LaunchResult { Ok = true };
                        return LaunchResult.Fail("TAI 服务意外退出",
                            "服务进程已退出（退出代码 " + process.ExitCode + "）。\r\n\r\n" +
                            "请确认端口 " + Port + " 未被占用、安装目录完整，然后点击“重试”。");
                    }
                    progress("正在启动本地 TAI 服务…（" + (int)watch.Elapsed.TotalSeconds + " 秒）");
                    Thread.Sleep(300);
                }
                TryKill(process);
                return LaunchResult.Fail("TAI 服务启动超时",
                    "等待 " + (int)ReadyTimeout.TotalSeconds + " 秒后服务仍未就绪：\r\n" + BaseUrl + "api/status\r\n\r\n" +
                    "可能被安全软件拦截或系统负载过高，请点击“重试”。");
            }
            finally
            {
                // Releases the handle only; the server keeps running after this window closes.
                process.Dispose();
            }
        }

        private Process StartServer()
        {
            var psi = new ProcessStartInfo
            {
                FileName = NodePath,
                Arguments = Quote(ServerScript),
                WorkingDirectory = AppDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                // No redirection: the server must not depend on pipes owned by this window.
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                RedirectStandardInput = false
            };
            // The flat path is a routing base: server selects mcp/v19 or mcp/v21 from tia-runtime.json.
            // Preserve explicit environment overrides; the routing base itself need not be an existing file.
            if (string.IsNullOrEmpty(psi.EnvironmentVariables["TAI_MCP_PATH"]))
                psi.EnvironmentVariables["TAI_MCP_PATH"] = McpPath;
            psi.EnvironmentVariables["TAI_PORT"] = Port.ToString();
            psi.EnvironmentVariables.Remove("TAI_OPEN"); // never open an external browser
            return Process.Start(psi);
        }

        private static LaunchResult PortOccupied()
        {
            return LaunchResult.Fail("端口 " + Port + " 已被占用",
                "本机端口 " + Port + " 正被其他程序使用，且不是 TAI 服务。\r\n\r\n" +
                "请关闭占用该端口的程序后点击“重试”。\r\n可在命令行执行：netstat -ano | findstr :" + Port + " 查找占用进程。");
        }

        /// <summary>Identifies what (if anything) answers on the loopback port.</summary>
        public static ProbeState Probe()
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(BaseUrl + "api/status");
                request.Method = "GET";
                request.Proxy = null; // loopback must never go through a system proxy
                request.Timeout = 1500;
                request.ReadWriteTimeout = 1500;
                request.KeepAlive = false;
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream()))
                {
                    string body = reader.ReadToEnd();
                    return TaiApp.IsMatch(body) ? ProbeState.Tai : ProbeState.Foreign;
                }
            }
            catch (WebException ex)
            {
                if (ex.Status == WebExceptionStatus.ConnectFailure) return ProbeState.Free;
                // Timeout, protocol error, HTTP 404 etc.: something else holds the port,
                // unless nothing is actually listening.
                return IsListening() ? ProbeState.Foreign : ProbeState.Free;
            }
            catch (Exception)
            {
                return IsListening() ? ProbeState.Foreign : ProbeState.Free;
            }
        }

        private static bool IsListening()
        {
            try
            {
                using (var client = new TcpClient())
                {
                    IAsyncResult ar = client.BeginConnect(IPAddress.Loopback, Port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(500)) return false;
                    client.EndConnect(ar);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static void TryKill(Process p)
        {
            try { if (!p.HasExited) p.Kill(); } catch { }
        }

        private static string Quote(string s)
        {
            return "\"" + s.Replace("\"", "\\\"") + "\"";
        }
    }
}
