using ModelContextProtocol.Server;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    public static class HmiEditingTools
    {
        [McpServerTool(Name = "GetHmiEditableObject"), Description("Inspect exact existing Classic HMI screen/template/global-elements/tag/connection SDK attributes and writable scalar types. Use canonical objectPath from HMI reading tools, including category and folders. No aliases, Unified editing, create, delete or rename. No project mutation.")]
        public static HmiEditResult GetHmiEditableObject(string softwarePath, string objectPath)
            => HmiEditing.Get(McpServer.Portal, softwarePath, objectPath);

        [McpServerTool(Name = "SetHmiObjectAttributes"), Description("WRITE existing Classic HMI object via public V21 SetAttributes, only SDK-advertised readable/writable scalar attributes. Requires expectedValues for every changed field, exports backup first, then verifies typed SDK readback. Partial failures are possible; inspect mutationAttempted and postconditionVerified. No rename, save, compile or automatic rollback.")]
        public static HmiEditResult SetHmiObjectAttributes(string softwarePath, string objectPath,
            [Description("Exact attribute names and JSON scalar values; enum values are exact declared names. Inspect GetHmiEditableObject first.")] Dictionary<string, JsonElement> values,
            [Description("Expected current scalar values; must include every changed field.")] Dictionary<string, JsonElement> expectedValues,
            [Description("Explicit absolute local .xml backup path, nonexistent file in an existing caller-controlled directory.")] string backupPath)
            => HmiEditing.Set(McpServer.Portal, softwarePath, objectPath, values, expectedValues, backupPath);

        [McpServerTool(Name = "ExportHmiObject"), Description("Export one existing Classic screen/template/global-elements/tag/connection with public V21 Export(WithDefaults), excluding read-only values for editing. Writes a new local XML artifact, does not mutate the project. Returns exact-byte sha256. Rejects existing output files.")]
        public static HmiEditResult ExportHmiObject(string softwarePath, string objectPath,
            [Description("Explicit absolute local .xml artifact path, nonexistent file in an existing caller-controlled directory.")] string backupPath)
            => HmiEditing.Export(McpServer.Portal, softwarePath, objectPath, backupPath);

        [McpServerTool(Name = "ImportHmiObject"), Description("WRITE one existing Classic screen/template/global-elements object using public V21 composition Import(Override) or HmiTarget.ImportScreenGlobalElements. Input must be one matching Simatic ML object with unchanged name. Requires edited input SHA256 and expected current attributes; backs up before import and conservatively checks re-export. Can change/delete child screen content as specified by XML. No top-level create/delete/rename, Unified, tag/connection import, save, compile or automatic rollback. A returned SDK import is not success unless postconditionVerified is true.")]
        public static HmiEditResult ImportHmiObject(string softwarePath, string objectPath,
            [Description("Existing absolute local edited .xml file.")] string importPath,
            [Description("SHA256 of edited import file bytes, not the original export.")] string expectedSha256,
            [Description("1..128 expected CURRENT readable scalar attributes from GetHmiEditableObject, checked before import. This is field-level concurrency protection, not a full screen-content version.")] Dictionary<string, JsonElement> expectedValues,
            [Description("Nonexistent absolute local .xml backup file. Re-export uses backupPath + .verified.xml; both paths must be free.")] string backupPath)
            => HmiEditing.Import(McpServer.Portal, softwarePath, objectPath, importPath, expectedSha256, expectedValues, backupPath);
    }
}
