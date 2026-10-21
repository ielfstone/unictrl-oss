[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Payload,[Parameter(Mandatory=$true)][string]$Report)
$ErrorActionPreference='Stop'
$keep=@('en','en-US','zh','zh-Hans','zh-Hant','zh-CN','zh-TW','zh-HK','zh-SG','zh-MO')
$removed=@()
foreach($folder in Get-ChildItem -LiteralPath $Payload -Directory) {
    try { $culture=[Globalization.CultureInfo]::GetCultureInfo($folder.Name) } catch { continue }
    if(-not $culture.Name -or $culture.Name -notmatch '^[a-z]{2,3}(?:-[A-Za-z0-9]{2,8})*$' -or $keep -contains $culture.Name){continue}
    $files=@(Get-ChildItem -LiteralPath $folder.FullName -File -Recurse)
    # Only known satellite/resource-only folders qualify; never remove runtime code or unknown assets.
    if(-not $files.Count -or @($files | Where-Object { $_.Name -notmatch '(?i)(\.resources\.dll|\.mui|\.pri)$' }).Count){continue}
    $removed+=@([ordered]@{language=$folder.Name;files=$files.Count;bytes=($files | Measure-Object Length -Sum).Sum})
    Remove-Item -LiteralPath $folder.FullName -Recurse -Force
}
[ordered]@{keptLanguages=$keep;removed=$removed;removedBytes=($removed | Measure-Object bytes -Sum).Sum} | ConvertTo-Json -Depth 5 | Set-Content $Report -Encoding utf8
