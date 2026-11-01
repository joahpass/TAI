[CmdletBinding()]
param([string]$PayloadRoot)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$base=Join-Path $PSScriptRoot ('test-runs\'+[Guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $base -Force
$root=Join-Path $base 'installed'; $add=Join-Path $base 'UserAddIns'
if (!$PayloadRoot) {
 $PayloadRoot=Join-Path $base 'payload'
 $null=New-Item -ItemType Directory -Path (Join-Path $PayloadRoot 'addin') -Force
 $null=New-Item -ItemType Directory -Path (Join-Path $PayloadRoot 'installer') -Force
 'fixture' | Set-Content (Join-Path $PayloadRoot 'TAI.Desktop.exe')
 'published-addin-fixture' | Set-Content (Join-Path $PayloadRoot 'addin\TAI.addin')
 foreach ($name in @('Common.ps1','Uninstall.ps1')) { Copy-Item (Join-Path $PSScriptRoot $name) (Join-Path $PayloadRoot 'installer') }
 $files=@(Get-ChildItem $PayloadRoot -File -Recurse | Where-Object { $_.FullName -notlike "$PayloadRoot\addin\*" } | ForEach-Object { @{path=$_.FullName.Substring($PayloadRoot.Length+1);sha256=(Get-FileHash $_.FullName).Hash} })
 @{product='TAI.UserLocal.1';version='test';files=$files;addInHash=(Get-FileHash (Join-Path $PayloadRoot 'addin\TAI.addin')).Hash} | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $PayloadRoot 'manifest.json')
}
$install=Join-Path $PSScriptRoot 'Install.ps1'
$uninstall=Join-Path $PSScriptRoot 'Uninstall.ps1'
function Expect-Refusal([scriptblock]$Action,[string]$Pattern) {
 $failed=$false
 try { & $Action } catch { if ($_.Exception.Message -notmatch $Pattern) { throw }; $failed=$true }
 if (!$failed) { throw "Expected refusal: $Pattern" }
}
& $install -PayloadRoot $PayloadRoot -InstallRoot $root -AddInRoot $add -NoRegistration
if (!(Test-Path (Join-Path $root '.tai-install.json'))) { throw 'Install marker missing.' }
'preserve this' | Set-Content (Join-Path $root 'user-file.txt')
& $install -PayloadRoot $PayloadRoot -InstallRoot $root -AddInRoot $add -NoRegistration
$exe=Join-Path $root 'TAI.Desktop.exe'
$original=[IO.File]::ReadAllBytes($exe)
[IO.File]::WriteAllText($exe,'modified')
Expect-Refusal { & $uninstall -InstallRoot $root -NoRegistration } 'modified'
[IO.File]::WriteAllBytes($exe,$original)
# Launch an installed Windows executable to verify refusal without killing any application.
$probe=Join-Path $root 'probe.exe'
Copy-Item (Join-Path $env:WINDIR 'System32\ping.exe') $probe
$p=Start-Process -FilePath $probe -ArgumentList '-n 3 127.0.0.1' -WindowStyle Hidden -PassThru
try { Expect-Refusal { & $uninstall -InstallRoot $root -NoRegistration } 'Running PIDs' } finally { $p.WaitForExit(10000) | Out-Null }
Remove-Item -LiteralPath $probe
& $uninstall -InstallRoot $root -NoRegistration
if (!(Test-Path (Join-Path $root 'user-file.txt')) -or (Test-Path (Join-Path $add 'TAI.addin'))) { throw 'Uninstall preservation check failed.' }
Expect-Refusal { & $install -PayloadRoot $PayloadRoot -InstallRoot $root -AddInRoot $add -NoRegistration } 'unmarked'
Expect-Refusal { & $install -PayloadRoot $PayloadRoot -InstallRoot $env:LOCALAPPDATA -AddInRoot $add -NoRegistration } 'Protected root'
Write-Output "PASS: install, update, modified-file refusal, running-process refusal, uninstall, unknown-file preservation, unmarked-root refusal, protected-root refusal. Test evidence: $base"
