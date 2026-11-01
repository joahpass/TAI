using ModelContextProtocol.Server;
using System.ComponentModel;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    public static class BlockEditingTools
    {
        [McpServerTool(Name = "DeleteBlock"), Description("Delete one exact PLC block after a verified target backup. Rejects wildcard paths, safety, system and protected blocks. Does not compile or save. Inspect Succeeded, Meta.success, mutationAttempted and backupPath.")]
        public static BlockEditResult DeleteBlock(
            [Description("Exact software device-item path from the project tree; no wildcard or regex.")] string softwarePath,
            [Description("Exact block path relative to Program blocks, or including its actual root group name; no wildcard.")] string blockPath,
            [Description("Absolute backup parent directory. A unique child directory is created; existing files are never reused.")] string backupPath)
            => BlockEditing.DeleteBlock(McpServer.Portal, softwarePath, blockPath, backupPath);

        [McpServerTool(Name = "RenameBlock"), Description("Rename one exact PLC block after a verified target backup and software-wide name conflict check. Rejects safety, system and protected blocks. Does not compile or save; callers must validate references and compile separately.")]
        public static BlockEditResult RenameBlock(string softwarePath, string blockPath,
            [Description("New literal block name, without path separators or wildcard characters.")] string newName,
            [Description("Absolute backup parent directory; a unique child directory is created.")] string backupPath)
            => BlockEditing.RenameBlock(McpServer.Portal, softwarePath, blockPath, newName, backupPath);

        [McpServerTool(Name = "BackupSoftwareForEdit"), Description("Back up every current PLC block, including unit, system and technology blocks. Try XML for consistent blocks, otherwise actual V21 document export. complete is true only when enumeration finishes and every block has verified nonempty files. Reports per-block blockers; does not compile or save. This is a block backup, not a whole-project archive.")]
        public static BlockEditResult BackupSoftwareForEdit(string softwarePath,
            [Description("Absolute backup parent directory; a unique child directory is created.")] string backupPath)
            => BlockEditing.BackupSoftwareForEdit(McpServer.Portal, softwarePath, backupPath);
    }
}
