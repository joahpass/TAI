// 工具中文名称（仅用于展示，未知工具显示原名）。分类以后端 /api/tools 的 effect 为准。

const LABELS = {
  GetState: '读取连接状态',
  GetProject: '读取工程信息',
  GetProjectTree: '读取项目树',
  GetDeviceInfo: '读取设备信息',
  GetDeviceItemInfo: '读取设备项信息',
  GetDevices: '列出设备',
  GetSoftwareInfo: '读取软件信息',
  GetSoftwareTree: '读取软件树',
  GetBlockInfo: '读取块信息',
  GetBlocks: '列出程序块',
  GetBlocksWithHierarchy: '列出程序块层级',
  GetTypeInfo: '读取类型信息',
  GetTypes: '列出数据类型',
  GetHmiSoftware: 'HMI软件',
  GetHmiScreens: 'HMI画面',
  GetHmiTagTables: 'HMI变量表',
  GetHmiConnections: 'HMI连接',
  GetHmiScreenContent: 'HMI画面内容',
  // HMI 对象编辑：读取可编辑属性（只读）/ 修改属性、导入（写工程）/ 导出（仅写本地文件，不改工程）。
  GetHmiEditableObject: '读取HMI可编辑对象',
  SetHmiObjectAttributes: '修改HMI对象属性',
  ExportHmiObject: '导出HMI对象',
  ImportHmiObject: '导入HMI对象',
  // 编译工具同时适用于 PLC 与 HMI 软件，因此使用通用名称。
  CompileSoftware: '编译软件',
  SaveProject: '保存工程',
  SaveAsProject: '另存工程',
  ExportBlock: '导出块',
  ImportBlock: '导入块',
  ExportBlocks: '批量导出块',
  ExportType: '导出类型',
  ImportType: '导入类型',
  ExportTypes: '批量导出类型',
  ExportAsDocuments: '导出为文档',
  ExportBlocksAsDocuments: '批量导出块文档',
  ImportFromDocuments: '从文档导入',
  ImportExternalSource: '从外部源生成程序块',
  GetCompatibilityInfo: '读取版本能力',
  ImportBlocksFromDocuments: '从文档批量导入块',
  DeleteBlock: '删除程序块',
  RenameBlock: '重命名程序块',
  BackupSoftwareForEdit: '修改前备份软件',
  Connect: '连接博途',
  Disconnect: '断开博途',
  OpenProject: '打开工程',
  CloseProject: '关闭工程',
  TAI_WriteArtifact: '保存工件文件',
  TAI_ReadArtifact: '读取工件文件',
  TAI_EditArtifact: '修改工件文件',
  // 只读工件工具：仅读取本地工件目录/文件，不触碰博途工程。
  TAI_ListArtifacts: '列出工件目录', // 目录分页列表
  TAI_InspectArtifact: '分析工件结构', // 工程文本大纲：格式/声明块/引用（词法分析，非编译校验）
  TAI_SearchArtifact: '搜索工件文本', // 字面量搜索：偏移/行号/上下文/分页
};

// 只读工件工具：不修改博途工程，也不写本地文件，手动执行无需确认。
const ARTIFACT_READ_ONLY = new Set(['TAI_ReadArtifact', 'TAI_ListArtifacts', 'TAI_InspectArtifact', 'TAI_SearchArtifact']);

export function isReadOnlyArtifactTool(name) {
  return ARTIFACT_READ_ONLY.has(name);
}

// 成功执行后会改变博途工程内容或工程文件的工具（工件文件编辑只改本地文件，不计入）。
const MODIFIES = new Set([
  'ImportBlock', 'ImportType', 'ImportFromDocuments', 'ImportExternalSource', 'ImportBlocksFromDocuments', 'DeleteBlock', 'RenameBlock', 'SaveProject', 'SaveAsProject',
  'SetHmiObjectAttributes', 'ImportHmiObject',
]);

// HMI 只读编辑辅助工具：即使后端 effect 缺失或误标，也不触发写入确认。
const HMI_READ_ONLY = new Set(['GetHmiEditableObject']);

// 会写入博途工程的 HMI 编辑工具：即使后端 effect 尚未提供，手动执行也需确认（与其他写入工具一致）。
const HMI_PROJECT_WRITE = new Set(['SetHmiObjectAttributes', 'ImportHmiObject']);

// 手动执行前需要确认的生命周期工具（可能丢失未保存更改或切换工程）。
const LIFECYCLE_CONFIRM = new Set(['OpenProject', 'CloseProject']);

export function toolLabel(name) {
  return LABELS[name] || String(name ?? '工具');
}

export function modifiesProject(name) {
  return MODIFIES.has(name);
}

/** 手动测试是否需要确认：写入类或会切换/关闭工程的生命周期工具。 */
export function needsConfirm(tool) {
  if (!tool) return false;
  if (ARTIFACT_READ_ONLY.has(tool.name) || HMI_READ_ONLY.has(tool.name)) return false;
  return tool.effect === 'write' || LIFECYCLE_CONFIRM.has(tool.name) || HMI_PROJECT_WRITE.has(tool.name);
}

/** 已完成任务中成功修改工程的工具调用。 */
export function projectChanges(tools = []) {
  const counts = new Map();
  for (const t of tools) {
    if (t.state === 'running' || t.isError || !modifiesProject(t.name)) continue;
    counts.set(t.name, (counts.get(t.name) || 0) + 1);
  }
  return [...counts].map(([name, count]) => ({ name, label: toolLabel(name), count }));
}
