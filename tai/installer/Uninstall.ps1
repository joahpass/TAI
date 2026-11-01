[CmdletBinding()]
param([string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'Programs\TAI'),[switch]$NoRegistration)
. (Join-Path $PSScriptRoot 'Common.ps1')
$InstallRoot = Get-SafeRoot $InstallRoot
$m = Read-Ownership $InstallRoot
Assert-Stopped $InstallRoot
if ([bool]$m.registered -eq [bool]$NoRegistration) { throw 'Pass -NoRegistration only for an installation created with -NoRegistration.' }
$addIn = if ($m.addInRoot) { Join-Path (Get-SafeRoot $m.addInRoot) 'TAI.addin' } else { $null }
if ($addIn -and (Test-Path -LiteralPath $addIn)) {
 if (!$m.addInHash) { throw "Refusing to delete unowned AddIn: $addIn" }
 Assert-Hash $addIn $m.addInHash
}
foreach ($f in $m.files) { $p=Get-OwnedPath $InstallRoot $f.path; if (Test-Path -LiteralPath $p) { Assert-Hash $p $f.sha256 } }
$key='HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\TAI'
$runKey='HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runName='TAIIntegration'
$ownedRun=$m.PSObject.Properties['runCommand'] -and $m.runCommand
if (!$NoRegistration) {
 if ($ownedRun) {
  $runValues=Get-ItemProperty -Path $runKey -ErrorAction SilentlyContinue
  if ($runValues -and $runValues.PSObject.Properties[$runName] -and $runValues.$runName -ne $m.runCommand) { throw 'TAI startup registration has been modified; refusing removal.' }
 }
 if ($m.shortcut -and $m.shortcut -ne (Join-Path ([Environment]::GetFolderPath('Programs')) 'TAI.lnk')) { throw 'Unexpected shortcut path.' }
 if ($m.shortcut -and (Test-Path -LiteralPath $m.shortcut)) { Assert-Hash $m.shortcut $m.shortcutHash }
 if ((Test-Path $key) -and (Get-ItemProperty $key).InstallLocation -ne $InstallRoot) { throw 'Uninstall registration belongs to another root.' }
}
# All ownership and process checks happen before deletion. Never remove config/data.
if ($addIn -and $m.addInHash -and (Test-Path -LiteralPath $addIn)) { Remove-Item -LiteralPath $addIn -Force }
if (!$NoRegistration) {
 if ($m.shortcut -and (Test-Path -LiteralPath $m.shortcut)) { Remove-Item -LiteralPath $m.shortcut -Force }
 if ($ownedRun) { Remove-ItemProperty -Path $runKey -Name $runName -ErrorAction SilentlyContinue }
 if (Test-Path $key) { Remove-Item -LiteralPath $key }
}
Remove-OwnedFiles $InstallRoot $m.files
# Installer-generated report, not user data; it is produced after the marker is written.
$report = Join-Path $InstallRoot 'install-verification.json'
if (Test-Path -LiteralPath $report -PathType Leaf) { Remove-Item -LiteralPath $report -Force }
Remove-Item -LiteralPath (Join-Path $InstallRoot '.tai-install.json') -Force
if (@(Get-ChildItem -LiteralPath $InstallRoot -Force).Count -eq 0) { [IO.Directory]::Delete($InstallRoot) }
Write-Output 'TAI uninstalled. User config/data in %LOCALAPPDATA%\TAI were preserved. Restart TIA Portal to unload the AddIn.'
