using Siemens.Engineering;
using Siemens.Engineering.SW.Blocks;
using System;
using System.Collections.Generic;

namespace TiaMcpServer.ModelContextProtocol
{
    public class Helper
    {
        public static List<Attribute> GetAttributeList(IEngineeringObject obj)
        {
            if (obj == null) throw new ArgumentNullException(nameof(obj));
            var attributes = new List<Attribute>();
            try
            {
                foreach (var attr in obj.GetAttributeInfos())
                {
                    attributes.Add(AttributeProjection.Read(attr.Name, attr.AccessMode.ToString(),
                        (attr.AccessMode & EngineeringAttributeAccessMode.Read) != 0,
                        () => obj.GetAttribute(attr.Name), SiemensAttributeProjection.TryConvert));
                }
            }
            catch (Exception ex)
            {
                // Metadata enumeration failure is an operation failure, not an empty success.
                throw new global::ModelContextProtocol.McpException(
                    $"Failed enumerating attributes for '{obj.GetType().FullName}': {AttributeProjection.DescribeException(ex)}",
                    ex, global::ModelContextProtocol.McpErrorCode.InternalError);
            }
            return attributes;
        }

        public static BlockGroupInfo BuildBlockHierarchy(PlcBlockGroup group)
        {
            var groupInfo = new BlockGroupInfo
            {
                Name = group.Name
            };

            var blockList = new List<ResponseBlockInfo>();
            foreach (var block in group.Blocks)
            {
                var attributes = Helper.GetAttributeList(block);
                blockList.Add(new ResponseBlockInfo
                {
                    Name = block.Name,
                    TypeName = block.GetType().Name,
                    Namespace = block.Namespace,
                    ProgrammingLanguage = Enum.GetName(typeof(ProgrammingLanguage), block.ProgrammingLanguage),
                    MemoryLayout = Enum.GetName(typeof(MemoryLayout), block.MemoryLayout),
                    IsConsistent = block.IsConsistent,
                    HeaderName = block.HeaderName,
                    ModifiedDate = block.ModifiedDate,
                    IsKnowHowProtected = block.IsKnowHowProtected,
                    Attributes = attributes,
                    Description = block.ToString()
                });
            }
            groupInfo.Blocks = blockList;

            var groupList = new List<BlockGroupInfo>();
            foreach (var subGroup in group.Groups)
            {
                groupList.Add(BuildBlockHierarchy(subGroup));
            }
            groupInfo.Groups = groupList;

            return groupInfo;
        }
    }
}
