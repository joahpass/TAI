param([Parameter(Mandatory=$true)][int]$TargetPid)
$ErrorActionPreference='Stop'
Add-Type -TypeDefinition 'using System;using System.Runtime.InteropServices;public static class LocatorTestDpi{[DllImport("user32.dll")]public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr c);}'
[void][LocatorTestDpi]::SetThreadDpiAwarenessContext([IntPtr](-4))
Add-Type -Path (Join-Path $PSScriptRoot '..\desktop\CopilotLocator.cs') -ReferencedAssemblies System.Drawing
$type=([AppDomain]::CurrentDomain.GetAssemblies() | ForEach-Object {$_.GetType('TAI.Desktop.PortalDiscovery',$false)} | Where-Object {$_} | Select-Object -First 1)
if(!$type){throw 'Discovery assembly missing'}
$main=$type.GetMethod('FindMainWindow').Invoke($null,@($TargetPid))
if($main -eq [IntPtr]::Zero){throw 'Verified TIA main window not found'}
$locator=$type.Assembly.GetType('TAI.Desktop.CopilotLocator')
$target=$locator.GetMethod('Find').Invoke($null,@($TargetPid,$main))
if(!$target){throw 'Original Copilot semantic target not found in current view'}
[pscustomobject]@{Pid=$TargetPid;Main=$main.ToInt64();Button=$target.ButtonWindow.ToInt64();Command=$target.CommandWindow.ToInt64();Bounds=$target.Bounds.ToString()} | ConvertTo-Json
