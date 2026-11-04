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
function Get-TaiProcesses {
    param([string]$Root, [object[]]$Processes = @(Get-CimInstance Win32_Process -ErrorAction Stop))
    $Root = Get-SafeRoot $Root
    $selected = @{}
    foreach ($p in $Processes) {
        if (!$p.ExecutablePath) { continue }
        $exe = [IO.Path]::GetFullPath([string]$p.ExecutablePath)
        $role = $null
        if ($exe -eq (Join-Path $Root 'TAI.Desktop.exe')) { $role = 'desktop' }
        elseif ($exe -eq (Join-Path $Root 'runtime\node.exe')) { $role = 'backend' }
        elseif ($exe.StartsWith($Root+'\',[StringComparison]::OrdinalIgnoreCase)) {
            $relative = $exe.Substring($Root.Length+1)
            if ($relative -match '^mcp\\(?:v[0-9]+\\)?TiaMcpServer\.exe$') { $role = 'mcp' }
        }
        if ($role) { $selected[[int]$p.ProcessId] = [pscustomobject]@{ Process=$p; Role=$role } }
    }
    # Only WebView descendants of this installation's desktop are related browser processes.
    $parents = @{}
    foreach ($id in @($selected.Keys)) { if ($selected[$id].Role -eq 'desktop') { $parents[$id]=$selected[$id].Process } }
    for ($depth=0; $depth -lt 16; $depth++) {
        $added=$false
        foreach ($p in $Processes) {
            if ($p.Name -ne 'msedgewebview2.exe' -or !$p.ExecutablePath -or $selected.ContainsKey([int]$p.ProcessId)) { continue }
            if ($parents.ContainsKey([int]$p.ParentProcessId)) {
                $parent=$parents[[int]$p.ParentProcessId]
                if (!$p.CreationDate -or !$parent.CreationDate) { continue }
                try { if ([datetime]$p.CreationDate -lt [datetime]$parent.CreationDate) { continue } } catch { continue }
                $selected[[int]$p.ProcessId]=[pscustomobject]@{Process=$p;Role='webview'}
                $parents[[int]$p.ProcessId]=$p; $added=$true
            }
        }
        if (!$added) { break }
    }
    return @($selected.Values)
}
function Stop-TaiProcesses {
    param([string]$Root, [int]$BusyWaitSeconds=15)
    $Root=Get-SafeRoot $Root
    $targets=@(Get-TaiProcesses $Root)
    if (!$targets.Count) { return }
    Write-Output ('Closing TAI processes for update: '+(($targets | ForEach-Object { "$($_.Process.Name)=$($_.Process.ProcessId)" }) -join ', '))
    # Do not interrupt an engineering task midway. Only inspect ports owned by this backend.
    $deadline=[datetime]::UtcNow.AddSeconds($BusyWaitSeconds)
    do {
        $busy=$false
        $unknown=$false
        foreach ($target in @($targets | Where-Object { $_.Role -eq 'backend' })) {
            $listenerErrors=@()
            $listeners=@(Get-NetTCPConnection -OwningProcess $target.Process.ProcessId -State Listen -ErrorAction SilentlyContinue -ErrorVariable listenerErrors | Where-Object { $_.LocalAddress -in @('127.0.0.1','::1') })
            if (@($listenerErrors | Where-Object { $_.CategoryInfo.Category -ne 'ObjectNotFound' }).Count) { $unknown=$true }
            foreach ($listener in $listeners) {
                try {
                    $address=if ($listener.LocalAddress -eq '::1') {'[::1]'} else {'127.0.0.1'}
                    $state=Invoke-RestMethod -Uri ('http://'+$address+':'+$listener.LocalPort+'/api/status') -TimeoutSec 2
                    if ($state.app -ne 'TAI' -or !($state.busy -is [bool])) { $unknown=$true }
                    elseif ($state.busy) { $busy=$true }
                } catch { $unknown=$true }
            }
        }
        if (!$busy -and !$unknown) { break }
        if ([datetime]::UtcNow -ge $deadline) {
            if ($unknown) { throw 'TAI task status could not be confirmed. Retry installation when the backend responds, or close TAI normally. No process was terminated.' }
            throw 'TAI is still completing an engineering task. Wait for completion, then retry installation; no running task was terminated.'
        }
        Start-Sleep -Milliseconds 500
    } while ($true)
    # The installed startup watcher uses --watch as its first option. Stop it before
    # closing helper windows, otherwise it can relaunch them during the graceful wait.
    $watchers=@($targets | Where-Object { $_.Role -eq 'desktop' -and $_.Process.CommandLine -match '^(?:"[^"]*"|\S+)\s+(?:"--watch"|--watch)(?:\s|$)' })
    foreach ($target in $watchers) {
        $current=Get-CimInstance Win32_Process -Filter ("ProcessId="+$target.Process.ProcessId) -ErrorAction Stop
        if (!$current -or $current.ExecutablePath -ne $target.Process.ExecutablePath -or $current.CreationDate -ne $target.Process.CreationDate) { continue }
        Stop-Process -Id $current.ProcessId -ErrorAction Stop
        Write-Output "Closed TAI integration watcher $($current.ProcessId)."
    }
    # First let desktop windows close so their normal unload handlers can persist state.
    foreach ($target in @($targets | Where-Object { $_.Role -eq 'desktop' })) {
        $current=Get-CimInstance Win32_Process -Filter ("ProcessId="+$target.Process.ProcessId) -ErrorAction Stop
        if ($current -and $current.ExecutablePath -eq $target.Process.ExecutablePath -and $current.CreationDate -eq $target.Process.CreationDate) {
            $window=Get-Process -Id $current.ProcessId -ErrorAction SilentlyContinue
            if ($window) { $null=$window.CloseMainWindow() }
        }
    }
    if (@($targets | Where-Object { $_.Role -eq 'desktop' }).Count) { Start-Sleep -Milliseconds 1500 }
    # Resnapshot identities before stopping: never act on a reused PID or a name match alone.
    foreach ($role in @('desktop','mcp','backend','webview')) {
        foreach ($target in @($targets | Where-Object { $_.Role -eq $role })) {
            $current=Get-CimInstance Win32_Process -Filter ("ProcessId="+$target.Process.ProcessId) -ErrorAction Stop
            if (!$current) { continue }
            if ($current.ExecutablePath -ne $target.Process.ExecutablePath -or $current.CreationDate -ne $target.Process.CreationDate) { continue }
            Stop-Process -Id $current.ProcessId -ErrorAction Stop
            Write-Output "Closed TAI $role process $($current.ProcessId)."
        }
    }
    # Watchers or late child processes may have raced the first snapshot. Do not copy over them.
    for ($attempt=0; $attempt -lt 10; $attempt++) {
        if (!@(Get-TaiProcesses $Root).Count) { return }
        Start-Sleep -Milliseconds 200
    }
    throw 'TAI processes are still exiting. Retry installation after they close.'
}
function Assert-Stopped([string]$Root) {
    $busy = @(Get-TaiProcesses $Root)
    if ($busy.Count) { throw ('TAI files are still in use: '+(($busy | ForEach-Object { "$($_.Process.Name)=$($_.Process.ProcessId)" }) -join ', ')+'. Retry after TAI closes.') }
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
