[CmdletBinding()]
param([string]$SetupExe)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (!$SetupExe) { $SetupExe = Join-Path (Split-Path $PSScriptRoot -Parent) 'dist\TAI-Setup-V19-V21-0.11.0.exe' }
# Offline lifecycle test: never execute Setup.exe, Desktop, TIA or MCP; never register.
$SetupExe = (Resolve-Path -LiteralPath $SetupExe).Path
$base = Join-Path $PSScriptRoot ('test-runs\offline-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $base
$results = New-Object System.Collections.Generic.List[object]
$originalExeHash = (Get-FileHash -LiteralPath $SetupExe -Algorithm SHA256).Hash
Start-Transcript -Path (Join-Path $base 'test.log') | Out-Null
function Check([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function Read-Json([string]$Path) { Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json }
function Save-Json($Value, [string]$Name) { $Value | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $base $Name) -Encoding UTF8 }
function Hash-Equals([string]$Path, [string]$Expected) {
 Check (Test-Path -LiteralPath $Path -PathType Leaf) "Missing file: $Path"
 Check ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -eq $Expected) "Hash mismatch: $Path"
}
$success = $false
$upgradeDummy = $null
try {
 $assembly = [Reflection.Assembly]::LoadFile($SetupExe)
 $resourceNames = @($assembly.GetManifestResourceNames())
 Check ($resourceNames -contains 'TAI.Payload.zip') 'Embedded payload resource missing'
 $stream = $assembly.GetManifestResourceStream('TAI.Payload.zip')
 $zip = Join-Path $base 'TAI.Payload.zip'
 $out = [IO.File]::Create($zip)
 try { $stream.CopyTo($out) } finally { $out.Dispose(); $stream.Dispose() }
 Add-Type -AssemblyName System.IO.Compression.FileSystem
 $extracted = Join-Path $base 'extracted'
 [IO.Compression.ZipFile]::ExtractToDirectory($zip, $extracted)
 $payload = Join-Path $extracted 'payload'
 $manifestPath = Join-Path $payload 'manifest.json'
 $manifest = Read-Json $manifestPath
 Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $base 'manifest.original.json')
 Check ($manifest.product -eq 'TAI.UserLocal.1' -and $manifest.version -eq '0.11.0') 'Wrong manifest product/version'
 Check ((@($manifest.tiaVersions | Sort-Object) -join ',') -eq '19,21') 'Wrong backend version matrix'
 Check ((@($manifest.addInVersions) -join ',') -eq '21') 'Expected published AddIn only for V21'
 . (Join-Path $extracted 'Common.ps1')
 $originalHashes = @()
 $seen = @{}
 foreach ($f in $manifest.files) {
  $path = Get-OwnedPath $payload $f.path
  Check (!$seen.ContainsKey($f.path)) "Duplicate manifest path: $($f.path)"
  $seen[$f.path] = $true
  Hash-Equals $path $f.sha256
  $originalHashes += [pscustomobject]@{path=$f.path;sha256=$f.sha256;passed=$true}
 }
 foreach ($v in $manifest.addInVersions) {
  $rel = "addin\v$v\TAI.addin"
  Hash-Equals (Join-Path $payload $rel) $manifest.addInHashes."$v"
  $originalHashes += [pscustomobject]@{path=$rel;sha256=$manifest.addInHashes."$v";passed=$true}
 }
 $unlisted = @(Get-ChildItem -LiteralPath $payload -Recurse -File | Where-Object {
  $rel = $_.FullName.Substring($payload.Length+1)
  $rel -ne 'manifest.json' -and !$seen.ContainsKey($rel) -and $rel -notmatch '^addin[\\/]v21[\\/]TAI\.addin$'
 })
 Check ($unlisted.Count -eq 0) 'Payload contains unlisted files'
 Save-Json $originalHashes 'original-hashes.json'
 $package = Read-Json (Join-Path $payload 'app\package.json')
 Check ($package.version -eq '0.11.0') 'Wrong packaged app version'
 Check ($package.repository.url -eq 'https://github.com/joahpass/TAI.git') 'Wrong packaged repository'
 Check (Test-Path -LiteralPath (Join-Path $payload 'app\server\updates.mjs') -PathType Leaf) 'Updater missing'
 $siemens = @(Get-ChildItem -LiteralPath $payload -Recurse -File -Filter 'Siemens*.dll')
 # Engineering SDK assemblies must resolve from the local TIA installation.
 # Collaboration.Net runtime dependencies are part of the MCP backend payload.
 $engineering = @($siemens | Where-Object { $_.Name -like 'Siemens.Engineering*.dll' })
 Save-Json @($siemens | ForEach-Object { [pscustomobject]@{path=$_.FullName.Substring($payload.Length+1);sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash} }) 'siemens-dlls.json'
 $results.Add([pscustomobject]@{test='no-engineering-sdk-dlls';passed=($engineering.Count -eq 0);siemensDllCount=$siemens.Count;engineeringDllCount=$engineering.Count})
 $results.Add([pscustomobject]@{test='original-payload';passed=$true;hashCount=$originalHashes.Count;packageVersion=$package.version;repository=$package.repository.url;updater=$true;siemensDllCount=$siemens.Count})
 # Only mutate the extracted test copy after every original hash has passed.
 $verifier = Join-Path $payload 'installer\verify-install.mjs'
 $fixture = @'
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve, join } from 'node:path';
const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const installed = process.argv.length === 2;
const runtime = installed ? JSON.parse(readFileSync(join(root, 'tia-runtime.json'), 'utf8').replace(/^\uFEFF/, '')) : { tiaVersion: Number(process.argv[2]), tiaLocation: process.argv[3] };
if (![19, 21].includes(runtime.tiaVersion) || !existsSync(runtime.tiaLocation)) throw new Error('Invalid fixture selection');
const report = { ok: true, phase: installed ? 'installed' : 'preflight', tiaVersion: runtime.tiaVersion, tiaLocation: runtime.tiaLocation, offlineFixture: true, realMcpStarted: false };
if (installed) writeFileSync(join(root, 'install-verification.json'), JSON.stringify(report, null, 2));
console.log(JSON.stringify(report));
'@
 [IO.File]::WriteAllText($verifier, $fixture, (New-Object System.Text.UTF8Encoding($false)))
 $entries = @($manifest.files | Where-Object { $_.path.Replace('/', '\') -eq 'installer\verify-install.mjs' })
 Check ($entries.Count -eq 1) 'Expected exactly one verifier entry'
 $entries[0].sha256 = (Get-FileHash -LiteralPath $verifier -Algorithm SHA256).Hash
 $manifest | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
 Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $base 'manifest.fixture.json')
 $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
 Check (Test-Path -LiteralPath $csc -PathType Leaf) 'Framework csc missing'
 $portals = @{}
 foreach ($v in @(19,21)) {
  $portal = Join-Path $base "Portal V$v"
  $sdkRelative = if ($v -eq 19) { 'PublicAPI\V19\Siemens.Engineering.dll' } else { 'PublicAPI\V21\net48\Siemens.Engineering.Base.dll' }
  $dll = Join-Path $portal $sdkRelative
  $null = New-Item -ItemType Directory -Path (Split-Path $dll -Parent) -Force
  $source = Join-Path $base "EmptyAssemblyV$v.cs"
  [IO.File]::WriteAllText($source, ('[assembly:System.Reflection.AssemblyVersion("'+$v+'.0.0.0")]'), (New-Object System.Text.UTF8Encoding($false)))
  & $csc /nologo /target:library "/out:$dll" $source
  Check ($LASTEXITCODE -eq 0) "V$v fixture compilation failed"
  Check ([Reflection.AssemblyName]::GetAssemblyName($dll).Version.Major -eq $v) 'Wrong fixture assembly identity'
  $portals[$v] = $portal
 }
 $installRoot = Join-Path $base 'installed'
 $userNote = Join-Path $installRoot 'user-note.txt'
 $userNested = Join-Path $installRoot 'user-data\settings.json'
 $userAddIn = Join-Path $base 'UserAddIns-V21\user-note.txt'
 $userHashes = @{}
 foreach ($scenario in @(@{label='fresh-v19';v=19},@{label='upgrade-v19-to-v21';v=21},@{label='switch-v21-to-v19';v=19})) {
  $v = $scenario.v
  $add = Join-Path $base "UserAddIns-V$v"
  if ($scenario.label -eq 'upgrade-v19-to-v21') {
   $idleScript = Join-Path $base 'idle-upgrade-fixture.cjs'
   [IO.File]::WriteAllText($idleScript, 'setInterval(() => {}, 1000);', (New-Object System.Text.UTF8Encoding($false)))
   $upgradeDummy = Start-Process -FilePath (Join-Path $installRoot 'runtime\node.exe') -ArgumentList ('"'+$idleScript+'"') -WindowStyle Hidden -PassThru
   Start-Sleep -Milliseconds 300
   Check (!$upgradeDummy.HasExited) 'Isolated upgrade dummy did not start'
   $corruptPath = Join-Path $payload 'app\server\index.mjs'
   $originalBytes = [IO.File]::ReadAllBytes($corruptPath)
   $blocked = $false
   try {
    [IO.File]::AppendAllText($corruptPath, '// corrupted offline fixture')
    try { & (Join-Path $extracted 'Install.ps1') -PayloadRoot $payload -NoRegistration -InstallRoot $installRoot -AddInRoot $add -TiaVersion $v -TiaLocation $portals[$v] }
    catch { $blocked = $_.Exception.Message -like 'Managed file missing or modified*' }
    Check ($blocked -and !$upgradeDummy.HasExited) 'Invalid payload did not preserve the running isolated old backend'
   } finally { [IO.File]::WriteAllBytes($corruptPath, $originalBytes) }
   $results.Add([pscustomobject]@{test='invalid-payload-preserves-running-backend';passed=$true})
  }
  & (Join-Path $extracted 'Install.ps1') -PayloadRoot $payload -NoRegistration -InstallRoot $installRoot -AddInRoot $add -TiaVersion $v -TiaLocation $portals[$v]
  if ($scenario.label -eq 'upgrade-v19-to-v21') {
   Check ($upgradeDummy.WaitForExit(3000)) 'Installer left the isolated old backend running'
   $results.Add([pscustomobject]@{test='upgrade-automatically-stops-old-backend';passed=$true;fixtureProcessId=$upgradeDummy.Id})
  }
  $markerPath = Join-Path $installRoot '.tai-install.json'
  $marker = Read-Json $markerPath
  Check ($marker.tiaVersion -eq $v -and $marker.tiaLocation -eq $portals[$v]) 'Wrong selected TIA version/location'
  Check ($marker.version -eq '0.11.0' -and !$marker.registered -and !$marker.shortcut) 'Wrong install ownership/registration state'
  Check ($marker.addInRoot -eq $add) 'Wrong isolated AddIn root'
  foreach ($f in $marker.files) { Hash-Equals (Get-OwnedPath $installRoot $f.path) $f.sha256 }
  $runtime = Read-Json (Join-Path $installRoot 'tia-runtime.json')
  Check ($runtime.tiaVersion -eq $v -and $runtime.tiaLocation -eq $portals[$v]) 'Wrong runtime selection'
  $proofPath = Join-Path $installRoot 'install-verification.json'
  $proof = Read-Json $proofPath
  Check ($proof.ok -and $proof.phase -eq 'installed' -and $proof.tiaVersion -eq $v -and $proof.offlineFixture -and !$proof.realMcpStarted) 'Wrong fixture evidence'
  Check (!(Test-Path -LiteralPath (Join-Path $base 'UserAddIns-V19\TAI.addin'))) 'V19 AddIn unexpectedly present'
  $addin21 = Join-Path $base 'UserAddIns-V21\TAI.addin'
  if ($v -eq 21) { Hash-Equals $addin21 $manifest.addInHashes.'21' } else { Check (!(Test-Path -LiteralPath $addin21)) 'Stale V21 AddIn remains' }
  if ($scenario.label -eq 'fresh-v19') {
   $null = New-Item -ItemType Directory -Path (Split-Path $userNested -Parent) -Force
   $null = New-Item -ItemType Directory -Path (Split-Path $userAddIn -Parent) -Force
   [IO.File]::WriteAllText($userNote, 'preserve offline user file')
   [IO.File]::WriteAllText($userNested, '{"preserve":true}')
   [IO.File]::WriteAllText($userAddIn, 'preserve unrelated AddIn user file')
   foreach ($p in @($userNote,$userNested,$userAddIn)) { $userHashes[$p] = (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash }
  }
  foreach ($p in $userHashes.Keys) { Hash-Equals $p $userHashes[$p] }
  Copy-Item -LiteralPath $markerPath -Destination (Join-Path $base ($scenario.label+'.ownership.json'))
  Copy-Item -LiteralPath $proofPath -Destination (Join-Path $base ($scenario.label+'.verification.json'))
  $results.Add([pscustomobject]@{test=$scenario.label;passed=$true;tiaVersion=$v;managedHashCount=@($marker.files).Count;addInPresent=($v -eq 21);userFilesPreserved=$true;offlineFixture=$true})
  Write-Output "PASS: $($scenario.label)"
 }
 $lastMarker = Read-Json (Join-Path $installRoot '.tai-install.json')
 & (Join-Path $installRoot 'installer\Uninstall.ps1') -InstallRoot $installRoot -NoRegistration
 foreach ($f in $lastMarker.files) { Check (!(Test-Path -LiteralPath (Join-Path $installRoot $f.path))) "Owned file remains: $($f.path)" }
 foreach ($name in @('.tai-install.json','install-verification.json','tia-runtime.json')) { Check (!(Test-Path -LiteralPath (Join-Path $installRoot $name))) "Generated metadata remains: $name" }
 foreach ($v in @(19,21)) { Check (!(Test-Path -LiteralPath (Join-Path $base "UserAddIns-V$v\TAI.addin"))) "AddIn V$v remains" }
 foreach ($p in $userHashes.Keys) { Hash-Equals $p $userHashes[$p] }
 $remaining = @(Get-ChildItem -LiteralPath $installRoot -Recurse -File)
 Check ($remaining.Count -eq 2) 'Unexpected files remaining after uninstall'
 $results.Add([pscustomobject]@{test='uninstall';passed=$true;managedFilesRemoved=$true;generatedReportsRemoved=$true;userFilesPreserved=$userHashes.Count})
 $success = @($results.ToArray() | Where-Object { !$_.passed }).Count -eq 0
} catch {
 $results.Add([pscustomobject]@{test='failure';passed=$false;message=$_.Exception.Message;detail=$_.ToString();stack=$_.ScriptStackTrace})
 throw
} finally {
 if ($upgradeDummy -and !$upgradeDummy.HasExited) { $upgradeDummy.Kill(); $null=$upgradeDummy.WaitForExit(3000) }
 $finalExeHash = (Get-FileHash -LiteralPath $SetupExe -Algorithm SHA256).Hash
 $unchanged = $originalExeHash -eq $finalExeHash
 Save-Json ([ordered]@{passed=($success -and $unchanged);setupExe=$SetupExe;setupSha256Before=$originalExeHash;setupSha256After=$finalExeHash;productionExeUnchanged=$unchanged;evidence=$base;mode='offline-extracted-fixture-no-registration';results=@($results.ToArray())}) 'results.json'
 Stop-Transcript | Out-Null
 if (!$unchanged) { throw 'Production EXE changed during test' }
}
if (!$success) { throw "Offline tests completed with failed assertions; see $base\results.json" }
Write-Output "PASS: offline payload integrity and V19 -> V21 -> V19 -> uninstall lifecycle. Evidence: $base"
