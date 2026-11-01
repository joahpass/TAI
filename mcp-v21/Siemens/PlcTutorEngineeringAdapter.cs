using PlcTutor.Engineering;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TiaMcpServer.Siemens
{
    public sealed class PlcTutorEngineeringAdapter : ITiaEngineeringService
    {
        private readonly Portal portal;

        public PlcTutorEngineeringAdapter(Portal portal)
        {
            this.portal = portal;
        }

        public ProjectSnapshot GetSnapshot(bool connectIfAvailable = true)
        {
            if (connectIfAvailable && !portal.IsConnected())
            {
                portal.ConnectPortal();
            }

            var snapshot = new ProjectSnapshot
            {
                Connected = portal.IsConnected(),
                Source = portal.IsConnected() ? "tia-mcp-openness" : "local-sources",
                ProjectName = portal.GetState().Project ?? "未连接工程"
            };
            if (portal.ProjectIsValid)
            {
                foreach (var device in portal.GetDevices())
                {
                    var deviceSnapshot = new DeviceSnapshot
                    {
                        Name = device.Name,
                        Type = device.GetType().Name
                    };
                    foreach (var item in Flatten(device.DeviceItems))
                    {
                        var container = item.GetService<SoftwareContainer>();
                        if (container?.Software != null) deviceSnapshot.Software.Add(container.Software.Name);
                        if (container?.Software is PlcSoftware plc)
                        {
                            snapshot.Cpu = plc.Name;
                            AddBlocks(plc.BlockGroup, "", snapshot.Blocks);
                        }
                    }
                    snapshot.Devices.Add(deviceSnapshot);
                }
            }
            snapshot.Fingerprint = ChangeSetFactory.Hash(string.Join(
                "|",
                snapshot.Blocks
                    .OrderBy(block => block.Path, StringComparer.OrdinalIgnoreCase)
                    .Select(block => $"{block.Path}:{block.Status}:{block.Language}")))
                .Substring(0, 12);
            return snapshot;
        }

        public ChangeSet StageChange(ChangeRequest request)
        {
            return ChangeSetFactory.Create(request, GetSnapshot().Fingerprint);
        }

        public ExportEvidence ExportBlockDocuments(string blockName, string outputDirectory)
        {
            var block = FirstPlcSoftware()
                .SelectMany(software => EnumerateBlocks(software.BlockGroup))
                .FirstOrDefault(item => string.Equals(item.Name, blockName, StringComparison.OrdinalIgnoreCase));
            if (block == null) throw new InvalidOperationException($"未找到程序块：{blockName}");
            if (!block.IsConsistent) throw new InvalidOperationException($"程序块 {blockName} 不一致，请先编译");
            Directory.CreateDirectory(outputDirectory);
            var result = block.ExportAsDocuments(new DirectoryInfo(outputDirectory), block.Name);
            return new ExportEvidence
            {
                Success = result != null && result.State == DocumentResultState.Success,
                ObjectName = blockName,
                Directory = outputDirectory,
                Files = Directory.GetFiles(outputDirectory, block.Name + ".*").ToList(),
                Message = result != null && result.State == DocumentResultState.Success
                    ? "程序块文档已导出"
                    : "文档导出失败"
            };
        }

        public RecoveryEvidence ExportRecovery(ChangeSet changeSet, string outputDirectory)
        {
            if (changeSet.ProjectFingerprint != GetSnapshot().Fingerprint)
                throw new InvalidOperationException("工程基线已变化");
            var export = ExportBlockDocuments(changeSet.BlockName, outputDirectory);
            changeSet.Recovery = new RecoveryEvidence
            {
                Required = true,
                Exported = export.Success,
                Directory = export.Directory,
                Files = export.Files,
                Message = export.Message
            };
            return changeSet.Recovery;
        }

        public CompileEvidence Compile()
        {
            var software = FirstPlcSoftware().FirstOrDefault();
            if (software == null) return new CompileEvidence { Message = "工程中没有 PLC 软件" };
            var result = software.GetService<ICompilable>().Compile();
            return new CompileEvidence
            {
                Executed = true,
                Success = result.State != CompilerResultState.Error,
                ErrorCount = result.ErrorCount,
                WarningCount = result.WarningCount,
                Message = $"编译完成：{result.ErrorCount} 个错误，{result.WarningCount} 个警告"
            };
        }

        public ApplyEvidence Apply(ChangeSet changeSet, string confirmationToken, bool saveProject)
        {
            throw new InvalidOperationException(
                "MCP 外壳保持只读；受控应用必须由 PLC 学伴桌面端在一次性测试工程中执行");
        }

        private IEnumerable<PlcSoftware> FirstPlcSoftware()
        {
            if (!portal.ProjectIsValid) yield break;
            foreach (var device in portal.GetDevices())
            foreach (var item in Flatten(device.DeviceItems))
            {
                var container = item.GetService<SoftwareContainer>();
                if (container?.Software is PlcSoftware plc) yield return plc;
            }
        }

        private static void AddBlocks(PlcBlockGroup group, string parentPath, ICollection<BlockSnapshot> target)
        {
            foreach (var block in group.Blocks)
            {
                var path = string.IsNullOrEmpty(parentPath) ? block.Name : parentPath + "/" + block.Name;
                target.Add(new BlockSnapshot
                {
                    Name = block.Name,
                    Path = path,
                    Type = block.GetType().Name,
                    Language = block.ProgrammingLanguage.ToString(),
                    Status = block.IsConsistent ? "consistent" : "inconsistent"
                });
            }
            foreach (var child in group.Groups)
            {
                var path = string.IsNullOrEmpty(parentPath) ? child.Name : parentPath + "/" + child.Name;
                AddBlocks(child, path, target);
            }
        }

        private static IEnumerable<PlcBlock> EnumerateBlocks(PlcBlockGroup group)
        {
            foreach (var block in group.Blocks) yield return block;
            foreach (var child in group.Groups)
            foreach (var block in EnumerateBlocks(child))
                yield return block;
        }

        private static IEnumerable<DeviceItem> Flatten(DeviceItemComposition items)
        {
            foreach (var item in items)
            {
                yield return item;
                foreach (var child in Flatten(item.DeviceItems)) yield return child;
            }
        }

        public void Dispose()
        {
        }
    }
}
