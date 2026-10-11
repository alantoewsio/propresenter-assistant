param([string]$Dotnet = "dotnet", [switch]$SkipDownload)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
Set-Location -LiteralPath $projectRoot
New-Item -ItemType Directory -Force -Path '.tools', 'dist', 'artifacts' | Out-Null
if (-not (Test-Path '.tools/ffmpeg/ffmpeg-9.0.2-essentials_build/bin/ffmpeg.exe')) {
    if ($SkipDownload) { throw 'FFmpeg 9.0.2 tools missing.' }
    Invoke-WebRequest 'https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.2-essentials_build.zip' -OutFile '.tools/ffmpeg.zip'
    Expand-Archive '.tools/ffmpeg.zip' '.tools/ffmpeg' -Force
}
$ffmpegRoot = Join-Path $projectRoot '.tools/ffmpeg/ffmpeg-9.0.2-essentials_build'
$ffmpegBin = Join-Path $ffmpegRoot 'bin'
$appOutput = Join-Path $projectRoot 'dist/portable'
& $Dotnet publish src/ProPresenterAssistant -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true "-p:FfmpegDir=$ffmpegBin" -o $appOutput
if ($LASTEXITCODE) { throw 'App build failed' }
& (Join-Path $projectRoot 'build-shell.ps1') -Output $appOutput
Copy-Item README.md, THIRD-PARTY-NOTICES.txt $appOutput
Copy-Item (Join-Path $ffmpegRoot 'LICENSE') (Join-Path $appOutput 'FFmpeg-LICENSE.txt')
Copy-Item (Join-Path $ffmpegRoot 'README.txt') (Join-Path $appOutput 'FFmpeg-BUILD.txt')
$payload = Join-Path $projectRoot 'artifacts/payload.zip'
Compress-Archive -Path "$appOutput/*" -DestinationPath $payload -Force
& $Dotnet publish src/ProPresenterAssistant.Setup -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true "-p:PayloadPath=$payload" -o (Join-Path $projectRoot 'dist/setup')
if ($LASTEXITCODE) { throw 'Installer build failed' }
Copy-Item 'dist/setup/ProPresenterAssistant-Setup.exe' 'dist/ProPresenterAssistant-Setup.exe'
Compress-Archive -Path "$appOutput/*" -DestinationPath 'dist/ProPresenterAssistant-Portable.zip' -Force
Get-FileHash 'dist/ProPresenterAssistant-Setup.exe', 'dist/portable/ProPresenterAssistant.exe' | Format-Table
