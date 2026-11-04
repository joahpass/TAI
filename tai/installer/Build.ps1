[CmdletBinding()]
param([string]$DesktopDir,[string]$AddInDir,[string]$NodePath,[string]$McpV19Dir,[string]$Version='0.11.0')
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$tai=Split-Path $PSScriptRoot -Parent
$repo=Split-Path $tai -Parent
function Find-Output([string]$Root,[string]$Name) {
 $candidates=@(Get-ChildItem -LiteralPath $Root -Filter $Name -File -Recurse -ErrorAction SilentlyContinue | Where-Object { $_.FullName -notmatch '[\\/]obj[\\/]' } | Sort-Object LastWriteTimeUtc -Descending)
 if (!$candidates.Count) { throw "Required output not ready: $Root\**\$Name. Build desktop/AddIn first." }
 return $candidates[0].DirectoryName
}
if (!$DesktopDir) { $DesktopDir=Find-Output (Join-Path $tai 'desktop') 'TAI.Desktop.exe' }
if (!$AddInDir) { $AddInDir=Find-Output (Join-Path $tai 'tia-addin') '*.addin' }
if (!$NodePath) { $NodePath=(Get-Command node.exe -ErrorAction Stop).Source }
$desktopExe=Join-Path $DesktopDir 'TAI.Desktop.exe'
$addins=@(Get-ChildItem -LiteralPath $AddInDir -Filter '*.addin' -File)
if ($addins.Count -ne 1) { throw 'AddInDir must contain exactly one published .addin.' }
$addin=$addins[0].FullName
$mcp=Join-Path $repo 'mcp-v21\bin\V21\Release\net48'
if (!$McpV19Dir) { $McpV19Dir=Join-Path $repo 'mcp-v21\bin\V19\Release\net48' }
foreach ($required in @($desktopExe,$addin,$NodePath,(Join-Path $tai 'server\index.mjs'),(Join-Path $tai 'frontend\dist\index.html'),(Join-Path $mcp 'TiaMcpServer.exe'),(Join-Path $McpV19Dir 'TiaMcpServer.exe'))) { if (!(Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required output not ready: $required" } }
$nodeVersion=& $NodePath --version
if ($LASTEXITCODE -ne 0 -or $nodeVersion -notmatch '^v(\d+)\.' -or [int]$Matches[1] -lt 22) { throw 'Node 22 or newer is required.' }
# Unique staging directory: no recursive cleanup or clobber of another build.
$stage=Join-Path $PSScriptRoot ('staging\'+[Guid]::NewGuid().ToString('N'))
$payload=Join-Path $stage 'payload'
$null=New-Item -ItemType Directory -Path $payload -Force
function Copy-Tree([string]$Source,[string]$Destination,[switch]$ExcludeSiemens) {
 foreach ($f in Get-ChildItem -LiteralPath $Source -File -Recurse) {
  if ($f.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse file rejected: $($f.FullName)" }
  if ($f.Extension -in @('.pdb','.xml')) { continue }
  if ($ExcludeSiemens -and $f.Name -like 'Siemens.Engineering*.dll') { continue }
  $rel=$f.FullName.Substring([IO.Path]::GetFullPath($Source).TrimEnd('\').Length+1)
  $dest=Join-Path $Destination $rel
  $null=New-Item -ItemType Directory -Path (Split-Path $dest -Parent) -Force
  Copy-Item -LiteralPath $f.FullName -Destination $dest
 }
}
Copy-Tree $DesktopDir $payload -ExcludeSiemens
if (!(Test-Path (Join-Path $payload 'Microsoft.Web.WebView2.Core.dll')) -or !(Test-Path (Join-Path $payload 'Microsoft.Web.WebView2.WinForms.dll')) -or !(Get-ChildItem -LiteralPath $payload -Filter WebView2Loader.dll -Recurse)) { throw 'Desktop output lacks WebView2 managed/native dependencies.' }
foreach ($d in @('runtime','app\server','app\frontend\dist','mcp','installer','licenses','addin')) { $null=New-Item -ItemType Directory -Path (Join-Path $payload $d) -Force }
Copy-Item -LiteralPath $NodePath -Destination (Join-Path $payload 'runtime\node.exe')
Get-ChildItem -LiteralPath (Join-Path $tai 'server') -Filter '*.mjs' -File | Where-Object { $_.Name -notlike '*.test.mjs' } | Copy-Item -Destination (Join-Path $payload 'app\server')
Copy-Item -LiteralPath (Join-Path $tai 'package.json') -Destination (Join-Path $payload 'app\package.json')
# The packaged app reports its version from this file; keep it identical to the build version.
$appPackage=Get-Content -Raw -LiteralPath (Join-Path $payload 'app\package.json') | ConvertFrom-Json
$appPackage.version=$Version
$appPackage | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $payload 'app\package.json') -Encoding UTF8
# Windows PowerShell writes a BOM; Node's JSON.parse rejects it, so rewrite without one.
[IO.File]::WriteAllText((Join-Path $payload 'app\package.json'),(Get-Content -Raw -LiteralPath (Join-Path $payload 'app\package.json')).TrimStart([char]0xFEFF),(New-Object System.Text.UTF8Encoding($false)))
Copy-Tree (Join-Path $tai 'frontend\dist') (Join-Path $payload 'app\frontend\dist')
Copy-Tree $mcp (Join-Path $payload 'mcp\v21') -ExcludeSiemens
Copy-Tree $McpV19Dir (Join-Path $payload 'mcp\v19') -ExcludeSiemens
$null=New-Item -ItemType Directory -Path (Join-Path $payload 'addin\v21') -Force
Copy-Item -LiteralPath $addin -Destination (Join-Path $payload 'addin\v21\TAI.addin')
foreach ($script in @('Common.ps1','Uninstall.ps1','verify-install.mjs','verify-connect.mjs')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $script) -Destination (Join-Path $payload 'installer') }
# Locate the user guide by pattern: a non-ASCII literal here breaks under codepage-936 PowerShell.
$guide=@(Get-ChildItem -LiteralPath $tai -Filter '*.md' -File | Where-Object { $_.Name.StartsWith('TAI-') -and $_.Name -notlike '*INTEGRATION*' -and $_.Name -notlike '*0.9.4*' })
if ($guide.Count -ne 1) { throw 'Cannot locate exactly one TAI user guide markdown next to installer\.' }
Copy-Item -LiteralPath $guide[0].FullName -Destination (Join-Path $payload 'USER-GUIDE.md')
foreach ($script in @('Common.ps1','Install.ps1')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $script) -Destination $stage }
$licenses=@()
$nodeDir=Split-Path $NodePath -Parent
foreach ($name in @('LICENSE','LICENSE.txt','LICENSE.md')) { $lp=Join-Path $nodeDir $name; if (Test-Path -LiteralPath $lp) { Copy-Item -LiteralPath $lp -Destination (Join-Path $payload ('licenses\Node-'+$name)); $licenses+=$lp } }
# Node bundles the complete license text, including its dependencies.
if (!($licenses.Count)) {
 # Version was already checked above.
 $nodeLicense=Join-Path $PSScriptRoot 'licenses\NODE-LICENSE.txt'
 if ((Test-Path -LiteralPath $nodeLicense) -and (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'licenses\NODE-VERSION.txt')) -and (Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'licenses\NODE-VERSION.txt')).Trim() -eq $nodeVersion) { Copy-Item -LiteralPath $nodeLicense -Destination (Join-Path $payload 'licenses\Node-LICENSE.txt'); $licenses+=$nodeLicense }
 else { throw "Node license unavailable next to $NodePath. Place its official version-matching LICENSE at installer\licenses\NODE-LICENSE.txt." }
}
# Include locally available package notices for bundled frontend/runtime packages.
$licenseRoots=@((Join-Path $tai 'frontend\node_modules'),$DesktopDir,$mcp,$McpV19Dir)
if ($env:USERPROFILE) { $licenseRoots+=Join-Path $env:USERPROFILE '.nuget\packages\microsoft.web.webview2' }
$assetsPath=Join-Path $repo 'mcp-v21\obj\project.assets.json'
if (Test-Path -LiteralPath $assetsPath) {
 $assets=Get-Content -Raw -LiteralPath $assetsPath | ConvertFrom-Json
 foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) { foreach ($library in $assets.libraries.PSObject.Properties) { if ($library.Value.type -eq 'package') { $licenseRoots+=Join-Path $folder $library.Name.ToLowerInvariant() } } }
}
$index=0
foreach ($root in $licenseRoots) { if (Test-Path -LiteralPath $root) { foreach ($f in Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object { $_.Name -match '^(LICENSE|COPYING|NOTICE)([.-].*)?$' }) { $index++; Copy-Item -LiteralPath $f.FullName -Destination (Join-Path $payload ("licenses\dependency-$index-$($f.Name)")); $licenses+=$f.FullName } } }
$licenses | Set-Content -LiteralPath (Join-Path $payload 'licenses\sources.txt') -Encoding UTF8
@{product='TAI.UserLocal.1';version=$Version;tiaVersions=@(19,21);entry='floating-ball';node=$nodeVersion;builtUtc=[DateTime]::UtcNow.ToString('o');webView2='Evergreen Runtime prerequisite; not downloaded';siemens='Not redistributed; each backend resolves the matching installed TIA Portal PublicAPI'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $payload 'version.json') -Encoding UTF8
$files=@(Get-ChildItem -LiteralPath $payload -Recurse -File | Where-Object { $_.FullName -notlike "$payload\addin\*" } | ForEach-Object { @{path=$_.FullName.Substring($payload.Length+1);sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash} })
@{product='TAI.UserLocal.1';version=$Version;tiaVersions=@(19,21);entry='floating-ball';files=$files;addInVersions=@(21);addInHashes=@{'21'=(Get-FileHash -LiteralPath $addin -Algorithm SHA256).Hash}} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $payload 'manifest.json') -Encoding UTF8
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=Join-Path $PSScriptRoot ('staging\payload-'+[Guid]::NewGuid().ToString('N')+'.zip')
[IO.Compression.ZipFile]::CreateFromDirectory($stage,$zip,[IO.Compression.CompressionLevel]::Optimal,$false)
$csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $csc)) { throw '.NET Framework 4.x x64 compiler is required.' }
$dist=Join-Path $tai 'dist'; $null=New-Item -ItemType Directory -Path $dist -Force
$exeName="TAI-Setup-V19-V21-$Version.exe"
$exe=Join-Path $dist $exeName
$assemblyInfo=Join-Path $PSScriptRoot ('staging\version-'+[Guid]::NewGuid().ToString('N')+'.cs')
$parsedVersion=[version]$Version
$fourPartVersion='{0}.{1}.{2}.0' -f $parsedVersion.Major,$parsedVersion.Minor,$parsedVersion.Build
[IO.File]::WriteAllText($assemblyInfo,('[assembly:System.Reflection.AssemblyVersion("'+$fourPartVersion+'")]'+[Environment]::NewLine+'[assembly:System.Reflection.AssemblyFileVersion("'+$fourPartVersion+'")]'),(New-Object System.Text.UTF8Encoding($false)))
& $csc /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll "/win32manifest:$(Join-Path $PSScriptRoot 'app.manifest.xml')" "/resource:$zip,TAI.Payload.zip" "/out:$exe" (Join-Path $PSScriptRoot 'Setup.cs') $assemblyInfo
if ($LASTEXITCODE -ne 0) { throw 'Setup compilation failed.' }
Copy-Item -LiteralPath (Join-Path $payload 'manifest.json') -Destination (Join-Path $dist ($exeName+'.manifest.json')) -Force
(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash+'  '+$exeName | Set-Content -LiteralPath (Join-Path $dist ($exeName+'.sha256')) -Encoding ASCII
Write-Output "Built $exe`nStaged payload: $payload"
