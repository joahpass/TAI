# Reflection probe of the installed TIA Portal V21 Add-In public API (read-only).
param([string]$ApiDir = 'E:\TIA Portal\Portal V21\PublicAPI\V21\net48')
$ErrorActionPreference = 'Stop'
[AppDomain]::CurrentDomain.add_ReflectionOnlyAssemblyResolve({
  param($s, $e)
  $n = (New-Object Reflection.AssemblyName $e.Name).Name
  $p = Join-Path $ApiDir "$n.dll"
  if (Test-Path $p) { return [Reflection.Assembly]::ReflectionOnlyLoadFrom($p) }
  return [Reflection.Assembly]::ReflectionOnlyLoad($e.Name)
})
$flags = [Reflection.BindingFlags]'Public,NonPublic,Instance,Static,DeclaredOnly'
function Dump($asmName, [string[]]$types) {
  $a = [Reflection.Assembly]::ReflectionOnlyLoadFrom((Join-Path $ApiDir "$asmName.dll"))
  "== $($a.FullName)"
  foreach ($t in $types) {
    $ty = $a.GetType($t, $true)
    try { $bt = [string]$ty.BaseType } catch { $bt = "<unresolved>" }; "-- $($ty.FullName) : $bt abstract=$($ty.IsAbstract) sealed=$($ty.IsSealed)"
    foreach ($m in $ty.GetMembers($flags)) {
      if ($m.MemberType -eq 'Method' -and ($m.IsPrivate -or $m.IsAssembly)) { continue }
      if ($m.MemberType -eq 'Constructor' -and ($m.IsPrivate -or $m.IsAssembly)) { continue }
      if ($m.MemberType -eq 'Field' -and ($m.IsPrivate -or $m.IsAssembly)) { continue }
      $mod = ''
      if ($m.MemberType -in 'Method','Constructor') { if ($m.IsFamily){$mod='protected '} ; if ($m.IsAbstract){$mod+='abstract '} elseif ($m.IsVirtual){$mod+='virtual '} }
      try { $s = $m.ToString() } catch { $s = "<" + $m.Name + ": " + $_.Exception.InnerException.Message + ">" }; "   [$($m.MemberType)] $mod$s"
    }
  }
}
Dump 'Siemens.Engineering.AddIn.Base' @('Siemens.Engineering.AddIn.ProjectTreeAddInProvider','Siemens.Engineering.AddIn.Menu.ContextMenuAddIn','Siemens.Engineering.AddIn.Menu.ContextMenuAddInRoot','Siemens.Engineering.AddIn.Menu.Submenu','Siemens.Engineering.AddIn.Menu.ChildItemFactory','Siemens.Engineering.AddIn.Menu.MenuSelectionProvider','Siemens.Engineering.AddIn.Menu.MenuStatus','Siemens.Engineering.AddIn.Menu.ActionItem`1+OnClickDelegate','Siemens.Engineering.AddIn.Menu.ActionItem`1+OnUpdateStatusDelegate')
Dump 'Siemens.Engineering.AddIn.Utilities' @('Siemens.Engineering.AddIn.Utilities.Process')
Dump 'Siemens.Engineering.AddIn.Permissions' @('Siemens.Engineering.AddIn.Permissions.ProcessStartPermissionAttribute')
$b = [Reflection.Assembly]::ReflectionOnlyLoadFrom((Join-Path $ApiDir 'Siemens.Engineering.Base.dll'))
"== $($b.FullName)"
$tp = $b.GetType('Siemens.Engineering.TiaPortal', $true)
"-- TiaPortal public members:"
$tp.GetMembers([Reflection.BindingFlags]'Public,Instance,Static,DeclaredOnly') | % { try { $s = $_.ToString() } catch { $s = "<" + $_.Name + ">" }; "   [$($_.MemberType)] $s" }
"-- Types with ProcessId / Process in name:"
$b.GetExportedTypes() | ? { $_.Name -match 'Process' } | % { "   $($_.FullName)" }
"-- Members named *ProcessId* / Id on TiaPortalProcess:"
$tpp = $b.GetType('Siemens.Engineering.TiaPortalProcess', $false)
if ($tpp) { $tpp.GetMembers([Reflection.BindingFlags]'Public,Instance,Static,DeclaredOnly') | % { try { $s = $_.ToString() } catch { $s = "<" + $_.Name + ">" }; "   [$($_.MemberType)] $s" } }
