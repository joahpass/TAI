param(
    [string]$AssemblyPath = (Join-Path $PSScriptRoot '../../mcp-v21/bin/Release/net48/TiaMcpServer.exe'),
    [string]$SdkPath = 'E:/TIA Portal/Portal V21/PublicAPI/V21/net48'
)
$ErrorActionPreference = 'Stop'
# Offline only: load public metadata and invoke pure guards. Never instantiate TiaPortal or invoke tool edits.
$base = [Reflection.Assembly]::LoadFrom((Join-Path $SdkPath 'Siemens.Engineering.Base.dll'))
$step7 = [Reflection.Assembly]::LoadFrom((Join-Path $SdkPath 'Siemens.Engineering.Step7.dll'))
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path $AssemblyPath))
$type = $assembly.GetType('TiaMcpServer.Siemens.BlockEditing', $true)
$validatePath = $type.GetMethod('ValidateLiteralPath')
$validateName = $type.GetMethod('ValidateNewName')
$checks = 0
function Assert-Check([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw "FAILED: $Message" }
    $script:checks++
}
function Expect-Rejected($Method, [object[]]$Arguments) {
    $rejected = $false
    try { $null = $Method.Invoke($null, $Arguments) }
    catch { if ($_.Exception.InnerException -is [ArgumentException]) { $rejected = $true } else { throw } }
    Assert-Check $rejected "Reject arguments: $Arguments"
}
foreach ($path in @('PLC_1', 'Device/CPU', 'Group/FB.1', 'Program blocks/用户块', '@units/Unit1/Main')) {
    $null = $validatePath.Invoke($null, @($path, 'test'))
    $checks++
}
foreach ($path in @('', ' ', '/Main', 'Main/', 'A//B', '../B', 'A/./B', 'A/*', 'A/?', 'A/[FB]', 'A\B', "A`nB", ' A')) {
    Expect-Rejected $validatePath @($path, 'test')
}
foreach ($name in @('Main', 'FB.1', '用户块')) { $null = $validateName.Invoke($null, @($name)); $checks++ }
foreach ($name in @('', 'A/B', '*', '../FB')) { Expect-Rejected $validateName @($name) }
$block = $step7.GetType('Siemens.Engineering.SW.Blocks.PlcBlock', $true)
Assert-Check $block.GetProperty('Name').CanWrite 'V21 PlcBlock.Name has a setter'
Assert-Check ($null -ne $block.GetMethod('Delete', [Type[]]@())) 'V21 PlcBlock.Delete exists'
$documents = $block.GetMethod('ExportAsDocuments', [Type[]]@([IO.DirectoryInfo], [string]))
Assert-Check ($null -ne $documents) 'V21 document export exact signature'
Assert-Check ($documents.ReturnType.GetProperty('ExportedDocuments').PropertyType -eq [Collections.Generic.IEnumerable[IO.FileInfo]]) 'Document result returns actual FileInfo objects'
# Exercise real file verification without engineering objects. Every temp artifact is owned by this test.
$temp = [string](Join-Path ([IO.Path]::GetTempPath()) ('block-editing-test-' + [Guid]::NewGuid().ToString('N')))
$null = [IO.Directory]::CreateDirectory($temp)
try {
    $inspect = $type.GetMethod('InspectFile', [Reflection.BindingFlags]'Static,NonPublic')
    $file = [string](Join-Path $temp 'backup.xml')
    [IO.File]::WriteAllText($file, '<Document />')
    $entry = $inspect.Invoke($null, @($file, $temp))
    Assert-Check ($entry.bytes -gt 0 -and $entry.sha256.Length -eq 64) 'Verified file has bytes and SHA256'
    $missing = $false
    try { $null = $inspect.Invoke($null, @([string](Join-Path $temp 'missing.xml'), $temp)) } catch { $missing = $_.Exception.InnerException -is [IO.FileNotFoundException] }
    Assert-Check $missing 'Missing backup file rejected'
    [IO.File]::WriteAllText($file, '')
    $empty = $false
    try { $null = $inspect.Invoke($null, @($file, $temp)) } catch { $empty = $_.Exception.InnerException -is [InvalidOperationException] }
    Assert-Check $empty 'Empty backup file rejected'
    $outside = $false
    try { $null = $inspect.Invoke($null, @($file, [string](Join-Path $temp 'child'))) } catch { $outside = $_.Exception.InnerException -is [InvalidOperationException] }
    Assert-Check $outside 'Export outside unique backup directory rejected'
} finally {
    # Exact random directory created by this invocation only.
    if ((Split-Path $temp -Leaf).StartsWith('block-editing-test-')) { [IO.Directory]::Delete($temp, $true) }
}
Write-Output "PASS: $checks offline block-editing checks; no TIA connection, export, delete, rename, compile or save invoked."
