[CmdletBinding()]
param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'WinUI XAML compilation requires a Windows build host.' }
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo 'artifacts' }
$out = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $out | Out-Null
$stage = Join-Path $out ('windows-stage-' + [guid]::NewGuid().ToString('N'))
$packageName = 'Unictrl-0.7.0-windows-x64-portable'
$payload = Join-Path $stage $packageName
New-Item -ItemType Directory -Force -Path $payload | Out-Null
$project = Join-Path $repo 'windows/Unictrl/Unictrl.csproj'
$tests = Join-Path $repo 'windows/Unictrl.Tests/Unictrl.Tests.csproj'
& dotnet test $tests -c Release --logger 'trx;LogFileName=windows.trx' --results-directory (Join-Path $out 'windows-tests')
if ($LASTEXITCODE -ne 0) { throw 'Windows tests failed.' }
& dotnet publish $project -c Release -p:Platform=x64 -r win-x64 --self-contained true '-p:WindowsAppSDKSelfContained=true' '-p:PublishSingleFile=false' '-p:PublishTrimmed=false' '-p:DebugType=None' '-p:DebugSymbols=false' -o $payload "-bl:$(Join-Path $out 'windows-publish.binlog')"
if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed.' }
foreach ($relative in @('Unictrl.exe','Unictrl.dll','Unictrl.pri','App.xbf','MainWindow.xbf','coreclr.dll','hostfxr.dll','Microsoft.UI.Xaml.dll','Assets/Unictrl.ico','Assets/UnictrlTray.ico')) {
    if (-not (Test-Path -LiteralPath (Join-Path $payload $relative) -PathType Leaf)) { throw "Publish output missing $relative" }
}
# Carry the MSVC runtime app-locally; the build runner may have it installed globally.
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (-not (Test-Path $vswhere)) { throw 'MSVC redistributable lookup needs Visual Studio Build Tools.' }
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Redist.14.Latest -property installationPath
if (-not $vs) { throw 'Visual Studio MSVC redistributables were not found.' }
$redistVersions = Get-ChildItem -LiteralPath (Join-Path $vs 'VC/Redist/MSVC') -Directory | Sort-Object Name -Descending
$crt = $null
foreach ($version in $redistVersions) {
    $x64 = Join-Path $version.FullName 'x64'
    if (Test-Path $x64) {
        $crt = Get-ChildItem -LiteralPath $x64 -Directory -Filter 'Microsoft.VC*.CRT' | Select-Object -First 1
        if ($crt) { break }
    }
}
if (-not $crt) { throw 'An x64 release CRT directory was not found.' }
Get-ChildItem -LiteralPath $crt.FullName -File -Filter '*.dll' | Copy-Item -Destination $payload
foreach ($required in @('msvcp140.dll','vcruntime140.dll','vcruntime140_1.dll')) {
    if (-not (Test-Path (Join-Path $payload $required))) { throw "App-local CRT missing $required" }
}
# Verify the launcher's PE machine field rather than relying on its file name.
$pe = [IO.File]::ReadAllBytes((Join-Path $payload 'Unictrl.exe'))
$peOffset = [BitConverter]::ToInt32($pe, 0x3c)
if ([BitConverter]::ToUInt16($pe, $peOffset + 4) -ne 0x8664) { throw 'The launcher is not Windows x64.' }
$runtimeConfig = Get-Content (Join-Path $payload 'Unictrl.runtimeconfig.json') -Raw | ConvertFrom-Json
if ($runtimeConfig.runtimeOptions.PSObject.Properties.Name -contains 'framework') { throw 'Publish is still framework-dependent.' }
Set-Content -LiteralPath (Join-Path $payload 'Unictrl.portable') -Value 'Portable mode v1' -Encoding utf8
Copy-Item (Join-Path $repo 'LICENSE') (Join-Path $payload 'LICENSE.txt')
Copy-Item (Join-Path $PSScriptRoot 'portable-README.zh-Hans.txt') (Join-Path $payload '使用说明.txt')
# The diagnostic exits before creating a window, input receiver or autostart entry.
$process = Start-Process -FilePath (Join-Path $payload 'Unictrl.exe') -ArgumentList '--discover-test' -WorkingDirectory $payload -PassThru
if (-not $process.WaitForExit(30000)) {
    $process.Kill()
    throw 'Windows runtime smoke test timed out.'
}
if ($process.ExitCode -ne 0) { throw "Windows runtime smoke test failed: exit $($process.ExitCode)" }
$log = Join-Path $payload 'Data/unictrl.log'
if (-not (Test-Path $log) -or -not (Select-String -LiteralPath $log -Pattern 'discover_test' -Quiet)) { throw 'Smoke test did not reach the application diagnostic.' }
$clipboardProcess = Start-Process -FilePath (Join-Path $payload 'Unictrl.exe') -ArgumentList '--clipboard-test' -WorkingDirectory $payload -PassThru
if (-not $clipboardProcess.WaitForExit(40000)) { $clipboardProcess.Kill(); throw 'Clipboard native integration test timed out.' }
if ($clipboardProcess.ExitCode -ne 0) { Get-Content $log; throw 'Clipboard native integration test failed.' }
if (-not (Select-String -LiteralPath $log -Pattern 'clipboard_test passed' -Quiet)) { Get-Content $log; throw 'Clipboard integration success marker missing.' }
$updateProcess = Start-Process -FilePath (Join-Path $payload 'Unictrl.exe') -ArgumentList '--update-test' -WorkingDirectory $payload -PassThru
if (-not $updateProcess.WaitForExit(120000)) { $updateProcess.Kill(); throw 'Updater integration test timed out.' }
if ($updateProcess.ExitCode -ne 0 -or -not (Select-String -LiteralPath $log -Pattern 'update_test passed' -Quiet)) { Get-Content $log; throw 'Updater integration test failed.' }
$fileProcess = Start-Process -FilePath (Join-Path $payload 'Unictrl.exe') -ArgumentList '--files-test' -WorkingDirectory $payload -PassThru
if (-not $fileProcess.WaitForExit(120000)) { $fileProcess.Kill(); Get-Content $log; throw 'File native integration test timed out.' }
if ($fileProcess.ExitCode -ne 0 -or -not (Select-String -LiteralPath $log -Pattern 'files_test passed' -Quiet)) { Get-Content $log; throw 'File native integration test failed.' }
Write-Host "FILE_TRANSFER_TEST=passed encrypted_bidirectional_1GiB acceptance cancellation native_file_list"
$panelProcess = Start-Process -FilePath (Join-Path $payload 'Unictrl.exe') -ArgumentList '--panel-test' -WorkingDirectory $payload -PassThru
if (-not $panelProcess.WaitForExit(30000)) { $panelProcess.Kill(); Get-Content $log; throw 'Tray panel native test timed out.' }
if ($panelProcess.ExitCode -ne 0 -or -not (Select-String -LiteralPath $log -Pattern 'panel_test passed' -Quiet)) { Get-Content $log; throw 'Tray panel native test failed.' }
Copy-Item (Join-Path $payload 'Data/panel-test.png') (Join-Path $out 'windows-tests/panel-test.png')
# Exclude diagnostic-generated personal state from the distributable archive.
Remove-Item -LiteralPath (Join-Path $payload 'Data') -Recurse -Force
$sourceStage = Join-Path $stage 'Source'
New-Item -ItemType Directory -Path $sourceStage | Out-Null
# Include actual modified source, not an archive of the unmodified upstream commit.
$sourceRoots = @('windows','macos','assets','tests','docs','tools','.github')
foreach ($dir in $sourceRoots) {
    $base = Join-Path $repo $dir
    if (-not (Test-Path $base)) { continue }
    foreach ($file in Get-ChildItem -LiteralPath $base -Recurse -File) {
        $relative = [IO.Path]::GetRelativePath($repo, $file.FullName)
        if ($relative -match '(^|[\\/])(bin|obj|Data|\.build[^\\/]*|\.git)([\\/]|$)') { continue }
        $destination = Join-Path $sourceStage $relative
        New-Item -ItemType Directory -Force -Path (Split-Path $destination) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination
    }
}
foreach ($file in Get-ChildItem -LiteralPath $repo -File) {
    if ($file.Extension -eq '.md' -or $file.Name -eq 'LICENSE') { Copy-Item $file.FullName $sourceStage }
}
Compress-Archive -Path (Join-Path $sourceStage '*') -DestinationPath (Join-Path $payload 'Source.zip')
$buildInfo = [ordered]@{
    version = '0.7.0'; sourceCommit = $env:GITHUB_SHA; architecture = 'x64'; baseCommit = 'db181b7e0ec03ed271dac5ed6df90b92c4d8b745'
    portable = $true; dotnetSelfContained = $true; windowsAppSDKSelfContained = $true
    layoutVersion = 2; feature = 'text and PNG clipboard with signed automatic updates'; windowsTests = 'passed'; runtimeSmokeTest = 'passed'; clipboardNativeIntegration = 'passed'; updateIntegration = 'passed'; compiledAtUtc = [DateTime]::UtcNow.ToString('o')
}
$buildInfo | ConvertTo-Json | Set-Content (Join-Path $payload 'BUILD-INFO.json') -Encoding utf8
$zip = Join-Path $out ($packageName + '.zip')
if (Test-Path $zip) { throw "Output already exists: $zip. Use another output directory to keep the previous package." }
Compress-Archive -LiteralPath $payload -DestinationPath $zip
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    foreach ($required in @('Unictrl.exe','Unictrl.portable','Microsoft.UI.Xaml.dll','coreclr.dll','vcruntime140.dll','Source.zip')) {
        if (-not ($archive.Entries.FullName -contains "$packageName/$required")) { throw "ZIP is missing $required" }
    }
} finally { $archive.Dispose() }
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $([IO.Path]::GetFileName($zip))" | Set-Content -LiteralPath ($zip + '.sha256') -Encoding ascii
Write-Host "Portable ZIP ready: $zip"
Write-Host "UPDATE_ASSET_SHA256=$hash"
Write-Host "UPDATE_ASSET_SIZE=$((Get-Item -LiteralPath $zip).Length)"
