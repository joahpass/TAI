using System.Collections.Generic;

namespace TiaMcpServer.ModelContextProtocol
{
    public class Attribute
    {
        public string? Name { get; set; }
        public object? Value { get; set; }
        public string? AccessMode { get; set; }
        public string? ValueType { get; set; }
        public string Status { get; set; } = "NotRead";
        public bool Succeeded => Status == "Success";
        public string? Error { get; set; }
    }

    public class BlockGroupInfo
    {
        public string? Name { get; set; }
        public IEnumerable<BlockGroupInfo>? Groups { get; set; }
        public IEnumerable<ResponseBlockInfo>? Blocks { get; set; }
    }
}
