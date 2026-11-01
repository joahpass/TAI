param([Parameter(Mandatory=$true)][int]$TargetPid,[string]$DesktopPath=(Join-Path $env:LOCALAPPDATA 'Programs\TAI\TAI.Desktop.exe'))
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '..\embedding-probe\icon-project-msaa.ps1') -DefinitionsOnly
Add-Type @'
using System;using System.Runtime.InteropServices;
public static class TaiToggleVerify {
 public delegate bool EnumProc(IntPtr h,IntPtr l);
 [StructLayout(LayoutKind.Sequential)] public struct POINT {public int X,Y;public POINT(int x,int y){X=x;Y=y;}}
 [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p,IntPtr l);
 [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr h,EnumProc p,IntPtr l);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint p);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
 [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h,uint f);
 [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")] public static extern void mouse_event(uint f,uint x,uint y,uint d,UIntPtr extra);
}
'@
[void][IPAcc]::SetThreadDpiAwarenessContext([IntPtr](-4))
$all=New-Object 'System.Collections.Generic.List[IntPtr]'
[void][TaiToggleVerify]::EnumWindows({param($h,$l) [uint32]$p=0;[void][TaiToggleVerify]::GetWindowThreadProcessId($h,[ref]$p);if($p -eq $TargetPid -and [TaiToggleVerify]::IsWindowVisible($h)){[void][TaiToggleVerify]::EnumChildWindows($h,{param($c,$q)$all.Add($c);return $true},[IntPtr]::Zero)};return $true},[IntPtr]::Zero)
$candidates=@($all | Where-Object {
 [uint32]$candidatePid=0;[void][TaiToggleVerify]::GetWindowThreadProcessId($_,[ref]$candidatePid)
 if($candidatePid -ne $TargetPid -or ![TaiToggleVerify]::IsWindowVisible($_)){return $false}
 try { [IPAcc]::Window($_.ToInt64()) -match 'Name=Application.GoToCopilot_;' } catch { $false }
})
if($candidates.Count -ne 1){throw 'Expected exactly one semantic Copilot command container.'}
$command=$candidates[0];$children=New-Object 'System.Collections.Generic.List[IntPtr]'
[void][TaiToggleVerify]::EnumChildWindows($command,{param($h,$l)if(([IPAcc]::Window($h.ToInt64())) -match 'Description=PEIconicButton'){$children.Add($h)};return $true},[IntPtr]::Zero)
if($children.Count -ne 1){throw 'Expected exactly one semantic Copilot glyph button.'}
$button=$children[0];$root=[TaiToggleVerify]::GetAncestor($command,2)
$chain='';$p=[TaiToggleVerify]::GetParent($command);while($p -ne [IntPtr]::Zero){$chain+=[IPAcc]::Window($p.ToInt64());$p=[TaiToggleVerify]::GetParent($p)}
if($chain -notmatch 'Name=MainToolbar;' -or $chain -notmatch 'Name=ApplicationHeader;'){throw 'Copilot ancestor chain changed.'}
[void][TaiToggleVerify]::SetForegroundWindow($root)
Start-Sleep -Milliseconds 300
$r=New-Object IPAcc+RECT;[void][IPAcc]::GetWindowRect($button,[ref]$r)
$p=[TaiToggleVerify]::GetParent($button);while($p -ne [IntPtr]::Zero){$pr=New-Object IPAcc+RECT;[void][IPAcc]::GetWindowRect($p,[ref]$pr);$r.Left=[Math]::Max($r.Left,$pr.Left);$r.Top=[Math]::Max($r.Top,$pr.Top);$r.Right=[Math]::Min($r.Right,$pr.Right);$r.Bottom=[Math]::Min($r.Bottom,$pr.Bottom);$p=[TaiToggleVerify]::GetParent($p)}
if($r.Right -le $r.Left -or $r.Bottom -le $r.Top){throw 'Original glyph is clipped.'}
$point=New-Object TaiToggleVerify+POINT ([int](($r.Left+$r.Right)/2)),([int](($r.Top+$r.Bottom)/2))
function Get-ToggleHost {
 $h=[TaiToggleVerify]::WindowFromPoint($point);[uint32]$p=0;[void][TaiToggleVerify]::GetWindowThreadProcessId($h,[ref]$p)
 $process=Get-Process -Id $p -ErrorAction Stop
 if($process.Path -ne $DesktopPath){throw 'Click refused: original icon position is not intercepted by the expected TAI executable.'}
 return $p
}
$hostPid=Get-ToggleHost
$panels=@($all | Where-Object {
 [uint32]$p=0;[void][TaiToggleVerify]::GetWindowThreadProcessId($_,[ref]$p)
 $rect=New-Object IPAcc+RECT;[void][IPAcc]::GetWindowRect($_,[ref]$rect)
 $parent=[TaiToggleVerify]::GetParent($_);[uint32]$pp=0;[void][TaiToggleVerify]::GetWindowThreadProcessId($parent,[ref]$pp)
 $p -eq $hostPid -and $pp -eq $TargetPid -and ($rect.Right-$rect.Left) -ge 300 -and ($rect.Bottom-$rect.Top) -ge 300
})
if($panels.Count -ne 1){throw 'Expected one embedded panel belonging to the verified icon interceptor.'}
$panel=$panels[0];$before=[TaiToggleVerify]::IsWindowVisible($panel)
foreach($index in 1,2){
 if((Get-ToggleHost) -ne $hostPid){throw 'Click target changed.'}
 [void][TaiToggleVerify]::SetCursorPos($point.X,$point.Y)
 [TaiToggleVerify]::mouse_event(2,0,0,0,[UIntPtr]::Zero);[TaiToggleVerify]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
 Start-Sleep -Milliseconds 500
 $visible=[TaiToggleVerify]::IsWindowVisible($panel)
 if(($index -eq 1 -and $visible -eq $before) -or ($index -eq 2 -and $visible -ne $before)){throw "Toggle $index failed to change/restore embedded visibility."}
}
Write-Output 'PASS: exact semantic original icon position targets TAI and two physical clicks toggle/restore its embedded panel.'
