param([string]$SdkPath = 'E:/TIA Portal/Portal V21/PublicAPI/V21/net48')
$ErrorActionPreference = 'Stop'
$base = [Reflection.Assembly]::LoadFrom((Join-Path $SdkPath 'Siemens.Engineering.Base.dll'))
foreach ($name in @('Siemens.Engineering.HW.Features.SoftwareContainer', 'Siemens.Engineering.HW.Software', 'Siemens.Engineering.ProjectBase', 'Siemens.Engineering.HW.DeviceSystemGroup', 'Siemens.Engineering.HW.DeviceItem')) {
    $type = $base.GetType($name, $true)
    "$name"
    $type.GetProperties() | Where-Object Name -in @('Software','Name','Devices','DeviceItems','DeviceGroups','UngroupedDevicesGroup','Parent') | ForEach-Object { "$($_.Name): $($_.PropertyType.FullName)" }
}
