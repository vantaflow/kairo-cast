# Télécharge ffmpeg (version LGPL, BtbN/FFmpeg-Builds) dans windows/vendor/ffmpeg.exe,
# embarqué ensuite dans KairoCast.exe à la publication.
$ErrorActionPreference = 'Stop'
$version = 'n8.1'
$name = "ffmpeg-$version-latest-win64-lgpl-$($version.TrimStart('n'))"
$url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/$name.zip"
$vendor = Join-Path $PSScriptRoot '..\vendor'
New-Item -ItemType Directory -Force $vendor | Out-Null
$zip = Join-Path $vendor 'ffmpeg.zip'
Invoke-WebRequest $url -OutFile $zip
Expand-Archive $zip -DestinationPath $vendor -Force
Copy-Item (Join-Path $vendor "$name\bin\ffmpeg.exe") (Join-Path $vendor 'ffmpeg.exe') -Force
Copy-Item (Join-Path $vendor "$name\LICENSE.txt") (Join-Path $vendor 'FFMPEG-LICENSE.txt') -Force
Remove-Item $zip
Write-Host "ffmpeg prêt : $((Get-Item (Join-Path $vendor 'ffmpeg.exe')).Length) octets"
