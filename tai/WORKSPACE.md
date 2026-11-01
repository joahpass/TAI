# TAI 多项目设计工作台

本次将 TAI 的日常设计入口整理成 DSH 风格的工作台：浅色项目导航、横向设计标签、中央对话区、底部输入框及右侧工程上下文。提供浅色、深色和跟随系统三种外观。

## 启动与入口

在 `E:/PLC SIM/tai` 执行：

```bash
npm start
```

前端生产包已构建到 `frontend/dist`。如果启动前已有同端口的 TAI 服务，应先正常退出该服务再启动当前源码版本。默认地址：

- 完整工作台：<http://127.0.0.1:17821/workspace>
- 博途紧凑助手：<http://127.0.0.1:17821/assistant>
- 设置与诊断：<http://127.0.0.1:17821/settings>；原根路径继续兼容配置控制台。
- 指定设计：`/workspace?design=<设计ID>`；指定项目：`/workspace?project=<项目ID>`。

0.10.0 已包含工作台、桌面正式构建与新版安装向导，可从 https://github.com/joahpass/TAI/releases/latest 下载。桌面依赖已正常恢复，标准 SDK 在线/离线构建均通过；构建说明见 `desktop/RELEASE.txt`。此工作目录中的开发与隔离验证未覆盖本机现有安装。设置页提供 GitHub Releases 检查更新。

## 日常使用

1. 左侧创建项目，为每条生产线或设备建立分组，通过项目管理填写目标与约定。
2. 在项目旁点击 `+` 新建设计。多个设计以标签打开；关闭标签保留历史和草稿，再次打开可继续。
3. 每份设计独立保存输入内容、只读/写入模式和阅读位置。窗口重载恢复标签与草稿；新窗口使用独立的窗口状态。多窗口同时修改同一条项目或设计元数据时，以 revision 检查冲突，过期修改会保留在对话框中并提示重试。
4. 从设计详情中置顶、重命名、移动或归档设计；项目也可归档。通过左侧「已归档」与管理对话框恢复。搜索同时匹配项目与设计名称。
5. 工程操作前，在右侧工程上下文连接博途并绑定实际打开的工程。执行工程工具前服务端再次核对工程路径；切换工作项目不会替换实际博途连接。配置页工具调试器也需选中已绑定的工作项目。
6. 设计可以并列打开，博途任务仍串行执行。运行时切换标签可以继续编写其他设计的草稿；顶部任务条显示原设计，可定位或取消。取消使用 taskId 检查，防止过期操作影响后续任务。
7. 嵌入助手可从右上角打开同一设计的完整工作台。独立桌面助手默认采用 1200×820 工作台窗口；嵌入助手保持紧凑布局。

快捷键：`Ctrl K` 搜索，`Ctrl N` 新建设计，`Ctrl Alt ←/→` 切换标签；标签内可用方向键和 Home/End；`Enter` 发送，`Shift Enter` 换行。

## 数据与任务恢复

默认数据目录沿用 `%LOCALAPPDATA%/TAI`，或环境变量 `TAI_DATA_DIR` 指定的目录。首次迁移旧会话时，先独占创建 `conversations.pre-projects.json`，再归入默认项目；重复启动保留首次备份。旧工件引用保持原位置；新设计使用 `projects/<projectId>/designs/<conversationId>` 独立目录，同名文件彼此隔离。移动设计保留已有工件目录引用。

标签切换保持流式请求连接。页面重载、关闭或网络断开会中止所属请求；重新打开后读取已保存记录以核对实际结果。已发出的博途操作可能已经完成，取消后请刷新工程状态。

项目与设计列表使用摘要查询；详情按需加载。元数据支持 revision，运行中的相关设计禁止移动、删除和修改工程绑定。用户手动命名保留，只有默认「新设计/新对话」在首次提交时自动生成标题。

## 验证与预览

所有验证使用临时数据、fake MCP 和 fake 模型；未运行真实 TIA 工程修改测试。

```bash
npm --prefix frontend run build
npm --prefix frontend test
node tests/browser-workspace.mjs
node --test tests/projects-backend.test.mjs tests/backend.test.mjs tests/http-smoke.test.mjs tests/execution.test.mjs tests/hmi-execution.test.mjs tests/model.test.mjs tests/artifact.test.mjs tests/artifact-editing.test.mjs tests/artifact-paging.test.mjs tests/artifact-analysis.test.mjs tests/artifact-analysis-integration.test.mjs tests/tia-version.test.mjs
```

浏览器验收脚本使用本机 Edge、动态端口和临时用户目录，覆盖项目/设计创建、改名、归档恢复、置顶、绑定、标签草稿模式与重载、指定设计链接、独立窗口状态、元数据冲突、流式任务归属、定位与取消、设置入口、主题及窄屏布局。运行后输出：

- `TAI-workspace-preview.png`：浅色完整工作台。
- `TAI-workspace-dark-preview.png`：深色完整工作台。
- `TAI-assistant-workspace-preview.png`：520px 紧凑助手。

桌面隔离检查共 22 项，具体命令和依赖说明见 `desktop/checks/README.md`。
