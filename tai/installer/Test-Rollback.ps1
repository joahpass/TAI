[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$SetupExe)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Common.ps1')
$base=Join-Path $PSScriptRoot ('test-runs\rollback-'+[Guid]::NewGuid().ToString('N'))
$root=Join-Path $base 'installed'; $add=Join-Path $base 'UserAddIns'
$portal=Join-Path $base 'Portal V19'; $sdk=Join-Path $portal 'PublicAPI\V19'
$null=New-Item -ItemType Directory -Path $sdk -Force
$source=Join-Path $base 'Fixture.cs'
'[assembly:System.Reflection.AssemblyVersion("19.0.0.0")] public class Fixture {}' | Set-Content -LiteralPath $source
$csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:library "/out:$sdk\Siemens.Engineering.dll" $source
if($LASTEXITCODE -ne 0){throw 'Fixture compilation failed'}
function New-Payload([string]$Name,[string]$Version,[bool]$PassVerify) {
 $p=Join-Path $base $Name
 foreach($d in @('runtime','mcp\v19','mcp\v21','installer','app')) { $null=New-Item -ItemType Directory -Path (Join-Path $p $d) -Force }
 Copy-Item (Join-Path $PSScriptRoot 'Common.ps1') (Join-Path $p 'installer\Common.ps1')
 Copy-Item -LiteralPath (Get-Command node.exe).Source -Destination (Join-Path $p 'runtime\node.exe')
 foreach($v in @(19,21)) { 'backend' | Set-Content (Join-Path $p "mcp\v$v\TiaMcpServer.exe") }
 'desktop' | Set-Content (Join-Path $p 'TAI.Desktop.exe')
 $verdict=if($PassVerify){'PASS: fixture preflight'}else{'FAIL: simulated preflight rejection'}
 @("console.log('$verdict');","process.exit($(if($PassVerify){0}else{1}));") | Set-Content -LiteralPath (Join-Path $p 'installer\verify-install.mjs')
 $files=@(Get-ChildItem -LiteralPath $p -File -Recurse | ForEach-Object { @{path=$_.FullName.Substring($p.Length+1);sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash} })
 @{product='TAI.UserLocal.1';version=$Version;tiaVersions=@(19,21);files=$files} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $p 'manifest.json')
 return $p
}
function Install-Payload([string]$Payload) {
 $argumentLine='-NoProfile -ExecutionPolicy Bypass -File "'+(Join-Path $PSScriptRoot 'Install.ps1')+'" -PayloadRoot "'+$Payload+'" -NoRegistration -InstallRoot "'+$root+'" -AddInRoot "'+$add+'" -TiaVersion 19 -TiaLocation "'+$portal+'"'
 $stdout=Join-Path $base ('install-'+[Guid]::NewGuid().ToString('N')+'.out.txt')
 $stderr=Join-Path $base ('install-'+[Guid]::NewGuid().ToString('N')+'.err.txt')
 $p=Start-Process -FilePath 'powershell.exe' -ArgumentList $argumentLine -Wait -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr -WindowStyle Hidden
 $script:lastOutput=(Get-Content -Raw -LiteralPath $stdout -ErrorAction SilentlyContinue)+(Get-Content -Raw -LiteralPath $stderr -ErrorAction SilentlyContinue)
 return $p.ExitCode
}
$good=New-Payload 'payload-good' '0.0.1' $true
$bad=New-Payload 'payload-bad' '0.0.2' $false
if((Install-Payload $good) -ne 0){throw "Baseline install failed: $script:lastOutput"}
Copy-Item -LiteralPath (Join-Path $root 'TAI.Desktop.exe') -Destination (Join-Path $base 'baseline-desktop.bin')
$before=Get-Content -Raw -LiteralPath (Join-Path $root '.tai-install.json') | ConvertFrom-Json
$runtimeBefore=Get-Content -Raw -LiteralPath (Join-Path $root 'tia-runtime.json')
'preserve me' | Set-Content -LiteralPath (Join-Path $root 'user-note.txt')
$code=Install-Payload $bad
if($code -eq 0){throw 'Simulated preflight rejection was not reported'}
$after=Get-Content -Raw -LiteralPath (Join-Path $root '.tai-install.json') | ConvertFrom-Json
if($after.version -ne $before.version){throw 'Marker changed after failed preflight'}
if((Get-Content -Raw -LiteralPath (Join-Path $root 'tia-runtime.json')) -ne $runtimeBefore){throw 'Runtime selection changed after failed preflight'}
foreach($f in $before.files){ $path=Join-Path $root $f.path; if(!(Test-Path -LiteralPath $path)){throw "Old file removed by failed upgrade: $($f.path)"}; if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $f.sha256){throw "Old file changed by failed upgrade: $($f.path)"} }
if((Get-FileHash -LiteralPath (Join-Path $root 'TAI.Desktop.exe') -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath (Join-Path $base 'baseline-desktop.bin') -Algorithm SHA256).Hash){throw 'Previous version binary was replaced'}
if(!(Test-Path -LiteralPath (Join-Path $root 'user-note.txt'))){throw 'User file lost after failed upgrade'}
if((Install-Payload $good) -ne 0){throw 'Retry after rejection failed'}
Write-Output "PASS: rejected preflight left every previously installed file, marker, runtime selection and user file intact; retry succeeded. Evidence: $base"
