param(
    [Parameter(Mandatory=$true)][string]$DesktopDir,
    [string]$EvidenceDir = (Join-Path $PSScriptRoot 'artifacts/evidence-0.10.0')
)
$ErrorActionPreference = 'Stop'
$DesktopDir = (Resolve-Path $DesktopDir).Path
New-Item -ItemType Directory -Force -Path $EvidenceDir | Out-Null
$EvidenceDir = (Resolve-Path $EvidenceDir).Path
function Assert($Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class DesktopResources {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    public static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
    [DllImport("kernel32.dll", SetLastError=true)]
    public static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);
    [DllImport("kernel32.dll", SetLastError=true)]
    public static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll", SetLastError=true)]
    public static extern uint SizeofResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll", SetLastError=true)]
    public static extern IntPtr LockResource(IntPtr resource);
    [DllImport("kernel32.dll")]
    public static extern bool FreeLibrary(IntPtr module);
}
'@
$exe = Join-Path $DesktopDir 'TAI.Desktop.exe'
$assembly = [Reflection.AssemblyName]::GetAssemblyName($exe)
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
Assert ($assembly.Version.ToString() -eq '0.10.0.0') 'Assembly version mismatch'
Assert ($version.FileVersion -eq '0.10.0.0') 'File version mismatch'
Assert ($version.ProductVersion -eq '0.10.0') 'Product version mismatch'
Assert ($version.ProductName -eq 'TAI 工程助手') 'Product name mismatch'
Assert ($version.FileDescription -eq 'TAI 工程助手桌面窗口') 'Assembly title mismatch'
Assert ($version.CompanyName -eq 'TAI') 'Company mismatch'
$module = [DesktopResources]::LoadLibraryEx($exe, [IntPtr]::Zero, 0x22)
Assert ($module -ne [IntPtr]::Zero) 'PE data resource load failed'
try {
    $resource = [DesktopResources]::FindResource($module, [IntPtr]1, [IntPtr]24)
    Assert ($resource -ne [IntPtr]::Zero) 'Embedded application manifest missing'
    $size = [DesktopResources]::SizeofResource($module, $resource)
    $loaded = [DesktopResources]::LoadResource($module, $resource)
    $pointer = [DesktopResources]::LockResource($loaded)
    $bytes = [byte[]]::new($size)
    [Runtime.InteropServices.Marshal]::Copy($pointer, $bytes, 0, $size)
    $manifestText = [Text.Encoding]::UTF8.GetString($bytes).Trim([char]0, [char]0xFEFF)
    [xml]$manifest = $manifestText
    $ns = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
    $ns.AddNamespace('v1','urn:schemas-microsoft-com:asm.v1')
    $ns.AddNamespace('v3','urn:schemas-microsoft-com:asm.v3')
    Assert ($manifest.SelectSingleNode('/v1:assembly/v1:assemblyIdentity', $ns).version -eq '0.10.0.0') 'Manifest identity mismatch'
    Assert ($manifest.SelectSingleNode('//v3:requestedExecutionLevel', $ns).level -eq 'asInvoker') 'Execution level mismatch'
    Assert ($manifestText.Contains('PerMonitorV2')) 'DPI manifest missing'
    [IO.File]::WriteAllText((Join-Path $EvidenceDir 'embedded.manifest.xml'), $manifestText)
} finally { [DesktopResources]::FreeLibrary($module) | Out-Null }
$configPath = Join-Path $DesktopDir 'TAI.Desktop.exe.config'
[xml]$config = Get-Content -Raw $configPath
Assert ($config.configuration.startup.supportedRuntime.sku -eq '.NETFramework,Version=v4.8') 'Runtime configuration mismatch'
$required = @('Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.WinForms.dll','Microsoft.Web.WebView2.Wpf.dll','WebView2Loader.dll')
$dependencies = foreach ($name in $required) {
    $path = Join-Path $DesktopDir $name
    Assert (Test-Path -LiteralPath $path -PathType Leaf) "Missing dependency: $name"
    $data = [IO.File]::ReadAllBytes($path)
    $pe = [BitConverter]::ToInt32($data, 0x3c)
    $machine = [BitConverter]::ToUInt16($data, $pe + 4)
    if ($name -eq 'WebView2Loader.dll') { Assert ($machine -eq 0x8664) 'Native loader is not x64' }
    $fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($path).FileVersion
    Assert ($fileVersion -eq '1.0.3537.50') "WebView2 version mismatch: $name $fileVersion"
    [ordered]@{ File=$name; FileVersion=$fileVersion; PEMachine=('0x{0:X4}' -f $machine) }
}
$exeData = [IO.File]::ReadAllBytes($exe)
$exePE = [BitConverter]::ToInt32($exeData, 0x3c)
Assert ([BitConverter]::ToUInt16($exeData, $exePE + 4) -eq 0x8664) 'Desktop executable is not x64'
Assert (-not [Text.Encoding]::UTF8.GetString($exeData).Contains('DesktopEntryChecks')) 'Test type found in release executable'
Assert (@(Get-ChildItem -Path $DesktopDir -Recurse -File | Where-Object { $_.Extension -in '.cs','.rsp','.csproj' -or $_.Name -like '*Checks*' }).Count -eq 0) 'Validation sources or binaries leaked into release'
$compileItemsPath = Join-Path $EvidenceDir 'compile-items.json'
$compileSources = @()
if (Test-Path -LiteralPath $compileItemsPath) {
    $compileSources = @((Get-Content -Raw $compileItemsPath | ConvertFrom-Json).Items.Compile.Identity)
    $expectedSources = @('CopilotLocator.cs','EmbeddedHost.cs','FloatingBall.cs','IntegrationWatcher.cs','MainForm.cs','Program.cs','ServerLauncher.cs')
    Assert (@(Compare-Object $expectedSources $compileSources).Count -eq 0) 'SDK Compile list contains missing or unexpected production sources'
}
$hashes = @(Get-ChildItem -Path $DesktopDir -File -Recurse | Sort-Object FullName | ForEach-Object {
    [ordered]@{ File=[IO.Path]::GetRelativePath($DesktopDir,$_.FullName); SHA256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash; Bytes=$_.Length }
})
$report = [ordered]@{ DesktopDir=$DesktopDir; Result='PASS'; AssemblyVersion=$assembly.Version.ToString(); FileVersion=$version.FileVersion; ProductVersion=$version.ProductVersion; Product=$version.ProductName; Title=$version.FileDescription; Company=$version.CompanyName; Manifest='RT_MANIFEST #1: 0.10.0.0, asInvoker, PerMonitorV2'; Target='net48 / x64'; CompileSources=$compileSources; Dependencies=@($dependencies); Files=$hashes; RuntimeExecution='None: resource inspection only; no TIA, MCP or application startup' }
$report | ConvertTo-Json -Depth 8 | Set-Content -Encoding utf8 (Join-Path $EvidenceDir 'verification.json')
$report | ConvertTo-Json -Depth 8
