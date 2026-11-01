# TAI 使用说明（0.9.4，V19 / V21）

TAI 是 Windows 本机工程助手：通过 Openness 在博途窗口内提供 AI 悬浮球，读写 PLC / HMI 工程对象，并允许用户配置自己的模型服务。安装包自带 Node 运行时、前端、桌面宿主和 V19 / V21 两套 MCP 后端。

---

## 1. 安装与升级

运行 `TAI-Setup-V19-V21-0.9.4.exe`。

**安装目录**：首次默认 `%LOCALAPPDATA%\Programs\TAI`；已安装过则自动沿用注册位置，也可浏览更换。升级请保留原目录。

**博途版本**：下拉框有「自动检测（沿用已安装版本）」「V19」「V21」，可随时切换，安装过程中也能改。自动检测的优先顺序：

1. 本次升级沿用已安装记录的版本（`tia-runtime.json`），避免升级时静默换后端；
2. 没有记录时，两者都检测到就选 **V21**（较新的主线版本）；
3. 只有 V19 时选 V19。

> 历史说明：早期版本固定“优先 V19”，是因为安装包最早只支持 V19，V21 后端是后加的，当时为了不改变老机器行为而固定下来。两个后端其实互不依赖（V19 44 个工具、V21 42 个工具），所以现在改成上述规则。

**博途目录**：按所选版本自动探测——注册表 `_InstalledSW\TIAP{19|21}\TIA_Opns` 的 `Path`、`Openness\{ver}.0\PublicAPI` 登记的 DLL 位置、`TAI_TIA_LOCATION` / `TiaPortalLocation` 环境变量、`Program Files` 下的标准目录；候选逐个用程序集主版本核对，错版本或损坏的候选直接跳过。目录随版本自动更新；手动浏览过的目录不会被自动检测覆盖。填的是 Portal 根目录（如 `E:\TIA Portal\Portal V21`），不是工程目录，也不是 `PublicAPI` 子目录。

**安装后验证**（每次安装必做）：

1. 解压目录先自检：载荷逐文件 SHA256、必需组件、真实 HTTP 服务、前端资源、MCP 初始化与工具清单；
2. 复制完成后再自检：`.tai-install.json` 中全部受管文件散列、`/api/status` 版本、`/api/config` 默认版本与路径、`/` 与每个 `/assets/*` 的字节、`/api/tools` 完整工具清单与参数 schema、离线 `GetState` 的连接状态、已部署 `TAI.addin` 的散列与容器结构。

验证使用随机本机端口和临时用户数据目录，不改模型密钥、会话或当前工程，结果写入安装目录 `install-verification.json`，日志在 `%LOCALAPPDATA%\TAI\logs\setup-*.log`。两阶段都通过才显示完成并启动集成；**验证失败时旧版文件保持原样**，按日志处理后重试。

**升级前**：托盘 TAI 图标右键 →「退出 TIA 集成」，关闭 TAI 配置窗口。安装器不会结束任何进程，检测到占用会直接拒绝并列出 PID（不要为此结束博途）。

**跨版本重装**同一 TAI 目录时，安装器按散列校验后迁移自己拥有的 Add-In；V19 使用悬浮球，不部署 Add-In，也不要求 `%APPDATA%` 可写。

**命令行等价参数**：

```powershell
& '.\TAI-Setup-V19-V21-0.9.4.exe' -TiaVersion 21
& '.\TAI-Setup-V19-V21-0.9.4.exe' -TiaVersion 19 -TiaLocation 'D:\Siemens\Automation\Portal V19'
```

隔离安装（测试用）：`-InstallRoot <绝对路径> -AddInRoot <绝对路径> -NoRegistration`，两目录必须显式给出且不能是默认位置。

**前置条件**：匹配版本的 TIA Portal 与 Openness、.NET Framework 4.8、WebView2 Runtime。Siemens DLL 不随安装包分发，各后端在运行机器上解析对应 `PublicAPI`。

---

## 2. 悬浮球与窗口集成

安装后集成管理器随登录启动，并对每个识别到的博途主窗口挂一个圆形 **AI** 悬浮球：单击展开/收起，按住拖动改位置（拖动不会触发点击），窗口缩放时限制在客户区内。

实现方式是真实 Win32 子窗口（`WS_CHILD`）挂到博途 `ADWorkbench` 主窗口，DPI 跟随博途窗口；博途最小化时一起隐藏，退出时助手随之关闭。**这不是 Siemens 官方停靠 API，也不修改、注入或挂钩任何 Siemens 程序**；面板展开会覆盖编辑区右侧，不触发编辑区重排。

相关命令：

```powershell
& "$env:LOCALAPPDATA\Programs\TAI\TAI.Desktop.exe" --watch            # 托盘集成
& "$env:LOCALAPPDATA\Programs\TAI\TAI.Desktop.exe" --stop-integration # 只停集成，不动博途
```

V21 的工程树右键菜单「TAI 工程助手 → 打开助手」只是备用入口；V19 没有该菜单，用悬浮球即可。悬浮球出现不等于已通过 Openness 连接工程。

---

## 3. 配置模型与博途

从开始菜单或托盘打开「TAI 配置与诊断」：

- **模型协议**：OpenAI 兼容（Chat Completions）或 Anthropic（Messages）。
- **基础地址**：如 `https://api.openai.com/v1`、`https://api.anthropic.com`。
- **模型名称**：供应商实际提供的 ID。
- **API Key**：保存后不再回显；留空表示保留原值，清除需用清除选项。
- **TIA 版本 / TIA 路径**：与当前工程一致；切换版本会停止原 MCP 并断开连接。
- **MCP 路径**：默认用安装包对应后端（`mcp\v19` 或 `mcp\v21`），通常无需修改。
- **系统提示词**：可编辑、导入 UTF-8 txt/md/json（仅填草稿，保存后下一轮生效）、导出与恢复默认。

V19/V21 同时安装时，切换版本要同时核对路径。多个博途进程并存时，连接时显式选择进程；多个悬浮球共用同一个本地服务的连接状态，不能凭球所在窗口判断当前连接对象。

---

## 4. 连接与首次使用

1. 博途中打开目标工程（建议先用工程副本联调）。
2. 展开悬浮球并连接；多实例时指定进程 ID。
3. 博途会弹出「Openness 访问」确认，核对程序后点允许——**这一步只能人工确认**，不点会一直等待直到超时。
4. 先查状态、工程名和设备列表，确认连的是预期工程。

建议第一条指令：

> 只读检查当前连接的工程名称、设备和软件列表，不修改工程。

连接失败常见原因：TIA 版本与 SDK 不匹配、Openness 未安装、当前 Windows 用户不在 **Siemens TIA Openness** 组（改完组需注销重登）、博途弹了授权窗未点允许。新版会在 `desktop.log` 和 MCP stderr 里记录可见进程列表、异常链和已加载 Siemens 程序集位置。

独立核对连接（只读，不保存工程）：

```powershell
& "$env:LOCALAPPDATA\Programs\TAI\runtime\node.exe" `
  "$env:LOCALAPPDATA\Programs\TAI\installer\verify-connect.mjs" `
  "$env:LOCALAPPDATA\Programs\TAI" <TIA进程PID> "$env:TEMP\tai-connect.json"
```

---

## 5. 能做什么，边界在哪

工具总数：**V19 44 个，V21 42 个**。V19 完整保留 V21 的 42 个工具名称、参数与默认值，另加 `GetCompatibilityInfo` 和 `ImportExternalSource`。

| 范围 | 能力 |
| --- | --- |
| 工程与连接 | 指定进程连接、状态、工程列表、打开/关闭、保存/另存；本地 Multiuser 会话打开/保存/关闭，不隐式提交服务器 |
| 设备与软件 | 工程树、规范路径、设备/设备项/软件属性、PLC 软件树、属性读取失败诊断 |
| PLC 查询 | 块/类型信息、正则筛选、组层级、XML 导出、批量导出 |
| PLC 修改 | XML 块/类型导入、删除、重命名、保护过滤、名称冲突检查、修改前后复核 |
| 备份 | 用户块、系统块、普通/安全单元及技术对象；XML 可解析性、字节数、SHA256、库存与修改时间稳定性校验 |
| SCL | V19 用 `ImportExternalSource`；生成前备份类型与程序块，生成后编译 |
| Classic HMI | 软件、画面、模板、变量表、连接读取；画面 XML 分页；已有对象可写标量属性修改；画面/模板 XML 回导与复核 |
| Unified HMI | 按运行时公开成员读取；与原版一致，不开放写入 |
| 编译/保存 | 保留编译状态、错误/警告计数与递归消息；编译失败阻止助手保存 |

**明确的限制**：

- 「工具接口移植」不等于拥有 V21 新增文件格式。`ExportAsDocuments`、`ExportBlocksAsDocuments`、`ImportFromDocuments`、`ImportBlocksFromDocuments` 对应 V20+ API，V19 保留输入契约但返回版本不支持；改扩展名不构成格式转换。V21 工程也不会因此降存为 V19。
- 不支持 Unified 写入，不支持顶层对象新建/删除/改名，不支持变量与连接的 XML 导入。
- Classic 画面内部控件通过 XML 修改；V19 的不一致/受保护块可能无法完整 XML 备份，备份不完整时停止自动修改，不把不完整备份标成成功。
- 不支持完整 HMI 自动生成、硬件组态和在线下载。

---

## 6. 日常操作与写入流程

**只读查看**

> 列出 PLC 块及分组，读取指定块信息并解释功能。

> 列出 HMI 画面、变量表和连接；读取 Main 画面内容。

工具返回的设备、软件、块路径要原样使用（含大小写与分组名）。

**修改 PLC**

> 先备份指定 PLC 的程序块，修改指定功能块，核对结果并编译，报告错误与警告；暂不保存。

切到写入模式后执行。写入流程包含备份、修改、复核与自动编译，失败会保留诊断并阻止保存；确认编译结果后再明确要求保存。

**修改 HMI**

> 读取指定画面的可编辑属性，先备份，把 Width 改成指定值，读回核对并编译。

修改字段要求 `expectedValues` 做并发校验，写前导出对象备份，写后按类型读回核对；对象备份不等于整个工程备份，部分写入后不保证自动回滚。

**源文件与 XML**

- PLC 块/类型：用对应 XML 导入工具。
- 普通 SCL：V19 用 `ImportExternalSource`。
- SIMATIC SD `.s7dcl/.s7res`：V20+ 能力，V19 明确返回版本限制。

---

## 7. 本地服务与接口

本地服务只监听 `127.0.0.1`（默认 `17821`，可用 `TAI_PORT` 改），会校验 Host、Origin、`Sec-Fetch-Site` 与 JSON Content-Type。主要接口：

| 接口 | 说明 |
| --- | --- |
| `GET /api/status` | 应用版本、MCP running/connected/toolCount、模型配置状态、busy |
| `GET/PUT /api/config` | 读写配置；永不返回密钥 |
| `GET /api/tools` | 工具清单与 `effect: read/write/lifecycle` |
| `POST /api/tools/call` | 直接调用 MCP 工具（诊断用，不走助手备份流程） |
| `GET /api/tia/state`、`POST /api/tia/connect`、`/disconnect` | 状态与连接控制 |
| `GET/POST/DELETE /api/conversations…` | 会话读写 |
| `POST /api/chat` | NDJSON 流：`status` / `tool_start` / `tool_end` / `text` / `done` / `error` |
| `POST /api/chat/cancel` | 取消；单任务互斥，忙时返回 409 |

工具结果遵循 MCP 结构 `{content:[{type:'text',text}],isError?,structuredContent?}`。只读模式只暴露检查类工具；写入模式允许工程修改，但不含物理下载工具。取消会阻止后续步骤，已开始的 TIA 操作可能执行完。

---

## 8. 数据、日志与安全

- 配置、会话、工件：`%LOCALAPPDATA%\TAI`（可用 `TAI_DATA_DIR` 改），与安装目录分离，卸载保留。
- 桌面日志：`%LOCALAPPDATA%\TAI\logs\desktop.log`
- 安装日志：`%LOCALAPPDATA%\TAI\logs\setup-*.log`
- 安装归属与版本：安装目录 `.tai-install.json`、`tia-runtime.json`
- 安装后验证结果：安装目录 `install-verification.json`
- 配置文件内含 API Key，仅本地服务读取，浏览器拿不到；**当前是明文文件存储，尚未接 Windows DPAPI**，不要共享该目录。会话与工程上下文会发送给你配置的模型服务。

分享诊断时不要附 API Key 或完整配置文件。退出集成只收起宿主，不关闭博途、不保存或修改工程。卸载用 Windows「已安装的应用」中的 TAI，或安装目录 `installer\Uninstall.ps1`；卸载会清理安装器自己生成的文件，保留你的配置、会话和未知文件。

---

## 9. 排查

**悬浮球不出现**

1. 核对实际安装版本：安装包生成或下载完不等于已升级；旧版用的是 Copilot 图标入口。
2. 核对托盘集成进程，必要时 `--stop-integration` 后重新 `--watch`。
3. 进入工程视图：启动画面/起始页不一定有目标主窗口。
4. 看 `desktop.log`：新版应出现 `floating ball`；仍是 `CopilotTarget` / `icon overlay` 说明旧桌面程序还在跑。
5. 权限级别：博途与 TAI 应为同一用户、兼容的权限级别，跨权限窗口挂载会失败。

**连接失败**

按第 4 节顺序排查；多实例必须指定 PID；授权窗未点会超时。安装器已启用 MCP 日志，失败时能看到可见进程、异常链和程序集路径。

**安装被拒**

提示程序仍在运行 → 退出集成、关闭配置窗口后重试，不要结束博途。提示根目录未标记/受保护 → 该目录不归 TAI 管理，换个目录或用卸载重装。提示文件被修改 → 有人改过受管文件，安装器拒绝覆盖。

---

## 10. 开发与构建

```bat
:: 前端与服务
npm --prefix frontend install
npm run build
npm start                :: http://127.0.0.1:17821

:: 测试
npm test                 :: 后端/契约，离线
npm --prefix frontend test

:: 桌面宿主
dotnet build desktop/TAI.Desktop.csproj -c Release

:: V19 / V21 后端（输出到 mcp-v21\bin\V19|V21\Release\net48）
dotnet build mcp-v21/TiaMcpServer.csproj -c Release -p:TiaMajorVersion=19
dotnet build mcp-v21/TiaMcpServer.csproj -c Release -p:TiaMajorVersion=21

:: 安装包（需先构建 frontend、desktop、tia-addin 与上面两个后端）
powershell -NoProfile -ExecutionPolicy Bypass -File installer/Build.ps1
```

安装包测试：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File installer/Test-Wizard.ps1     -SetupExe dist/TAI-Setup-V19-V21-0.9.4.exe
powershell -NoProfile -ExecutionPolicy Bypass -File installer/Test-Package-Matrix.ps1 -SetupExe dist/TAI-Setup-V19-V21-0.9.4.exe -PreviousExe dist/TAI-Setup-V19-0.9.3.exe
powershell -NoProfile -ExecutionPolicy Bypass -File installer/Test-Rollback.ps1   -SetupExe dist/TAI-Setup-V19-V21-0.9.4.exe
```

`Test-Package-Matrix.ps1` 覆盖：V19/V21 新装、重复安装、旧版升级、V19↔V21 原位切换、干净卸载（目录本身删除）、卸载保留用户文件、卸载后无残留标记；`Test-Rollback.ps1` 用故意失败的预检验证旧安装零改动且可重试。

---

## 11. 本版验证范围

**实际通过**（0.9.4，真实 EXE 与真实安装目录）：

| 项目 | 结果 |
| --- | --- |
| 向导渲染、.NET 4.8 / WebView2 检测、版本↔目录联动 | 通过 |
| V19 新装（SDK 标识夹具） | 206 受管文件、2 前端资源、44 工具、无 AddIn |
| V19 重复安装 / 0.9.3→0.9.4 升级 | 通过，用户文件保留 |
| V21 新装（本机真实 V21 SDK） | 206 受管文件、42 工具、AddIn 散列一致 |
| V21 重复安装 / 0.9.3→0.9.4 升级 | 通过 |
| V19→V21→V19 原位切换 | 通过，旧 V21 AddIn 已移除 |
| 干净卸载 / 保留用户文件卸载 | 通过 |
| 预检拒绝时旧安装保全并可重试 | 通过 |
| 后端与契约测试 | 43/43 |

**V21 Connect 实测状态**：安装后的 V21 后端已枚举到唯一实例并进入 `Attach()`，日志无程序集加载错误、无 `Connection to TiaPortal failed`；随后博途弹出「Openness 访问」等待人工允许，240 秒超时。也就是说解析与进程选择链路已验证通过，最终连接成功取决于在博途里点一次允许。

**未覆盖**：

- V19 真实连接与工程操作：本机没有 V19，只用带 `19.0.0.0` 标识的替身 SDK 验证了版本选择、目录校验、后端打包与 44 工具注册。真实 V19 需在装有 V19 的目标机上验证。
- 真实工程的导入、编译、保存与 HMI 写回：本轮未对任何真实工程执行写操作。
- 复制阶段非事务式：断电或磁盘故障后需按 `setup-*.log` 重试（预检失败不会破坏旧安装）。
