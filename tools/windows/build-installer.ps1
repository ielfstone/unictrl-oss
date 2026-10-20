[CmdletBinding()]
param([string]$OutputDirectory = '', [string]$Version = '0.7.1')
$ErrorActionPreference='Stop'
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if(-not $OutputDirectory){$OutputDirectory=Join-Path $repo 'artifacts'}
$out=[IO.Path]::GetFullPath($OutputDirectory)
$stage=Join-Path $out ('installer-stage-'+[guid]::NewGuid().ToString('N'))
Expand-Archive -LiteralPath (Join-Path $out "Unictrl-$Version-windows-x64-portable.zip") -DestinationPath $stage
$payload=Join-Path $stage "Unictrl-$Version-windows-x64-portable"
$compiler=Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'
if(-not (Test-Path $compiler)){throw 'Inno Setup 6 compiler missing on build runner.'}
& $compiler "/DAppVersion=$Version" "/DPayloadDir=$payload" "/DOutputDir=$out" (Join-Path $PSScriptRoot 'Unictrl.iss')
if($LASTEXITCODE -ne 0){throw 'Installer compilation failed.'}
$installer=Join-Path $out "Unictrl-$Version-windows-x64-Setup.exe"
# Install, migrate, then upgrade the same path on the isolated CI runner.
$portable=Join-Path $stage 'old-portable'
New-Item -ItemType Directory -Force (Join-Path $portable 'Data') | Out-Null
Set-Content (Join-Path $portable 'Data/settings.json') '{"DeviceId":"C3FB4A03-2578-454D-8132-3C74835A27C0","Enabled":false,"StartAtLogin":false,"PairingKey":"ci-install-migration-only"}' -Encoding utf8
$target=Join-Path $stage 'installed app'
foreach($iteration in 1..2){
  $arguments="/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR=`"$target`" /LOG=`"$(Join-Path $out "installer-$iteration.log")`""
  if($iteration -eq 1){$arguments+=" /PORTABLESOURCE=`"$portable`""}else{$arguments+=' /UPDATE=1'}
  $setup=Start-Process -FilePath $installer -ArgumentList $arguments -Wait -PassThru
  if($setup.ExitCode -ne 0){throw "Installation smoke test failed: $($setup.ExitCode)"}
  if(Test-Path (Join-Path $target 'Unictrl.portable')){throw 'Installed app was left in portable mode.'}
  $test=Start-Process -FilePath (Join-Path $target 'Unictrl.exe') -ArgumentList '--installation-test' -Wait -PassThru
  if($test.ExitCode -ne 0){throw 'Installed settings migration/startup test failed.'}
}
$hash=(Get-FileHash $installer -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $([IO.Path]::GetFileName($installer))" | Set-Content ($installer+'.sha256') -Encoding ascii
Write-Host "INSTALLER_ASSET_SHA256=$hash"
Write-Host "INSTALLER_ASSET_SIZE=$((Get-Item $installer).Length)"
