using ModelContextProtocol.Server;
using System.ComponentModel;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    public static class HmiReadTools
    {
        [McpServerTool(Name = "GetHmiSoftware"), Description("Discover Classic and Unified HMI software in the already selected project. Returns exact device-item paths, public SDK type and capability limits. Read-only; never attaches to TIA or opens a project.")]
        public static HmiReadResult GetHmiSoftware(
            [Description("Zero-based result offset; 0 to 100000. Keep engineering configuration unchanged between pages.")] int offset = 0,
            [Description("Maximum records returned; 1 to 500.")] int limit = 100)
            => HmiReading.GetHmiSoftware(McpServer.Portal, offset, limit);

        [McpServerTool(Name = "GetHmiScreens"), Description("Read HMI screen/folder hierarchy and public attributes. Unified also returns screen items and dynamizations as paged child records. Classic item/binding inspection is explicitly unavailable in the typed V21 API. No export or runtime connection.")]
        public static HmiReadResult GetHmiScreens(
            [Description("Exact case-sensitive softwarePath returned by GetHmiSoftware. Passed unchanged; no regex or wildcard matching.")] string softwarePath,
            [Description("Optional exact screen path returned by this tool, including category/folder segments. Empty selects all screens. Passed unchanged.")] string screenPath = "",
            [Description("Zero-based offset in the flattened hierarchy; 0 to 100000.")] int offset = 0,
            [Description("Maximum records returned, including folders/items/bindings; 1 to 500.")] int limit = 100)
            => HmiReading.GetHmiScreens(McpServer.Portal, softwarePath, screenPath, offset, limit);

        [McpServerTool(Name = "GetHmiScreenContent"), Description("Read exact serialized XML of a Classic HMI screen, template, popup, slide-in, overview or global-elements record using its public V21 Export API. Inspect each GetHmiScreens record's contentExport capability; target export restrictions may still apply. Exports only to a private temporary file; does not modify the project. For Unified use GetHmiScreens. Returns bounded pages, not a rendered preview. Continuations require prior sha256 and nextOffset.")]
        public static object GetHmiScreenContent(
            [Description("Exact softwarePath from GetHmiSoftware.")] string softwarePath,
            [Description("Exact case-sensitive canonical path from GetHmiScreens (for example screens/画面/根画面, templates/模板/模板_1, special/globalElements). Omitting only the first category segment is accepted when unique; no regex, suffix guessing or trimming. Responses return canonical screenPath; use it for continuations.")] string screenPath,
            [Description("UTF-16 character offset; zero starts a new read.")] int offset = 0,
            [Description("Page size, 1 to 20000 characters.")] int maxChars = 20000,
            [Description("Previous sha256, required when offset is positive.")] string? expectedSha256 = null)
            => HmiReading.GetHmiScreenContent(McpServer.Portal, softwarePath, screenPath, offset, maxChars, expectedSha256);

        [McpServerTool(Name = "GetHmiTagTables"), Description("Read configured HMI tag folders, tables and tags with public address/connection attributes. Unified includes structured tag members. Values are engineering configuration, not live PLC values. Inspect per-attribute diagnostics and capability limits.")]
        public static HmiReadResult GetHmiTagTables(
            [Description("Exact case-sensitive softwarePath returned by GetHmiSoftware; passed unchanged.")] string softwarePath,
            [Description("Optional exact table path returned by this tool. Empty selects all tables. Passed unchanged.")] string tablePath = "",
            [Description("Zero-based offset in flattened folder/table/tag records; 0 to 100000.")] int offset = 0,
            [Description("Maximum records returned; 1 to 500.")] int limit = 100)
            => HmiReading.GetHmiTagTables(McpServer.Portal, softwarePath, tablePath, offset, limit);

        [McpServerTool(Name = "GetHmiConnections"), Description("Read actual configured HMI connections, including S7/OPC UA driver and partner information exposed by V21. Unified driver properties are separate paged child records. Classic uses advertised readable attributes. Does not establish S7/OPC UA sessions or report online connectivity.")]
        public static HmiReadResult GetHmiConnections(
            [Description("Exact case-sensitive softwarePath returned by GetHmiSoftware; passed unchanged.")] string softwarePath,
            [Description("Zero-based offset in connection/driver-property records; 0 to 100000.")] int offset = 0,
            [Description("Maximum records returned; 1 to 500.")] int limit = 100)
            => HmiReading.GetHmiConnections(McpServer.Portal, softwarePath, offset, limit);
    }
}
