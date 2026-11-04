# TAI 代理工作区与对话

完整入口采用工作区树、跨工作区对话标签、任务记录、底部输入框和右侧工程上下文。工作区保存共用目标、指令和博途工程绑定；每条对话独立保存历史、草稿和执行模式。提供浅色、深色和跟随系统外观。

## 启动与入口

在 `E:/PLC SIM/tai` 执行 `npm start`。前端生产包位于 `frontend/dist`；启动当前源码前，应正常退出占用同端口的旧服务。

- 完整工作区：<http://127.0.0.1:17821/workspace>
- 博途紧凑助手：<http://127.0.0.1:17821/assistant>
- 设置与诊断：<http://127.0.0.1:17821/settings>；原根路径继续兼容。
- 指定对话：`/workspace?conversation=<对话ID>`。旧 `?design=<ID>` 保持兼容。
- 指定工作区首页：`/workspace?project=<工作区ID>`。

0.11.0 将工作区、权限等级和运行模型选择一起打包；正式安装包与源码在 GitHub Releases 同步发布。设置页提供 GitHub Releases 检查更新。

## 日常使用

1. 创建工作区，填写名称、说明和工作区指令。说明用于展示目标，指令会提供给该工作区中的每次对话任务，最多 20,000 字符。
2. 工作区名称恢复该窗口上次浏览的对话或首页；树旁箭头仅折叠列表。顶部工作区面包屑明确返回首页。
3. 首页展示绑定工程、运行任务和最近对话。在首页输入并首次发送后，才创建服务器对话记录；反复点击“新对话”不会产生空记录。每个工作区的首页草稿分别保留。
4. 多条对话可并列打开，标签显示所属工作区。关闭标签保留历史和草稿，再次打开可继续。新窗口的导航、草稿、模式与阅读位置独立。
5. 通过对话管理重命名、移动、归档或删除；移动后后续任务使用目标工作区的指令和工程绑定，已有历史与工件目录保留。
6. 右侧上下文可查看工作区指令、连接博途、绑定实际打开的工程和查看当前对话执行记录。每次工程工具操作前，服务端核对实际工程与绑定路径。
7. 任务仍串行执行。切换工作区或对话时，任务继续归属于原对话；可编辑其他草稿，通过任务条定位或取消。取消按 taskId 核对，防止误取消下一项任务。
8. 紧凑助手右上角可打开当前对话的完整工作区。独立桌面默认 1200×820；嵌入助手维持紧凑布局。

快捷键：`Ctrl K` 搜索，`Ctrl N` 新对话，`Ctrl Alt ←/→` 切换标签；标签内支持方向键及 Home/End；`Enter` 发送，`Shift Enter` 换行。

## 权限、模型与思考程度

输入框旁提供“只读 / 工作区内修改 / 完全权限”。只读允许查询博途与读取工件；工作区内修改增加当前工作区工件的编辑能力；完全权限允许 TAI 提供的工程写入和会话工具。工程操作仍核对工作区绑定，工件工具限制在所属对话目录内；权限选择不会授予操作系统管理员身份。

每个工作区首页和每条对话分别保存权限、模型 ID 与思考程度，切换标签或重开页面后恢复。留空模型和“跟随设置”使用全局默认，临时选择只影响该对话后续任务。设置中配置服务协议、地址、密钥与默认模型；“加载模型”从该服务读取可用模型，也支持手填 ID。

思考程度提供跟随设置、模型默认、关闭、低、中、高。模型默认不附加思考参数；显式程度按 OpenAI 或 Anthropic 协议发送。具体支持由模型服务决定，不支持时显示服务错误。当前任务冻结启动时的配置，运行中调整选项用于下一次发送。

## 归档和删除

归档保留全部数据，工作区或对话进入只读状态；首页和历史仍可浏览，通过左侧“已归档”与管理对话框恢复。工作区归档后，其下所有对话停止接收新任务，不能向归档工作区新建或移动对话。

删除与归档分开。删除对话需要再次确认；删除工作区显示所属对话数量，必须输入完整工作区名称，并连带移除所属对话记录。默认工作区禁止删除。删除会清理当前窗口相关标签、草稿和阅读位置；其他窗口刷新摘要后移除失效标签。磁盘工件文件保留。删除界面明确说明记录删除后不能从应用恢复，确认前请核对。

相关任务运行时，禁止删除、归档或移动对应记录，也禁止改变工程绑定。工作区说明及指令仍可编辑：已开始的任务使用启动时的快照，下一次任务才使用更新值。多窗口编辑或删除通过 revision 检查，过期操作返回冲突并保留对话框内容供核对。

## 数据与任务恢复

数据目录沿用 `%LOCALAPPDATA%/TAI`，或由 `TAI_DATA_DIR` 指定。旧 projects/conversations 的 ID、API 字段名和历史保持兼容。旧对话归组迁移前独占创建 `conversations.pre-projects.json`；给旧工作区补充空指令前独占创建原文 `projects.pre-instructions.json`；重复启动保留首次备份。

新工件继续使用 `projects/<projectId>/designs/<conversationId>` 隔离目录；移动对话保留原目录引用，旧工件引用保持原位置。模型只接收当前对话的历史与其所属工作区上下文，不会合并其他对话历史。

页面关闭、刷新或网络断开会中止所属流式请求；重新打开后读取保存记录核对结果。已发出的博途操作可能已经完成，取消后应刷新工程状态。摘要按需刷新，完整历史按打开的对话加载。

## 安装升级

0.11.0 安装器会先校验包内容、旧安装归属和组件，再关闭目标安装目录内的 TAI 窗口、后台、监控与 MCP 进程。配置和数据目录保持原状。清理通过精确可执行路径、创建时间及进程关联判断归属；博途和其他安装目录程序保持运行。

任务忙碌时等待约 15 秒，仍忙碌则保留任务并提示重试；状态读取异常时也保留程序，恢复后台响应或正常退出 TAI 后重试。隔离检查命令：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File installer/Test-ProcessCleanup.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File installer/Test-Installer-Offline.ps1
```

## 验证与预览

本轮验证使用临时数据、fake MCP 和 fake 模型，不启动真实博途工程。

```bash
npm --prefix frontend test
npm --prefix frontend run build
node tests/browser-workspace.mjs
node --test tests/model-selection-backend.test.mjs tests/agent-workspace-backend.test.mjs tests/projects-backend.test.mjs tests/backend.test.mjs tests/http-smoke.test.mjs tests/execution.test.mjs tests/hmi-execution.test.mjs tests/model.test.mjs tests/artifact.test.mjs tests/artifact-editing.test.mjs tests/artifact-paging.test.mjs tests/artifact-analysis.test.mjs tests/artifact-analysis-integration.test.mjs tests/tia-version.test.mjs server/updates.test.mjs
```

浏览器验收覆盖工作区首页、首次发送创建、草稿迁移、多窗口、归档恢复、删除确认、标签、指令设置、绑定、流式任务定位取消、主题、指定对话链接、设置和窄屏布局。预览：

- `TAI-workspace-home-preview.png`：工作区首页。
- `TAI-workspace-preview.png`：浅色完整工作区。
- `TAI-workspace-dark-preview.png`：深色完整工作区。
- `TAI-assistant-workspace-preview.png`：520px 紧凑助手。
