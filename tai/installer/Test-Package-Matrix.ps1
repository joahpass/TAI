[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$SetupExe,[string]$PreviousExe)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Common.ps1')
$SetupExe=(Resolve-Path -LiteralPath $SetupExe).Path
$base=Join-Path $PSScriptRoot ('test-runs\matrix-'+[Guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $base -Force
Start-Transcript -Path (Join-Path $base 'test.log') | Out-Null
$results=New-Object System.Collections.Generic.List[object]
try {
 $v21=Get-TiaInstallation -Version 21
 $portal19=Join-Path $base 'Portal V19'
 $sdk=Join-Path $portal19 'PublicAPI\V19'
 $null=New-Item -ItemType Directory -Path $sdk -Force
 $source=Join-Path $base 'Fixture.cs'
 '[assembly:System.Reflection.AssemblyVersion("19.0.0.0")] public class Fixture {}' | Set-Content -LiteralPath $source
 $csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
 & $csc /nologo /target:library "/out:$sdk\Siemens.Engineering.dll" $source
 if($LASTEXITCODE -ne 0){throw 'Fixture compilation failed'}
 function Run-Setup([string]$Exe,[int]$Version,[string]$Root,[string]$Label) {
  $portal=if($Version -eq 19){$portal19}else{$v21.Location}
  $add=Join-Path $base "UserAddIns-V$Version"
  $arguments='-NoRegistration -InstallRoot "'+$Root+'" -AddInRoot "'+$add+'" -TiaVersion '+$Version+' -TiaLocation "'+$portal+'"'
  $p=Start-Process -FilePath $Exe -ArgumentList $arguments -PassThru
  if(!$p.WaitForExit(180000)){ $p.Kill();throw "Setup timeout: $Label" }
  $p.Refresh()
  if($p.ExitCode -ne 0){throw "Setup failure $($p.ExitCode): $Label"}
  $marker=Get-Content -Raw -LiteralPath (Join-Path $Root '.tai-install.json') | ConvertFrom-Json
  if($marker.tiaVersion -ne $Version){throw 'Selected version mismatch'}
  foreach($f in $marker.files){Assert-Hash (Join-Path $Root $f.path) $f.sha256}
  if($Version -eq 19 -and (Test-Path -LiteralPath (Join-Path $add 'TAI.addin'))){throw 'Wrong AddIn deployed to V19'}
  if($Version -eq 21){Assert-Hash (Join-Path $add 'TAI.addin') $marker.addInHash}
  if($Exe -eq $SetupExe){
   $proof=Get-Content -Raw -LiteralPath (Join-Path $Root 'install-verification.json') | ConvertFrom-Json
   if(!$proof.ok -or $proof.phase -ne 'installed' -or $proof.tiaVersion -ne $Version){throw 'Installed verification evidence missing'}
   Copy-Item -LiteralPath (Join-Path $Root 'install-verification.json') -Destination (Join-Path $base ($Label+'.json'))
  }
  if(Test-Path -LiteralPath (Join-Path $Root 'user-note.txt')){if((Get-Content -Raw -LiteralPath (Join-Path $Root 'user-note.txt')).Trim() -ne 'preserve-user-file'){throw 'User file changed'}}
  $results.Add([pscustomobject]@{test=$Label;passed=$true;tiaVersion=$Version;sdk=if($Version -eq 19){'identity-fixture-not-live'}else{'real-V21-SDK'};root=$Root})
  Write-Output "PASS: $Label"
 }
 # Each root has an independent owned AddIn location; uninstall after each scenario.
 function Assert-Uninstalled([string]$Root) {
  foreach($name in @('.tai-install.json','install-verification.json','tia-runtime.json','TAI.Desktop.exe')) {
   if(Test-Path -LiteralPath (Join-Path $Root $name)){throw "Uninstall left $name behind"}
  }
 }
 foreach($v in @(19,21)) {
  $root=Join-Path $base "fresh-v$v"
  Run-Setup $SetupExe $v $root "fresh-v$v"
  'preserve-user-file' | Set-Content -LiteralPath (Join-Path $root 'user-note.txt')
  Run-Setup $SetupExe $v $root "repeat-v$v"
  & (Join-Path $root 'installer\Uninstall.ps1') -InstallRoot $root -NoRegistration
  if(!(Test-Path -LiteralPath (Join-Path $root 'user-note.txt'))){throw 'Uninstall lost user file'}
  Assert-Uninstalled $root
  if($PreviousExe){
   $root=Join-Path $base "upgrade-v$v"
   Run-Setup $PreviousExe $v $root "baseline-v$v"
   'preserve-user-file' | Set-Content -LiteralPath (Join-Path $root 'user-note.txt')
   Run-Setup $SetupExe $v $root "upgrade-v$v"
   & (Join-Path $root 'installer\Uninstall.ps1') -InstallRoot $root -NoRegistration
   Assert-Uninstalled $root
  }
 }
 # Clean uninstall must remove the generated report and the now-empty root itself.
 $root=Join-Path $base 'clean-v19'
 Run-Setup $SetupExe 19 $root 'clean-install-v19'
 & (Join-Path $root 'installer\Uninstall.ps1') -InstallRoot $root -NoRegistration
 if(Test-Path -LiteralPath $root){throw "Uninstall left the install root behind: $root"}
 $results.Add([pscustomobject]@{test='clean-uninstall-v19';passed=$true;tiaVersion=19;sdk='identity-fixture-not-live';root='removed'})
 Write-Output 'PASS: clean-uninstall-v19'
 $root=Join-Path $base 'switch-version'
 Run-Setup $SetupExe 19 $root 'switch-baseline-v19'
 Run-Setup $SetupExe 21 $root 'switch-v19-to-v21'
 Run-Setup $SetupExe 19 $root 'switch-v21-to-v19'
 if(Test-Path -LiteralPath (Join-Path $base 'UserAddIns-V21\TAI.addin')){throw 'Old V21 AddIn was not removed'}
 & (Join-Path $root 'installer\Uninstall.ps1') -InstallRoot $root -NoRegistration
 $results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $base 'results.json') -Encoding UTF8
 Write-Output "PASS: full matrix. Evidence: $base"
} finally {Stop-Transcript | Out-Null}
