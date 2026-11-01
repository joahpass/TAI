[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$SetupExe)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$SetupExe=(Resolve-Path -LiteralPath $SetupExe).Path
$base=Join-Path $PSScriptRoot ('test-runs\v19-package-'+[Guid]::NewGuid().ToString('N'))
$root=Join-Path $base 'installed'
$add=Join-Path $base 'UserAddIns'
$portal=Join-Path $base 'Portal V19'
$sdk=Join-Path $portal 'PublicAPI\V19'
$null=New-Item -ItemType Directory -Path $sdk -Force
# Assembly identity fixture only. No Siemens code, no live TIA process.
$source=Join-Path $base 'Fixture.cs'
'[assembly:System.Reflection.AssemblyVersion("19.0.0.0")] public class Fixture {}' | Set-Content -LiteralPath $source
$csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:library "/out:$sdk\Siemens.Engineering.dll" $source
if ($LASTEXITCODE -ne 0) { throw 'Fixture compile failed.' }
function Install-Fixture {
 $arguments='-NoRegistration -InstallRoot "'+$root+'" -AddInRoot "'+$add+'" -TiaVersion 19 -TiaLocation "'+$portal+'"'
 $setup=Start-Process -FilePath $SetupExe -ArgumentList $arguments -Wait -PassThru
 if ($setup.ExitCode -ne 0) { throw "Setup returned $($setup.ExitCode)" }
}
Install-Fixture
$config=Get-Content -Raw -LiteralPath (Join-Path $root 'tia-runtime.json') | ConvertFrom-Json
if ($config.tiaVersion -ne 19 -or $config.tiaLocation -ne $portal) { throw 'Runtime selection mismatch.' }
if (Test-Path (Join-Path $add 'TAI.addin')) { throw 'V21 AddIn installed into V19.' }
foreach ($v in @(19,21)) { if (!(Test-Path (Join-Path $root "mcp\v$v\TiaMcpServer.exe"))) { throw "Missing backend V$v" } }
$marker=Get-Content -Raw -LiteralPath (Join-Path $root '.tai-install.json') | ConvertFrom-Json
foreach ($entry in $marker.files) {
 if ((Get-FileHash -LiteralPath (Join-Path $root $entry.path) -Algorithm SHA256).Hash -ne $entry.sha256) { throw "Hash mismatch: $($entry.path)" }
}
'keep me' | Set-Content -LiteralPath (Join-Path $root 'user-note.txt')
Install-Fixture
& (Join-Path $root 'runtime\node.exe') (Join-Path $PSScriptRoot 'smoke-installed.mjs') $root
if ($LASTEXITCODE -ne 0) { throw 'Installed runtime/MCP smoke test failed.' }
$uninstall=Join-Path $root 'installer\Uninstall.ps1'
& $uninstall -InstallRoot $root -NoRegistration
if (!(Test-Path (Join-Path $root 'user-note.txt'))) { throw 'User file was removed.' }
if (Test-Path (Join-Path $root '.tai-install.json')) { throw 'Marker remains after uninstall.' }
Write-Output "PASS: actual EXE extraction, V19 selection, dual backends, no mismatched AddIn, all installed hashes, repeat install, uninstall, user-file preservation. Evidence: $base"
