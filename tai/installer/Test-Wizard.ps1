param([Parameter(Mandatory=$true)][string]$SetupExe)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Windows.Forms,System.Drawing
$a=[Reflection.Assembly]::LoadFrom((Resolve-Path $SetupExe).Path)
$t=$a.GetType('InstallForm',$true)
$f=[Activator]::CreateInstance($t)
$fields=[Reflection.BindingFlags]'Instance,NonPublic'
$static=[Reflection.BindingFlags]'Static,NonPublic'
$count=0
function Check($Condition,[string]$Message) {if(!$Condition){throw $Message};$script:count++}
function Field([string]$Name) {$t.GetField($Name,$fields).GetValue($f)}
function Invoke-Form([string]$Name,[object[]]$Arguments) {$t.GetMethod($Name,$fields).Invoke($f,$Arguments)}
function Capture([string]$Name) {
 [Windows.Forms.Application]::DoEvents();$f.Refresh()
 $bitmap=New-Object Drawing.Bitmap($f.Width,$f.Height)
 try {$f.DrawToBitmap($bitmap,(New-Object Drawing.Rectangle(0,0,$f.Width,$f.Height)));$bitmap.Save((Join-Path $PSScriptRoot $Name))} finally {$bitmap.Dispose()}
}
try {
 [Windows.Forms.Application]::EnableVisualStyles()
 $f.Show();[Windows.Forms.Application]::DoEvents()
 $version=Field 'version';$portal=Field 'portal';$next=Field 'install';$back=Field 'back';$close=Field 'close'
 Check ($f.Text -eq 'TAI 安装向导') 'Unexpected wizard title'
 Check ((Field 'page') -eq 0) 'Wizard must start at Welcome'
 Check ($next.Text -eq '下一步' -and !$back.Visible) 'Welcome navigation failed'
 Check ($f.FormBorderStyle -eq 'FixedDialog' -and !$f.MaximizeBox) 'Standard dialog sizing failed'
 Capture 'installer-welcome-preview.png'
 $next.PerformClick();[Windows.Forms.Application]::DoEvents()
 Check ((Field 'page') -eq 1) 'Next must show Options'
 Check ($next.Text -eq '安装' -and $back.Visible) 'Options navigation failed'
 Check ($version.Items.Count -eq 3 -and $version.SelectedIndex -eq 0) 'Highest auto selection must be default'
 $detect=$t.GetMethod('DetectPortal',$static)
 foreach($v in @(19,21,19,21)) {
  $version.SelectedIndex=if($v -eq 19){1}else{2};[Windows.Forms.Application]::DoEvents()
  Check ($portal.Text -eq $detect.Invoke($null,@([int]$v))) "Automatic directory failed V$v"
  Check $version.Enabled 'Manual version selection locked'
 }
 # Pure fixture selection: no registry edits, SDK load, TIA or MCP process startup.
 $detected=Field 'detected';$saved=@{};foreach($entry in $detected.GetEnumerator()){$saved[$entry.Key]=$entry.Value}
 (Field 'destination').Text='D:\Apps\TAI'
 $detected.Clear();$detected.Add(19,'D:\Fixture\Portal V19');$detected.Add(21,'D:\Fixture\Portal V21')
 $version.SelectedIndex=0
 Check ((Invoke-Form 'SelectedVersion' @()) -eq 21) 'Auto must choose 21 when 19 and 21 exist'
 Check ($portal.Text -eq 'D:\Fixture\Portal V21') 'Highest portal directory mismatch'
 $version.SelectedIndex=1
 Check ((Invoke-Form 'SelectedVersion' @()) -eq 19) 'Explicit V19 must override highest'
 $portal.Text='D:\Custom\Portal V19';Invoke-Form 'RefreshPortal' @()
 Check ($portal.Text -eq 'D:\Custom\Portal V19') 'Manual path was lost'
 $detected.Remove(21) | Out-Null;$version.SelectedIndex=0
 Check ((Invoke-Form 'SelectedVersion' @()) -eq 19) 'Auto must fall back to 19'
 $detected.Clear();Invoke-Form 'RefreshPortal' @()
 Check ((Invoke-Form 'SelectedVersion' @()) -eq 0) 'Missing installation must require explicit selection'
 Check ((Invoke-Form 'ValidateOptions' @()).Length -gt 0) 'Missing installation accepted'
 foreach($key in $saved.Keys){$detected.Add([int]$key,[string]$saved[$key])};Invoke-Form 'RefreshPortal' @()
 Capture 'installer-preview.png'
 $back.PerformClick();Check ((Field 'page') -eq 0) 'Back must show Welcome'
 $next.PerformClick();Check ((Field 'page') -eq 1) 'Returning to Options failed'
 $t.GetField('running',$fields).SetValue($f,$true);Invoke-Form 'ShowPage' @([int]2)
 Check (!$next.Enabled -and !$close.Enabled -and !$back.Visible) 'Running navigation must be disabled'
 Invoke-Form 'Report' @('TAI_PROGRESS|48|正在安装 TAI 文件')
 Check ((Field 'progress').Value -eq 48) 'Progress protocol failed'
 Invoke-Form 'Report' @('TAI_PROGRESS|12|旧阶段')
 Check ((Field 'progress').Value -eq 48) 'Progress moved backwards'
 Capture 'installer-progress-preview.png'
 Invoke-Form 'CompleteInstall' @([int]1)
 Check ((Field 'page') -eq 3 -and $back.Visible -and $close.Enabled) 'Failure retry controls missing'
 Check (!(Field 'launch').Visible -and (Field 'viewLog').Visible) 'Failure displayed launch option'
 $back.PerformClick();Check ((Field 'page') -eq 1) 'Retry must show Options'
 Invoke-Form 'CompleteInstall' @([int]0)
 Check ((Field 'page') -eq 3 -and $next.Text -eq '完成') 'Success finish page failed'
 Check ((Field 'launch').Visible -and !(Field 'viewLog').Visible -and !$back.Visible) 'Success controls failed'
 Capture 'installer-finish-preview.png'
 Check ($t.GetMethod('HasFramework',$static).Invoke($null,@())) 'Framework detection failed on test host'
 Check ($t.GetMethod('HasWebView',$static).Invoke($null,@())) 'WebView runtime detection failed on test host'
 Write-Output "PASS: $count wizard checks; welcome/options/progress/failure/retry/finish, highest-version fixtures, explicit override, runtime detection, screenshots. No install or TIA/MCP startup."
} finally {$f.Dispose()}
