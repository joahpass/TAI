$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '..\installer\Common.ps1')
foreach ($name in @('Common.ps1','Install.ps1','Uninstall.ps1')) {
    $tokens=$null; $errors=$null
    $null=[System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot "..\installer\$name"),[ref]$tokens,[ref]$errors)
    if ($errors.Count) { throw ($errors | Out-String) }
}
$fixture=Join-Path $env:TEMP ('TAI-version-test-'+[guid]::NewGuid().ToString('N'))
try {
    $null=New-Item -ItemType Directory -Path (Join-Path $fixture 'PublicAPI\V19') -Force
    $dll=Join-Path $fixture 'PublicAPI\V19\Siemens.Engineering.dll'
    Add-Type -TypeDefinition 'using System.Reflection; [assembly: AssemblyVersion("19.0.0.0")] public class Tia19Fixture {}' -OutputAssembly $dll
    $result=Get-TiaInstallation -Version 19 -Location $fixture
    if ($result.Version -ne 19 -or $result.Location -ne [IO.Path]::GetFullPath($fixture)) { throw ('V19 fixture detection failed: '+($result | ConvertTo-Json)) }
    $failed=$false
    try { $null=Get-TiaInstallation -Version 21 -Location $fixture } catch { $failed=$true }
    if (!$failed) { throw 'V21 incorrectly accepted V19 layout.' }
    $failed=$false
    try { $null=Get-TiaInstallation -Location $fixture } catch { $failed=$true }
    if (!$failed) { throw 'Explicit location accepted without a version.' }
    Write-Output 'PASS: PowerShell syntax, V19 assembly identity/layout, wrong-version rejection, explicit-root validation. No installation or TIA attachment performed.'
} finally {
    if ($fixture -like "$env:TEMP\TAI-version-test-*") { Remove-Item -LiteralPath $fixture -Recurse -Force }
}
