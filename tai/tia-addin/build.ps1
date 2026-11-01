# Builds TAI.TiaAddIn (net48/x64) and packages it with the official TIA Portal V21 Add-In Publisher.
# Output: <this dir>\out\TAI.TiaAddIn.addin
# Does not start TIA Portal and does not touch any installed AddIns directory.
param(
    [string]$TiaPublicApiRoot = 'E:\TIA Portal\Portal V21\PublicAPI\V21',
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\TAI.TiaAddIn\TAI.TiaAddIn.csproj'
$manifest = Join-Path $root 'publish\TAI.TiaAddIn.publish.xml'
$outDir = Join-Path $root 'out'
$addin = Join-Path $outDir 'TAI.TiaAddIn.addin'
$log = Join-Path $outDir 'publisher.log'
$publisher = Join-Path $TiaPublicApiRoot 'Siemens.Engineering.AddIn.Publisher.exe'
$apiDir = Join-Path $TiaPublicApiRoot 'net48'

if (-not (Test-Path $publisher)) { throw "Publisher not found: $publisher" }
if ($Configuration -ne 'Release') { throw 'publish\TAI.TiaAddIn.publish.xml points at bin\x64\Release; use -Configuration Release.' }

dotnet build $project -c $Configuration -p:Platform=x64 "-p:TiaPublicApiDir=$apiDir"
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed ($LASTEXITCODE)" }

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
if (Test-Path $addin) { Remove-Item $addin -Force }

# The Publisher returns exit code 0 even when packaging FAILS, so check the log and the file.
$console = & $publisher -f $manifest -o $addin -l $log -c -v 2>&1 | Out-String
Write-Host $console
if ($console -notmatch 'S U C C E E D E D' -or -not (Test-Path $addin)) {
    throw "Publisher failed; see $log"
}
$hash = (Get-FileHash $addin -Algorithm SHA256).Hash
Write-Host "OK: $addin"
Write-Host "SHA256: $hash"
