param([string]$ApiDir = 'E:\TIA Portal\Portal V21\PublicAPI\V21\net48')
[AppDomain]::CurrentDomain.add_ReflectionOnlyAssemblyResolve({ param($s,$e) $n=(New-Object Reflection.AssemblyName $e.Name).Name; $p=Join-Path $ApiDir "$n.dll"; if(Test-Path $p){[Reflection.Assembly]::ReflectionOnlyLoadFrom($p)} else {[Reflection.Assembly]::ReflectionOnlyLoad($e.Name)} })
$a=[Reflection.Assembly]::ReflectionOnlyLoadFrom((Join-Path $ApiDir 'Siemens.Engineering.AddIn.Base.dll'))
foreach($t in 'Siemens.Engineering.AddIn.MessageBoxProvider','Siemens.Engineering.AddIn.Menu.MenuSelectionProvider`1'){ $ty=$a.GetType($t,$true); "-- $t"
 $ty.GetMembers([Reflection.BindingFlags]'Public,Instance,Static,DeclaredOnly') | % { try{$s=$_.ToString()}catch{$s="<$($_.Name)>"}; "   [$($_.MemberType)] $s" } }
$a.GetExportedTypes() | ? { $_.Namespace -eq 'Siemens.Engineering.AddIn' } | % { "   T $($_.FullName)" }
