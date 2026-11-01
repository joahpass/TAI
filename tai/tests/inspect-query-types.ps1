$ErrorActionPreference='Stop'
$a=[Reflection.Assembly]::LoadFrom('E:/TIA Portal/Portal V21/PublicAPI/V21/net48/Siemens.Engineering.Base.dll')
foreach($name in @('Siemens.Engineering.HW.CustomDataTypes.TableData','Siemens.Engineering.HW.CustomDataTypes.StructuredData','Siemens.Engineering.Security.Certificate')) {
 $type=$a.GetType($name,$true)
 $type.GetProperties() | Select-Object @{n='Owner';e={$name}},Name,@{n='Type';e={$_.PropertyType.FullName}}
}
