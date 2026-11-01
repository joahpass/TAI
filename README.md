# TAI 工程助手

TAI 是 Windows 本地 TIA Portal V19 / V21 工程助手，提供多项目、多设计标签、工程上下文管理及 PLC / HMI 工具。

[下载安装包](https://github.com/joahpass/TAI/releases/latest) · [工作台说明](tai/WORKSPACE.md)

![TAI 多项目工作台](tai/TAI-workspace-preview.png)

## 安装

从 Releases 下载安装 EXE。需要 Windows x64、.NET Framework 4.8、WebView2 Runtime 和匹配版本的 TIA Portal Openness。

向导包括欢迎、安装选项、安装进度和完成页面。默认选择本机已安装且具有匹配 Openness 组件的最高支持版本，V21 优先于 V19；也可手动指定。升级沿用原安装目录。安装前退出 TAI 托盘集成并关闭 TAI 窗口。

安装完成可直接启动工作台。模型接口在“设置”中配置；配置与会话默认保存在当前用户的 `%LOCALAPPDATA%\TAI`，卸载保留用户数据。

![TAI 安装向导](tai/installer/installer-preview.png)

## 检查更新

工作台左下角“设置”提供“检查更新”。软件读取本仓库最新稳定 GitHub Release，与当前版本比较，提供发布页面链接。下载新版安装包后运行升级，不自动执行安装包。

开发部署可通过 `TAI_GITHUB_REPOSITORY=owner/repo` 覆盖更新仓库；默认由应用元数据指向 `joahpass/TAI`。网络错误可重试，结果会缓存。

## 源码运行

```bash
cd tai/frontend
npm ci
npm run build
cd ..
npm start
```

需要 Node.js 22 或更新。打开 `http://127.0.0.1:17821/workspace`。`/assistant` 为紧凑助手，`/settings` 与 `/` 为配置和诊断。

## 构建

保留 `tai/` 与 `mcp-v21/` 相邻布局。MCP 需要两版对应 Siemens SDK，这些 SDK 不随仓库和安装包分发。参见 [MCP 项目配置](mcp-v21/TiaMcpServer.csproj)。

```bash
cd tai/desktop
bash build-release.sh --online
```

参见 [桌面构建说明](tai/desktop/RELEASE.txt) 和 [V21 AddIn 构建脚本](tai/tia-addin/build.ps1)。准备好两版 MCP、AddIn、前端构建及 Node 后：

```powershell
pwsh -NoProfile -File tai/installer/Build.ps1 -DesktopDir <desktop-release-dir> -Version 0.10.0
```

产物输出至 `tai/dist`。

## 测试

前端运行 `npm --prefix tai/frontend test`。后端离线验证和浏览器用例参见 [工作台说明](tai/WORKSPACE.md)；更新检查测试为 `node --test tai/server/updates.test.mjs`。

[向导检查](tai/installer/Test-Wizard.ps1) 覆盖页面导航、最高版本选择、进度与失败重试。[隔离安装检查](tai/installer/Test-Installer-Offline.ps1) 使用临时目录及 SDK/验证器替身，覆盖安装、升级、版本切换和卸载。生产安装器保留真实组件预检；隔离检查不代表真实博途联调已完成。

本次发布验证采用离线测试。真实 TIA 测试脚本需确认对应工程和会话范围后执行。
