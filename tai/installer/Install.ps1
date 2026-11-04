[CmdletBinding()]
param(
 [string]$PayloadRoot,
 [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'Programs\TAI'),
 [string]$AddInRoot,
 [ValidateSet(0,19,21)][int]$TiaVersion = 0,
 [string]$TiaLocation,
 [switch]$NoRegistration
)
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
. (Join-Path $PSScriptRoot 'Common.ps1')
if (!$PayloadRoot) { $PayloadRoot = Join-Path $PSScriptRoot 'payload' }
$InstallRoot = Get-SafeRoot $InstallRoot
$explicitAddInRoot = ![string]::IsNullOrWhiteSpace($AddInRoot)
Write-Output 'TAI_PROGRESS|12|正在检测博途版本与目录'
$tia = Get-TiaInstallation -Version $TiaVersion -Location $TiaLocation
$TiaVersion = $tia.Version
$TiaLocation = $tia.Location
$manifest = Get-Content -Raw -LiteralPath (Join-Path $PayloadRoot 'manifest.json') | ConvertFrom-Json
if ($manifest.product -ne $script:ProductId) { throw 'Wrong payload product.' }
if (!$manifest.PSObject.Properties['tiaVersions'] -or $TiaVersion -notin $manifest.tiaVersions) { throw "Payload does not contain a TIA V$TiaVersion MCP build." }
if (!(Test-Path -LiteralPath (Join-Path $PayloadRoot "mcp\v$TiaVersion\TiaMcpServer.exe") -PathType Leaf)) { throw "Missing V$TiaVersion MCP executable." }
$installAddIn = $manifest.PSObject.Properties['addInVersions'] -and $TiaVersion -in $manifest.addInVersions
if (!$AddInRoot -and $installAddIn) { $AddInRoot = Join-Path $env:APPDATA "Siemens\Automation\Portal V$TiaVersion\UserAddIns" }
# Versions without a published AddIn must stay installable even when %APPDATA% is redirected or absent.
$AddInRoot = if ($AddInRoot) { Get-SafeRoot $AddInRoot } else { $null }
if ($NoRegistration -and ($InstallRoot -eq (Join-Path $env:LOCALAPPDATA 'Programs\TAI') -or ($installAddIn -and !$explicitAddInRoot))) { throw '-NoRegistration requires explicit isolated InstallRoot AND AddInRoot.' }
$addInSource = if ($installAddIn) { Join-Path $PayloadRoot "addin\v$TiaVersion\TAI.addin" } else { $null }
$addInHash = if ($installAddIn) { [string]$manifest.addInHashes."$TiaVersion" } else { $null }
$old = $null
if (Test-Path -LiteralPath $InstallRoot) { $old = Read-Ownership $InstallRoot }
$oldAddIn = if ($old -and $old.addInRoot) { Join-Path (Get-SafeRoot $old.addInRoot) 'TAI.addin' } else { $null }
if ($oldAddIn -and $old.addInHash -and (Test-Path -LiteralPath $oldAddIn)) { Assert-Hash $oldAddIn $old.addInHash }
$addIn = if ($AddInRoot) { Join-Path $AddInRoot 'TAI.addin' } else { $null }
if ($installAddIn -and (Test-Path -LiteralPath $addIn)) {
 if (!$old -or $oldAddIn -ne $addIn -or !$old.addInHash) { throw "Refusing to overwrite unowned AddIn: $addIn" }
 Assert-Hash $addIn $old.addInHash
}
Write-Output 'TAI_PROGRESS|22|正在校验安装文件'
$seen = @{}
$incoming = @{}
foreach ($f in $manifest.files) {
 if ($f.path -in @('.tai-install.json','tia-runtime.json')) { throw 'Payload cannot own generated installation metadata.' }
 if ($seen.ContainsKey($f.path)) { throw 'Duplicate payload path.' }; $seen[$f.path] = $true
 $incoming[$f.path] = $f.sha256
 $src = Get-OwnedPath ([IO.Path]::GetFullPath($PayloadRoot)) $f.path
 Assert-Hash $src $f.sha256
 $dest = Get-OwnedPath $InstallRoot $f.path
 if ((Test-Path -LiteralPath $dest) -and (!$old -or !(@($old.files.path) -contains $f.path))) { throw "Refusing to overwrite unowned file: $dest" }
}
if ($installAddIn) { Assert-Hash $addInSource $addInHash }
$runtimeFile = Join-Path $InstallRoot 'tia-runtime.json'
if ((Test-Path -LiteralPath $runtimeFile) -and (!$old -or 'tia-runtime.json' -notin @($old.files.path))) { throw 'Refusing to overwrite unowned tia-runtime.json.' }
if ($old) {
 # Accept a file that still matches the recorded hash, or one already replaced by this exact
 # payload (interrupted update): both are bytes this installer shipped, so a retry is safe.
 foreach ($f in $old.files) {
  $p = Get-OwnedPath $InstallRoot $f.path
  if (!(Test-Path -LiteralPath $p)) { continue }
  $h = (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash
  if ($h -ne $f.sha256 -and (!$incoming.ContainsKey($f.path) -or $h -ne $incoming[$f.path])) { throw "Managed file modified; refusing overwrite/delete: $p" }
 }
 if ([bool]$old.registered -ne (!$NoRegistration)) { throw 'Update registration mode must match the existing installation.' }
}
# Registration conflicts are also checked before installed files change.
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'TAI.lnk'
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\TAI'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runName = 'TAIIntegration'
$runCommand = '"'+(Join-Path $InstallRoot 'TAI.Desktop.exe')+'" --watch'
if (!$NoRegistration) {
 $runValues = Get-ItemProperty -Path $runKey -ErrorAction SilentlyContinue
 $existingRun = if ($runValues -and $runValues.PSObject.Properties[$runName]) { $runValues.$runName } else { $null }
 if ($existingRun -and (!$old -or !$old.PSObject.Properties['runCommand'] -or $existingRun -ne $old.runCommand)) { throw 'Refusing to overwrite unowned TAI startup registration.' }
 if ((Test-Path -LiteralPath $shortcut) -and (!$old -or $old.shortcut -ne $shortcut)) { throw "Refusing unowned shortcut: $shortcut" }
 if ($old -and $old.shortcutHash -and (Test-Path -LiteralPath $shortcut)) { Assert-Hash $shortcut $old.shortcutHash }
 if (Test-Path $key) { $existing = Get-ItemProperty $key; if (!$old -or $existing.InstallLocation -ne $InstallRoot) { throw 'Uninstall registration belongs to another root.' } }
}
# Preflight completes before any installed files are changed.
Write-Output 'TAI_PROGRESS|32|正在检查运行组件'
Write-Output "Selected TIA V$TiaVersion at $TiaLocation. Checking bundled runtime before installation..."
& (Join-Path $PayloadRoot 'runtime\node.exe') (Join-Path $PayloadRoot 'installer\verify-install.mjs') "$TiaVersion" $TiaLocation
if ($LASTEXITCODE -ne 0) { throw 'Package preflight failed before installed files were changed. Check Openness user group and the log above.' }
if ($old) {
 Write-Output 'TAI_PROGRESS|42|正在关闭旧版 TAI，保留博途与用户数据'
 Stop-TaiProcesses $InstallRoot
} else {
 Write-Output 'TAI_PROGRESS|42|正在准备安装目录'
}
Assert-Stopped $InstallRoot
if ($old) {
 # Every old file already passed the hash check above, so remove without re-asserting a stale hash.
 foreach ($f in $old.files) { $p = Get-OwnedPath $InstallRoot $f.path; if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Force } }
 $staleDirs = @($old.files | ForEach-Object { $d = [IO.Path]::GetDirectoryName((Get-OwnedPath $InstallRoot $_.path)); while ($d -and $d -ne $InstallRoot) { $d; $d = [IO.Path]::GetDirectoryName($d) } } | Sort-Object -Unique | Sort-Object Length -Descending)
 foreach ($d in $staleDirs) { if ((Test-Path -LiteralPath $d) -and @(Get-ChildItem -LiteralPath $d -Force).Count -eq 0) { [IO.Directory]::Delete($d) } }
}
Write-Output 'TAI_PROGRESS|48|正在安装 TAI 文件'
$null = New-Item -ItemType Directory -Path $InstallRoot -Force
foreach ($f in $manifest.files) {
 $dest = Get-OwnedPath $InstallRoot $f.path
 $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($dest)) -Force
 Copy-Item -LiteralPath (Join-Path $PayloadRoot $f.path) -Destination $dest -Force
}
Write-Output 'TAI_PROGRESS|68|正在配置博途集成'
# Prove the copy before the marker claims it: a truncated or failed copy never becomes "installed".
foreach ($f in $manifest.files) { Assert-Hash (Join-Path $InstallRoot $f.path) $f.sha256 }
if ($installAddIn) {
 $null = New-Item -ItemType Directory -Path $AddInRoot -Force
 Copy-Item -LiteralPath $addInSource -Destination $addIn -Force
 Assert-Hash $addIn $addInHash
}
if ($oldAddIn -and $old.addInHash -and (!$installAddIn -or $oldAddIn -ne $addIn) -and (Test-Path -LiteralPath $oldAddIn)) {
 Assert-Hash $oldAddIn $old.addInHash
 Remove-Item -LiteralPath $oldAddIn -Force
}
@{tiaVersion=$TiaVersion;tiaLocation=$TiaLocation} | ConvertTo-Json | Set-Content -LiteralPath $runtimeFile -Encoding UTF8
$runtimeEntry = @{path='tia-runtime.json';sha256=(Get-FileHash -LiteralPath $runtimeFile -Algorithm SHA256).Hash}
$record = [ordered]@{product=$script:ProductId;version=$manifest.version;root=$InstallRoot;addInRoot=$AddInRoot;addInHash=$addInHash;tiaVersion=$TiaVersion;tiaLocation=$TiaLocation;registered=(!$NoRegistration);files=@($manifest.files)+@($runtimeEntry);shortcut=$null;shortcutHash=$null}
$marker = Join-Path $InstallRoot '.tai-install.json'
$record | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $marker -Encoding UTF8
if (!$NoRegistration) {
 $shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'TAI.lnk'
 if ((Test-Path -LiteralPath $shortcut) -and (!$old -or $old.shortcut -ne $shortcut)) { throw "Refusing unowned shortcut: $shortcut" }
 if ($old -and $old.shortcutHash -and (Test-Path -LiteralPath $shortcut)) { Assert-Hash $shortcut $old.shortcutHash }
 $shell = New-Object -ComObject WScript.Shell
 $link = $shell.CreateShortcut($shortcut); $link.TargetPath = Join-Path $InstallRoot 'TAI.Desktop.exe'; $link.Arguments = '--assistant'; $link.WorkingDirectory = $InstallRoot; $link.Description = 'TAI Engineering Workspace'; $link.Save()
 $record.shortcut=$shortcut; $record.shortcutHash=(Get-FileHash -LiteralPath $shortcut -Algorithm SHA256).Hash
 $record | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $marker -Encoding UTF8
 $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\TAI'
 if (Test-Path $key) { $existing = Get-ItemProperty $key; if ($existing.InstallLocation -ne $InstallRoot) { throw 'Uninstall registration belongs to another root.' } }
 $null = New-Item -Path $key -Force
 $command = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "'+(Join-Path $InstallRoot 'installer\Uninstall.ps1')+'" -InstallRoot "'+$InstallRoot+'"'
 foreach ($entry in @{DisplayName='TAI';DisplayVersion=$manifest.version;Publisher='TAI';InstallLocation=$InstallRoot;DisplayIcon=(Join-Path $InstallRoot 'TAI.Desktop.exe');UninstallString=$command}.GetEnumerator()) { $null=New-ItemProperty -Path $key -Name $entry.Key -Value $entry.Value -PropertyType String -Force }
 $record['runCommand']=$runCommand
 $record | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $marker -Encoding UTF8
 $null=New-Item -Path $runKey -Force
 $null=New-ItemProperty -Path $runKey -Name $runName -Value $runCommand -PropertyType String -Force
 $null=New-ItemProperty -Path $key -Name NoModify -Value 1 -PropertyType DWord -Force
 $null=New-ItemProperty -Path $key -Name NoRepair -Value 1 -PropertyType DWord -Force
}
Write-Output 'TAI_PROGRESS|82|正在验证已安装组件'
Write-Output 'Checking the installed Node runtime, frontend assets and MCP tool loading...'
& (Join-Path $InstallRoot 'runtime\node.exe') (Join-Path $InstallRoot 'installer\verify-install.mjs')
if ($LASTEXITCODE -ne 0) { throw 'Installed package load verification failed. See log above; TIA integration was not started.' }
if (!$NoRegistration) {
 $watcher=Start-Process -FilePath (Join-Path $InstallRoot 'TAI.Desktop.exe') -ArgumentList '--watch' -WindowStyle Hidden -PassThru
 Start-Sleep -Milliseconds 2500
 $watcher.Refresh()
 if ($watcher.HasExited) { Write-Output "Installed TAI $($manifest.version) at $InstallRoot, but the integration watcher exited immediately (code $($watcher.ExitCode)). Another TAI watcher may already hold the single-instance lock; start it from the tray or with --watch. WebView2 Runtime is required." }
 else { Write-Output "Installed TAI $($manifest.version) at $InstallRoot. Integration watcher running (PID $($watcher.Id)); TIA integration also starts at user logon. WebView2 Runtime is required." }
} else {
 Write-Output "Installed TAI $($manifest.version) at $InstallRoot without registration or process startup."
}
