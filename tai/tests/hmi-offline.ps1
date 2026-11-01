param(
    [string]$AssemblyPath = (Join-Path $PSScriptRoot '../../mcp-v21/bin/Release/net48/TiaMcpServer.exe'),
    [string]$SdkPath = 'E:/TIA Portal/Portal V21/PublicAPI/V21/net48'
)
$ErrorActionPreference = 'Stop'
# Offline metadata and pure DTO/guard tests only. Never instantiate an engineering object.
$base = [Reflection.Assembly]::LoadFrom((Join-Path $SdkPath 'Siemens.Engineering.Base.dll'))
$classic = [Reflection.Assembly]::LoadFrom((Join-Path $SdkPath 'Siemens.Engineering.WinCC.dll'))
$unified = [Reflection.Assembly]::LoadFrom((Join-Path $SdkPath 'Siemens.Engineering.WinCCUnified.dll'))
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path $AssemblyPath))
$type = $assembly.GetType('TiaMcpServer.Siemens.HmiReading', $true)
$resultType = $assembly.GetType('TiaMcpServer.Siemens.HmiReadResult', $true)
$rowType = $type.GetNestedType('Row', [Reflection.BindingFlags]'NonPublic')
$listType = [Collections.Generic.List``1].MakeGenericType($rowType)
$checks = 0
function Assert-Check([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw "FAILED: $Message" }
    $script:checks++
}
function Expect-Rejected($Method, [object[]]$Arguments, [Type]$ExceptionType = [ArgumentException]) {
    $rejected = $false
    try { $null = $Method.Invoke($null, $Arguments) }
    catch { if ($ExceptionType.IsInstanceOfType($_.Exception.InnerException)) { $rejected = $true } else { throw } }
    Assert-Check $rejected "Reject invalid arguments for $($Method.Name)"
}
$validatePath = $type.GetMethod('ValidatePath')
$validatePage = $type.GetMethod('ValidatePage')
foreach ($path in @('Device/HMI_1', 'Group/Panel.1/HMI', 'screens/用户画面', ' name /[literal]*?\\name')) {
    $null = $validatePath.Invoke($null, @($path, $false)); $checks++
}
$null = $validatePath.Invoke($null, @('', $true)); $checks++
foreach ($path in @('', '/Main', 'Main/', 'A//B', '../B', 'A/./B', "A`nB")) {
    Expect-Rejected $validatePath @($path, $false)
}
foreach ($pair in @(@(0, 1), @(100000, 500), @(123, 100))) {
    $null = $validatePage.Invoke($null, [object[]]$pair); $checks++
}
foreach ($pair in @(@(-1, 1), @(100001, 1), @(0, 0), @(0, 501))) {
    Expect-Rejected $validatePage ([object[]]$pair) ([ArgumentOutOfRangeException])
}
# Verify named SDK members used by the implementation, without reading their values.
$contracts = @(
    @($classic, 'Siemens.Engineering.Hmi.HmiTarget', @('ScreenFolder', 'TagFolder', 'Connections')),
    @($classic, 'Siemens.Engineering.Hmi.Screen.ScreenSlidein', @('SlideinType')),
    @($unified, 'Siemens.Engineering.HmiUnified.HmiSoftware', @('Screens', 'ScreenGroups', 'TagTables', 'TagTableGroups', 'Connections')),
    @($unified, 'Siemens.Engineering.HmiUnified.UI.Screens.HmiScreen', @('ScreenItems', 'Dynamizations')),
    @($unified, 'Siemens.Engineering.HmiUnified.UI.Base.HmiScreenItemBase', @('Name', 'Dynamizations')),
    @($unified, 'Siemens.Engineering.HmiUnified.UI.Dynamization.TagDynamization', @('PropertyName', 'Tag', 'Address', 'PlcTag', 'DataType', 'ReadOnly', 'UseIndirectAddressing')),
    @($unified, 'Siemens.Engineering.HmiUnified.HmiTags.HmiTag', @('Address', 'Connection', 'PlcTag', 'PlcName', 'TagTableName', 'Members')),
    @($unified, 'Siemens.Engineering.HmiUnified.HmiConnections.HmiConnection', @('CommunicationDriver', 'Partner', 'Node', 'Station', 'InitialAddress', 'DriverProperties')),
    @($unified, 'Siemens.Engineering.HmiUnified.HmiConnections.DriverProperty', @('PropertyName', 'Value', 'Info'))
)
foreach ($contract in $contracts) {
    $sdkType = $contract[0].GetType($contract[1], $true)
    foreach ($property in $contract[2]) {
        Assert-Check ($null -ne $sdkType.GetProperty($property).GetGetMethod()) "$($contract[1]).$property public getter"
    }
}
$screenType = $classic.GetType('Siemens.Engineering.Hmi.Screen.Screen', $true)
Assert-Check ($null -eq $screenType.GetProperty('ScreenItems')) 'Classic does not advertise typed screen items'
Assert-Check ($null -eq $screenType.GetProperty('Dynamizations')) 'Classic does not advertise typed bindings'
# Test paging control flow on empty or skipped metadata rows; no SDK object is needed.
$page = $type.GetMethod('Page', [Reflection.BindingFlags]'Static,NonPublic')
$finish = $type.GetMethod('Finish', [Reflection.BindingFlags]'Static,NonPublic')
function New-Result([int]$Offset = 0, [int]$Limit = 2) {
    $result = [Activator]::CreateInstance($resultType)
    $result.offset = $Offset; $result.limit = $Limit
    return $result
}
function New-Rows([string[]]$Paths) {
    $rows = [Activator]::CreateInstance($listType)
    foreach ($path in $Paths) {
        $row = [Activator]::CreateInstance($rowType, $true)
        $row.Path = $path; $row.Kind = 'screen'
        $rows.Add($row)
    }
    return ,$rows
}
$empty = New-Rows @()
$result = New-Result
$null = $page.Invoke($null, @($empty, $result, '', 'screen'))
$null = $finish.Invoke($null, @($result))
Assert-Check ($result.Succeeded -and $result.complete -and !$result.hasMore) 'Empty available composition is a successful exhausted traversal'
Expect-Rejected $page @($empty, (New-Result), 'screens/missing', 'screen') ([InvalidOperationException])
$rows = New-Rows @('screens/A', 'screens/B')
$result = New-Result 2
$null = $page.Invoke($null, @($rows, $result, '', 'screen'))
Assert-Check ($result.records.Count -eq 0 -and !$result.hasMore) 'Offset skips without projecting SDK objects'
Expect-Rejected $page @($rows, (New-Result 2), 'screens/a', 'screen') ([InvalidOperationException])
$duplicates = New-Rows @('screens/A', 'screens/A')
Expect-Rejected $page @($duplicates, (New-Result 3), '', 'screen') ([InvalidOperationException])
$result = New-Result
$recordType = $assembly.GetType('TiaMcpServer.Siemens.HmiReadRecord', $true)
$record = [Activator]::CreateInstance($recordType)
$record.diagnostics.Add('Address: ReadFailed: offline simulated attribute failure')
$result.records.Add($record)
$null = $finish.Invoke($null, @($result))
Assert-Check (!$result.Succeeded -and !$result.complete -and $result.Meta['partial'].ToString() -eq 'true') 'Per-record failures propagate to overall partial result'
$result = New-Result
$result.hasMore = $true
$null = $finish.Invoke($null, @($result))
Assert-Check ($result.Succeeded -and !$result.complete) 'Successful page is not confused with complete traversal'
# Registration methods exist; names and defaults are stable and no path rewriting is used.
$tools = $assembly.GetType('TiaMcpServer.ModelContextProtocol.HmiReadTools', $true)
foreach ($name in @('GetHmiSoftware', 'GetHmiScreens', 'GetHmiTagTables', 'GetHmiConnections')) {
    Assert-Check ($null -ne $tools.GetMethod($name)) "Tool $name exists"
}
Write-Output "PASS: $checks offline HMI checks; no TIA connection, engineering object creation, export, import, compile, save or runtime operation."
