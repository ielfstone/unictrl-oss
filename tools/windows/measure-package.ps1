[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Zip,[Parameter(Mandatory=$true)][string]$Output)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive=[IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($Zip))
try {
    $entries=@($archive.Entries | Where-Object { $_.Name } | ForEach-Object { [ordered]@{path=$_.FullName;bytes=$_.Length;compressed=$_.CompressedLength} })
    $languages=@($entries | Where-Object { $_.path -match '/([a-z]{2,3}(?:-[A-Za-z0-9]{2,8})?)/[^/]+\.(?:resources\.dll|mui|pri)$' } | Group-Object {($_.path -split '/')[-2]} | ForEach-Object { [ordered]@{language=$_.Name;files=$_.Count;bytes=($_.Group | Measure-Object bytes -Sum).Sum;compressed=($_.Group | Measure-Object compressed -Sum).Sum} })
    $report=[ordered]@{archiveBytes=(Get-Item $Zip).Length;files=$entries.Count;unpackedBytes=($entries | Measure-Object bytes -Sum).Sum;languages=$languages;largest=@($entries | Sort-Object bytes -Descending | Select-Object -First 25);source=@($entries | Where-Object { $_.path -match '/Source\.zip$' })}
    $report | ConvertTo-Json -Depth 8 | Set-Content $Output -Encoding utf8
} finally { $archive.Dispose() }
