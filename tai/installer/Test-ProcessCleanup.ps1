[CmdletBinding()]
param([switch]$SkipLive)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# Only Common.ps1 is loaded. No installer, application, TIA or real MCP is launched.
$common = Join-Path $PSScriptRoot 'Common.ps1'
. $common
$base = Join-Path $PSScriptRoot ('test-runs\process-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $base
$root = Join-Path $base 'installed'
$results = [Collections.Generic.List[object]]::new()
Start-Transcript -Path (Join-Path $base 'test.log') | Out-Null
function Check([bool]$Condition,[string]$Message) { if (!$Condition) { throw $Message } }
function Proc([int]$Id,[string]$Exe,[int]$ParentId=0,[string]$Created='2026-01-01T00:00:00Z',[string]$Name='') {
    if (!$Name) { $Name = [IO.Path]::GetFileName($Exe) }
    [pscustomobject]@{ ProcessId=$Id; ExecutablePath=$Exe; ParentProcessId=$ParentId; CreationDate=$Created; Name=$Name; CommandLine="`"$Exe`" --mention `"$root`"" }
}
function Reset-Mock([object[]]$Items=@()) {
    $script:items = @($Items)
    $script:stops = [Collections.Generic.List[int]]::new()
    $script:windows = [Collections.Generic.List[int]]::new()
    $script:requests = [Collections.Generic.List[string]]::new()
    $script:ports = [Collections.Generic.List[int]]::new()
    $script:sleeps = [Collections.Generic.List[int]]::new()
    $script:onIdentity = $null
    $script:onSleep = $null
    $script:busy = $false
    $script:statusError = $false
    $script:invalidStatus = $false
}
# Operating-system boundaries only are mocked; Get/Stop-TaiProcesses remain real.
function Get-CimInstance {
    param($ClassName,$Filter,$ErrorAction)
    if ($ClassName -ne 'Win32_Process') { throw "Unexpected CIM class $ClassName" }
    if ($Filter) {
        Check ($Filter -match '^ProcessId=(\d+)$') "Unexpected filter $Filter"
        $requested = [int]$Matches[1]
        if ($script:onIdentity) { & $script:onIdentity $requested }
        return @($script:items | Where-Object ProcessId -eq $requested)
    }
    return $script:items
}
function Stop-Process {
    param([int]$Id,$ErrorAction)
    $script:stops.Add($Id)
    $script:items = @($script:items | Where-Object ProcessId -ne $Id)
}
function Get-Process {
    param([int]$Id,$ErrorAction)
    $script:windows.Add($Id)
    $window = [pscustomobject]@{ Id=$Id }
    $window | Add-Member -MemberType ScriptMethod -Name CloseMainWindow -Value { return $true }
    return $window
}
function Get-NetTCPConnection {
    param([int]$OwningProcess,$State,$ErrorAction,$ErrorVariable)
    $script:ports.Add($OwningProcess)
    [pscustomobject]@{ LocalAddress='127.0.0.1'; LocalPort=45001 }
    [pscustomobject]@{ LocalAddress='::1'; LocalPort=45002 }
    [pscustomobject]@{ LocalAddress='0.0.0.0'; LocalPort=45003 }
}
function Invoke-RestMethod {
    param([string]$Uri,$TimeoutSec)
    $script:requests.Add($Uri)
    if ($script:statusError) { throw 'Fixture HTTP timeout' }
    if ($script:invalidStatus) { return [pscustomobject]@{ app='TAI'; busy='false' } }
    [pscustomobject]@{ app='TAI'; busy=$script:busy }
}
function Start-Sleep {
    param([int]$Milliseconds)
    $script:sleeps.Add($Milliseconds)
    if ($script:onSleep) { & $script:onSleep $Milliseconds }
}
function Case([string]$Name,[scriptblock]$Body) {
    try {
        Reset-Mock
        & $Body
        $results.Add([pscustomobject]@{test=$Name;passed=$true;stopped=@($script:stops);windowRequests=@($script:windows);statusRequests=@($script:requests);sleeps=@($script:sleeps)})
        Write-Host "PASS $Name"
    } catch {
        $results.Add([pscustomobject]@{test=$Name;passed=$false;error=$_.Exception.Message;detail=$_.ScriptStackTrace})
        Write-Host "FAIL ${Name}: $($_.Exception.Message)"
    }
}
try {
    Case 'exact-ownership-and-negative-paths' {
        $fixtures = @(
            (Proc 101 "$root\TAI.Desktop.exe"), (Proc 102 "$root\runtime\node.exe"),
            (Proc 103 "$root\mcp\TiaMcpServer.exe"), (Proc 104 "$root\mcp\v19\TiaMcpServer.exe"),
            (Proc 105 "$root\mcp\v21\TiaMcpServer.exe"), (Proc 106 ($root.ToUpperInvariant()+'\RUNTIME\NODE.EXE')),
            (Proc 201 "$base\other\TAI.Desktop.exe"), (Proc 202 "$base\other\runtime\node.exe"),
            (Proc 203 "$root-other\runtime\node.exe"), (Proc 204 "$root\runtime\nested\node.exe"),
            (Proc 205 "$root\node.exe"), (Proc 206 "$root\mcp\v19\extra\TiaMcpServer.exe"),
            (Proc 207 "$root\other\TiaMcpServer.exe"), (Proc 208 "$base\other\TiaMcpServer.exe"),
            (Proc 209 '' 0 '2026-01-01' 'node.exe'), (Proc 210 "$root\TAI.Desktop.exe.bak")
        )
        $got = @(Get-TaiProcesses -Root $root -Processes $fixtures)
        Check (($got.Process.ProcessId | Sort-Object) -join ',' -eq '101,102,103,104,105,106') 'Exact ownership selected an unexpected PID'
        $fixtures | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $base 'ownership-fixtures.json')
        $got | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $base 'ownership-selected.json')
    }
    Case 'webview-only-owned-desktop-descendants' {
        $fixtures = @(
            (Proc 303 'C:\fixture-browser\msedgewebview2.exe' 302),
            (Proc 302 'C:\fixture-browser\msedgewebview2.exe' 301),
            (Proc 301 "$root\TAI.Desktop.exe"),
            (Proc 304 "$base\other\TAI.Desktop.exe"),
            (Proc 305 'C:\fixture-browser\msedgewebview2.exe' 304),
            (Proc 306 'C:\fixture-browser\msedgewebview2.exe' 0),
            (Proc 307 "$root\runtime\node.exe"),
            (Proc 308 'C:\fixture-browser\msedgewebview2.exe' 307),
            (Proc 309 'C:\fixture-browser\helper.exe' 301),
            (Proc 310 '' 301 '2026-01-01' 'msedgewebview2.exe')
        )
        $got = @(Get-TaiProcesses $root $fixtures)
        Check (($got.Process.ProcessId | Sort-Object) -join ',' -eq '301,302,303,307') 'WebView ownership escaped desktop ancestry'
        Check (@($got | Where-Object Role -eq 'webview').Count -eq 2) 'Expected child and grandchild WebViews'
    }
    Case 'webview-stale-parent-pid-not-current-desktop-descendant' {
        # Win32_Process.ParentProcessId can reference an exited process whose PID was reused.
        # A browser born before the current desktop cannot be its child or grandchild.
        $fixtures=@((Proc 311 "$root\TAI.Desktop.exe" 0 '2026-02-01T00:00:00Z'),(Proc 312 'C:\fixture-browser\msedgewebview2.exe' 311 '2026-01-01T00:00:00Z'),(Proc 313 'C:\fixture-browser\msedgewebview2.exe' 312 '2026-01-02T00:00:00Z'))
        $got=@(Get-TaiProcesses $root $fixtures)
        $fixtures | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $base 'stale-parent-fixtures.json')
        $got | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $base 'stale-parent-selected.json')
        Check (($got.Process.ProcessId -join ',') -eq '311') 'Stale ParentProcessId captured WebViews older than the current desktop; risks stopping an unrelated browser'
    }
    Case 'fresh-install-empty-process-list' {
        Stop-TaiProcesses $root
        Assert-Stopped $root
        Check ($script:stops.Count -eq 0 -and $script:ports.Count -eq 0) 'Empty install performed process work'
    }
    Case 'stop-only-owned-processes-and-owned-loopback-ports' {
        Reset-Mock @((Proc 401 "$root\TAI.Desktop.exe"),(Proc 402 "$root\runtime\node.exe"),(Proc 403 "$root\mcp\v21\TiaMcpServer.exe"),(Proc 404 'C:\fixture-browser\msedgewebview2.exe' 401),(Proc 405 "$root-other\runtime\node.exe"),(Proc 406 "$base\other\TiaMcpServer.exe"))
        Stop-TaiProcesses $root
        Assert-Stopped $root
        Check (($script:stops -join ',') -eq '401,403,402,404') 'Unexpected stop PID/order'
        Check (($script:windows -join ',') -eq '401') 'Unexpected desktop window close'
        Check (($script:ports -join ',') -eq '402') 'Inspected a foreign process port'
        Check (($script:requests -join ',') -eq 'http://127.0.0.1:45001/api/status,http://[::1]:45002/api/status') 'Unexpected HTTP status probes'
        Check (($script:items.ProcessId -join ',') -eq '405,406') 'Foreign process was removed'
    }
    Case 'watcher-stops-before-helper-can-relaunch' {
        $watcher=Proc 481 "$root\TAI.Desktop.exe"
        $watcher.CommandLine='"'+$root+'\TAI.Desktop.exe" --watch --root "'+$root+'"'
        Reset-Mock @((Proc 480 "$root\TAI.Desktop.exe"),$watcher)
        $script:onSleep = { param($Delay)
            if ($Delay -eq 1500 -and @($script:items | Where-Object ProcessId -eq 481).Count) {
                $script:items += Proc 482 "$root\TAI.Desktop.exe" 481 '2026-02-01'
            }
        }
        Stop-TaiProcesses $root
        Assert-Stopped $root
        Check (($script:stops -join ',') -eq '481,480') 'Watcher could relaunch a helper while desktop windows were closing'
        Check (($script:windows -join ',') -eq '480') 'Watcher was not stopped before the window close phase'
    }
    Case 'pid-reuse-different-path-never-close-or-stop' {
        Reset-Mock @((Proc 501 "$root\TAI.Desktop.exe"))
        $script:onIdentity = { param($Requested) $script:items = @((Proc 501 "$base\foreign\TAI.Desktop.exe" 0 '2026-02-01')) }
        Stop-TaiProcesses $root
        Check ($script:stops.Count -eq 0 -and $script:windows.Count -eq 0) 'Reused foreign PID was touched'
    }
    Case 'pid-reuse-same-path-new-creation-never-close-or-stop' {
        Reset-Mock @((Proc 502 "$root\TAI.Desktop.exe"))
        $script:onIdentity = { param($Requested) $script:items = @((Proc 502 "$root\TAI.Desktop.exe" 0 '2026-02-01')) }
        $message = ''
        try { Stop-TaiProcesses $root } catch { $message=$_.Exception.Message }
        Check ($message -eq 'TAI processes are still exiting. Retry installation after they close.') 'Replacement process should block copying'
        Check ($script:stops.Count -eq 0 -and $script:windows.Count -eq 0) 'New creation identity was touched'
    }
    Case 'process-exits-before-stop' {
        Reset-Mock @((Proc 503 "$root\mcp\TiaMcpServer.exe"))
        $script:onIdentity = { param($Requested) $script:items = @() }
        Stop-TaiProcesses $root
        Check ($script:stops.Count -eq 0) 'Already exited process was stopped'
    }
    Case 'busy-task-waits-then-process-exits-without-termination' {
        Reset-Mock @((Proc 601 "$root\runtime\node.exe"))
        $script:busy = $true
        $script:onSleep = { param($Delay) if ($Delay -eq 500) { $script:busy=$false; $script:items=@() } }
        Stop-TaiProcesses $root -BusyWaitSeconds 15
        Check (($script:sleeps -join ',') -eq '500') 'Busy task did not wait once'
        Check ($script:stops.Count -eq 0) 'Naturally exiting busy process was stopped'
    }
    Case 'busy-task-finishes-before-cleanup' {
        Reset-Mock @((Proc 602 "$root\runtime\node.exe"))
        $script:busy=$true
        $script:onSleep = { param($Delay) if ($Delay -eq 500) { $script:busy=$false } }
        Stop-TaiProcesses $root -BusyWaitSeconds 15
        Check (($script:sleeps -join ',') -eq '500' -and ($script:stops -join ',') -eq '602') 'Idle backend cleanup after busy wait failed'
    }
    Case 'busy-timeout-aborts-without-any-stop' {
        Reset-Mock @((Proc 603 "$root\runtime\node.exe"),(Proc 604 "$root\TAI.Desktop.exe"))
        $script:busy=$true
        $message=''
        try { Stop-TaiProcesses $root -BusyWaitSeconds 0 } catch { $message=$_.Exception.Message }
        Check ($message -like 'TAI is still completing an engineering task.*') 'Expected busy-task diagnostic'
        Check ($script:stops.Count -eq 0 -and $script:windows.Count -eq 0) 'Busy timeout touched an application'
    }
    Case 'status-timeout-preserves-all-processes' {
        Reset-Mock @((Proc 605 "$root\runtime\node.exe"),(Proc 606 "$root\TAI.Desktop.exe"))
        $script:statusError=$true
        $message=''
        try { Stop-TaiProcesses $root -BusyWaitSeconds 0 } catch { $message=$_.Exception.Message }
        Check ($message -like 'TAI task status could not be confirmed.*') 'Status timeout was treated as idle'
        Check ($script:stops.Count -eq 0 -and $script:windows.Count -eq 0) 'Unknown task state touched an application'
    }
    Case 'invalid-status-preserves-all-processes' {
        Reset-Mock @((Proc 607 "$root\runtime\node.exe"))
        $script:invalidStatus=$true
        $message=''
        try { Stop-TaiProcesses $root -BusyWaitSeconds 0 } catch { $message=$_.Exception.Message }
        Check ($message -like 'TAI task status could not be confirmed.*') 'Invalid busy field was accepted'
        Check ($script:stops.Count -eq 0) 'Invalid status terminated the backend'
    }
    Case 'busy-then-status-timeout-keeps-waiting' {
        Reset-Mock @((Proc 608 "$root\runtime\node.exe"))
        $script:busy=$true
        $script:onSleep = { param($Delay) if ($Delay -eq 500) { $script:statusError=$true; if (@($script:sleeps | Where-Object { $_ -eq 500 }).Count -ge 2) { throw 'Fixture confirmed second wait' } } }
        $message=''
        try { Stop-TaiProcesses $root -BusyWaitSeconds 15 } catch { $message=$_.Exception.Message }
        Check ($message -eq 'Fixture confirmed second wait' -and $script:stops.Count -eq 0) 'Lost busy response allowed termination'
    }
    Case 'late-owned-process-blocks-copy' {
        Reset-Mock @((Proc 701 "$root\mcp\v19\TiaMcpServer.exe"))
        $script:onSleep = { param($Delay) }
        # A new backend appears during the first identity resnapshot.
        $script:onIdentity = { param($Requested) if (@($script:items | Where-Object ProcessId -eq 702).Count -eq 0) { $script:items += (Proc 702 "$root\runtime\node.exe") } }
        $message=''
        try { Stop-TaiProcesses $root } catch { $message=$_.Exception.Message }
        Check ($message -eq 'TAI processes are still exiting. Retry installation after they close.') 'Late process should block copying'
        Check (($script:stops -join ',') -eq '701') 'Late process must not be blindly stopped'
    }
    if (!$SkipLive) {
        # Separate pwsh process: none of this script's mocks can leak into live validation.
        $livePath = Join-Path $base 'live-dummy.ps1'
        @'
param([string]$Common,[string]$Base,[string]$Node)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
. $Common
$root=Join-Path $Base 'live-installed'
$created=[Collections.Generic.List[object]]::new()
$success=$false
try {
    $paths=@("$root\runtime\node.exe","$root\mcp\v21\TiaMcpServer.exe","$root-other\runtime\node.exe","$Base\live-other\TiaMcpServer.exe")
    foreach ($path in $paths) {
        $null=New-Item -ItemType Directory -Path (Split-Path $path -Parent) -Force
        Copy-Item -LiteralPath $Node -Destination $path
        $p=Start-Process -FilePath $path -ArgumentList @('-e','"setInterval(()=>{},1000)"') -PassThru -WindowStyle Hidden
        $created.Add($p)
    }
    Start-Sleep -Milliseconds 700
    foreach ($p in $created) { if ($p.HasExited) { throw "Dummy failed to start: $($p.Id)" } }
    $selected=@(Get-TaiProcesses $root)
    $expected=@($created[0].Id,$created[1].Id)
    $selected | ForEach-Object { [pscustomobject]@{role=$_.Role;pid=$_.Process.ProcessId;parentPid=$_.Process.ParentProcessId;executablePath=$_.Process.ExecutablePath;creationDate=[string]$_.Process.CreationDate} } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Base 'live-selected.json')
    $actualIds=($selected.Process.ProcessId | Sort-Object) -join ','
    $expectedIds=($expected | Sort-Object) -join ','
    if ($actualIds -ne $expectedIds) { throw "Live selection mismatch: actual=$actualIds expected=$expectedIds" }
    Stop-TaiProcesses $root -BusyWaitSeconds 0
    Assert-Stopped $root
    foreach ($p in @($created[0],$created[1])) { if (!$p.WaitForExit(5000)) { throw "Owned dummy survived: $($p.Id)" } }
    foreach ($p in @($created[2],$created[3])) { if ($p.HasExited) { throw "Foreign dummy was killed: $($p.Id)" } }
    $success=$true
} finally {
    $observed=@($created | ForEach-Object { [pscustomobject]@{pid=$_.Id;exitedBeforeFixtureCleanup=$_.HasExited;startTime=$_.StartTime.ToString('o')} })
    # Process handles belong solely to fixtures created above; never enumerate or kill by name.
    foreach ($p in $created) { if (!$p.HasExited) { $p.Kill(); $null=$p.WaitForExit(5000) } }
    [pscustomobject]@{passed=$success;processes=$observed;allFixtureProcessesExited=(@($created | Where-Object { !$_.HasExited }).Count -eq 0)} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Base 'live-result.json')
    foreach ($p in $created) { $p.Dispose() }
}
'@ | Set-Content -LiteralPath $livePath -Encoding UTF8
        Case 'live-isolated-node-and-mcp-dummies' {
            $node = (Get-Command node -CommandType Application -ErrorAction Stop).Source
            $shell = (Get-Command pwsh -CommandType Application -ErrorAction Stop).Source
            & $shell -NoProfile -File $livePath -Common $common -Base $base -Node $node
            Check ($LASTEXITCODE -eq 0) "Live fixture exited $LASTEXITCODE"
            $proof=Get-Content -LiteralPath (Join-Path $base 'live-result.json') -Raw | ConvertFrom-Json
            Check ($proof.passed -and $proof.allFixtureProcessesExited) 'Live fixture proof failed'
        }
    }
} finally {
    $failed=@($results | Where-Object { !$_.passed }).Count
    [pscustomobject]@{common=$common;commonSha256=(Get-FileHash -LiteralPath $common -Algorithm SHA256).Hash;passed=($failed -eq 0);testCount=$results.Count;failed=$failed;liveSkipped=[bool]$SkipLive;results=@($results)} | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $base 'results.json') -Encoding UTF8
    Write-Host "Evidence: $base"
    Stop-Transcript | Out-Null
}
if ($failed) { throw "$failed process cleanup tests failed. Evidence: $base" }
