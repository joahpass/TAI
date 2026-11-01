param([Parameter(Mandatory=$true)][int]$TargetPid,[string]$DesktopPath=(Join-Path $env:LOCALAPPDATA 'Programs\TAI\TAI.Desktop.exe'),[switch]$InspectOnly)
$ErrorActionPreference='Stop'
Add-Type @'
using System; using System.Text; using System.Runtime.InteropServices;
public static class TaiEmbedVerify {
 public delegate bool EnumProc(IntPtr h,IntPtr l);
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
 [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p,IntPtr l);
 [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr h,EnumProc p,IntPtr l);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint p);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
 [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h,int i);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out RECT r);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h,StringBuilder s,int n);
 [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr c);
}
'@
[void][TaiEmbedVerify]::SetThreadDpiAwarenessContext([IntPtr](-4))
$roots=New-Object 'System.Collections.Generic.List[IntPtr]'
[void][TaiEmbedVerify]::EnumWindows({param($h,$l) [uint32]$p=0; [void][TaiEmbedVerify]::GetWindowThreadProcessId($h,[ref]$p); if($p-eq$TargetPid -and [TaiEmbedVerify]::IsWindowVisible($h)){$roots.Add($h)}; return $true},[IntPtr]::Zero)
$rows=New-Object 'System.Collections.Generic.List[object]'
foreach($root in $roots){
 [void][TaiEmbedVerify]::EnumChildWindows($root,{param($h,$l)
  [uint32]$p=0; [void][TaiEmbedVerify]::GetWindowThreadProcessId($h,[ref]$p)
  if($p -ne $TargetPid){
   $process=Get-Process -Id $p -ErrorAction SilentlyContinue
   if($process -and $process.Path -eq $DesktopPath){
    $parent=[TaiEmbedVerify]::GetParent($h); [uint32]$parentPid=0; [void][TaiEmbedVerify]::GetWindowThreadProcessId($parent,[ref]$parentPid)
    $style=[TaiEmbedVerify]::GetWindowLong($h,-16); $r=New-Object TaiEmbedVerify+RECT; [void][TaiEmbedVerify]::GetWindowRect($h,[ref]$r)
    $text=New-Object Text.StringBuilder 256; [void][TaiEmbedVerify]::GetWindowText($h,$text,256)
    $rows.Add([pscustomobject]@{Hwnd=$h.ToInt64();Pid=$p;Parent=$parent.ToInt64();ParentPid=$parentPid;Root=$root.ToInt64();Child=[bool]($style-band 0x40000000);Popup=[bool]($style-band -2147483648);Visible=[TaiEmbedVerify]::IsWindowVisible($h);Width=$r.R-$r.L;Height=$r.B-$r.T;Left=$r.L;Top=$r.T;Title=$text.ToString()})
   }
  }; return $true
 },[IntPtr]::Zero)
}
$rows | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'embedded-window-evidence.json') -Encoding UTF8
$direct=@($rows | Where-Object {$_.ParentPid -eq $TargetPid})
$direct | Format-Table -AutoSize
if(!$InspectOnly){
 $panels=@($direct | Where-Object {$_.Child -and !$_.Popup -and $_.Visible -and $_.Width -ge 300 -and $_.Height -ge 300})
 if(!$panels.Count){throw 'No visible TAI WS_CHILD panel parented to TIA was found.'}
 Write-Output 'PASS: actual visible child panel inside TIA; WS_CHILD set, WS_POPUP clear, parent belongs to target TIA process.'
}
