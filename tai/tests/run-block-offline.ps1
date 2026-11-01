$ErrorActionPreference = 'Stop'
$testPath = Join-Path $PSScriptRoot 'block-editing-offline.ps1'
$assemblyPath = Join-Path $PSScriptRoot '../../mcp-v21/bin/Release/net48/TiaMcpServer.exe'
$source = [IO.File]::ReadAllText($testPath, [Text.Encoding]::UTF8)
& ([ScriptBlock]::Create($source)) -AssemblyPath $assemblyPath -SdkPath 'E:/TIA Portal/Portal V21/PublicAPI/V21/net48'
