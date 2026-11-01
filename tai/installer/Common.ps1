Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:ProductId = 'TAI.UserLocal.1'
function Get-TiaInstallation {
    param([ValidateSet(0,19,21)][int]$Version = 0, [string]$Location)
    # Automatic selection always uses the highest supported installation.
    $versions = if ($Version) { @($Version) } else { @(21,19) }
    if ($Location -and !$Version) { throw 'TiaLocation requires an explicit TiaVersion.' }
    foreach ($v in $versions) {
        $roots = @()
        if ($Location) { $roots = @($Location) }
        else {
            foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64,[Microsoft.Win32.RegistryView]::Registry32)) {
                $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,$view)
                try {
                    $key = $base.OpenSubKey("SOFTWARE\Siemens\Automation\_InstalledSW\TIAP$v\TIA_Opns")
                    if ($key) { try { $roots += [string]$key.GetValue('Path') } finally { $key.Dispose() } }
                    $suffix = if ($v -eq 21) { '\net48' } else { '' }
                    $key = $base.OpenSubKey("SOFTWARE\Siemens\Automation\Openness\$v.0\PublicAPI\$v.0.0.0$suffix")
                    if ($key) {
                        try {
                            $name = if ($v -eq 21) { 'Siemens.Engineering.Base' } else { 'Siemens.Engineering' }
                            $dll = [string]$key.GetValue($name)
                            if ($dll) { $d = [IO.DirectoryInfo][IO.Path]::GetDirectoryName($dll); if ($v -eq 21) { $d=$d.Parent }; $roots += $d.Parent.Parent.FullName }
                        } finally { $key.Dispose() }
                    }
                } finally { $base.Dispose() }
            }
            foreach ($p in @($env:TAI_TIA_LOCATION,$env:TiaPortalLocation)) { if ($p) { $roots += $p } }
            foreach ($p in @($env:ProgramFiles,${env:ProgramFiles(x86)})) { if ($p) { $roots += Join-Path $p "Siemens\Automation\Portal V$v" } }
        }
        foreach ($root in @($roots | Where-Object { $_ } | Select-Object -Unique)) {
            $relative = if ($v -eq 19) { 'PublicAPI\V19\Siemens.Engineering.dll' } else { 'PublicAPI\V21\net48\Siemens.Engineering.Base.dll' }
            $dll = Join-Path $root $relative
            if (Test-Path -LiteralPath $dll -PathType Leaf) {
                # A stale or foreign copy in an earlier candidate must not abort the search.
                try {
                    $identity = [Reflection.AssemblyName]::GetAssemblyName($dll)
                    if ($identity.Version.Major -ne $v) { continue }
                } catch { continue }
                return [pscustomobject]@{ Version=$v; Location=[IO.Path]::GetFullPath($root); Assembly=$dll }
            }
        }
    }
    throw 'TIA Portal V19/V21 with matching Openness SDK was not found. Specify -TiaVersion 19 or 21 and -TiaLocation <Portal root>.'
}

function Get-SafeRoot([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'An explicit nonempty root is required.' }
    $p = [IO.Path]::GetFullPath($Path).TrimEnd('\','/')
    if ($p -notmatch '^[A-Za-z]:\\' -or $p.Length -lt 8) { throw "Unsafe root: $p" }
    $forbidden = @($env:USERPROFILE,$env:LOCALAPPDATA,$env:APPDATA,$env:ProgramFiles,${env:ProgramFiles(x86)},$env:WINDIR,([IO.Path]::GetPathRoot($p)),(Join-Path $env:LOCALAPPDATA 'TAI'))
    foreach ($f in $forbidden) { if ($f -and $p -eq $f.TrimEnd('\','/')) { throw "Protected root: $p" } }
    foreach ($f in @($env:WINDIR,$env:ProgramFiles,${env:ProgramFiles(x86)},(Join-Path $env:LOCALAPPDATA 'TAI'))) { if ($f -and $p.StartsWith($f.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) { throw "Protected root: $p" } }
    if ($p.Substring(2).Contains(':') -or $p -match '[. ]([\\/]|$)') { throw "Unsafe root: $p" }
    $cursor = $p
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) { if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point not allowed: $cursor" } }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
    return $p
}
function Get-OwnedPath([string]$Root,[string]$Relative) {
    if (!$Relative -or [IO.Path]::IsPathRooted($Relative) -or $Relative -match '(^|[\\/])\.\.([\\/]|$)' -or $Relative.Contains(':')) { throw "Unsafe managed path: $Relative" }
    $p = [IO.Path]::GetFullPath((Join-Path $Root $Relative))
    if (!$p.StartsWith($Root.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Managed path escaped root.' }
    $null = Get-SafeRoot $p
    return $p
}
function Read-Ownership([string]$Root) {
    $marker = Get-OwnedPath $Root '.tai-install.json'
    if (!(Test-Path -LiteralPath $marker -PathType Leaf)) { throw "Refusing unmarked existing root: $Root" }
    $m = Get-Content -Raw -LiteralPath $marker | ConvertFrom-Json
    if ($m.product -ne $script:ProductId -or $m.root -ne $Root) { throw "Invalid ownership marker: $marker" }
    foreach ($f in $m.files) { $null = Get-OwnedPath $Root $f.path }
    return $m
}
function Assert-Stopped([string]$Root) {
    $busy = @(Get-CimInstance Win32_Process -ErrorAction Stop | Where-Object {
        ($_.ExecutablePath -and $_.ExecutablePath.StartsWith($Root+'\',[StringComparison]::OrdinalIgnoreCase)) -or
        ($_.CommandLine -and $_.Name -match '^(node|TAI.Desktop|TiaMcpServer)\.exe$' -and $_.CommandLine.IndexOf($Root,[StringComparison]::OrdinalIgnoreCase) -ge 0)
    })
    if ($busy.Count) { throw ('Close TAI and its backend/MCP processes, then retry. Running PIDs: '+(($busy | ForEach-Object { "$($_.Name)=$($_.ProcessId)" }) -join ', ')+'. No process was terminated; do not terminate TIA.') }
}
function Assert-Hash([string]$Path,[string]$Hash) {
    $null = Get-SafeRoot $Path
    if (!(Test-Path -LiteralPath $Path -PathType Leaf) -or (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Hash) { throw "Managed file missing or modified; refusing overwrite/delete: $Path" }
}
function Remove-OwnedFiles([string]$Root,$Files) {
    foreach ($f in $Files) { $p = Get-OwnedPath $Root $f.path; if (Test-Path -LiteralPath $p) { Assert-Hash $p $f.sha256 } }
    foreach ($f in $Files) { $p = Get-OwnedPath $Root $f.path; if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Force } }
    # Only remove empty ancestor directories of owned files, never recursively.
    $dirs = @($Files | ForEach-Object { $d = [IO.Path]::GetDirectoryName((Get-OwnedPath $Root $_.path)); while ($d -and $d -ne $Root) { $d; $d = [IO.Path]::GetDirectoryName($d) } } | Sort-Object -Unique | Sort-Object Length -Descending)
    foreach ($d in $dirs) { if ((Test-Path -LiteralPath $d) -and @(Get-ChildItem -LiteralPath $d -Force).Count -eq 0) { [IO.Directory]::Delete($d) } }
}
