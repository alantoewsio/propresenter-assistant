$ErrorActionPreference = 'Stop'
$output = Join-Path $PSScriptRoot 'artifacts/shell-contract-test'
& (Join-Path $PSScriptRoot 'build-shell.ps1') -Output $output
$vs = & 'C:/Program Files (x86)/Microsoft Visual Studio/Installer/vswhere.exe' -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$vc = (Get-ChildItem "$vs/VC/Tools/MSVC" -Directory | Sort-Object Name -Descending | Select-Object -First 1).FullName
$sdkRoot='C:/Program Files (x86)/Windows Kits/10'
$sdk=(Get-ChildItem "$sdkRoot/Include" -Directory | Sort-Object Name -Descending | Select-Object -First 1).Name
$cl="$vc/bin/Hostx64/x64/cl.exe"
$includes=@("/I$vc/include","/I$sdkRoot/Include/$sdk/um","/I$sdkRoot/Include/$sdk/shared","/I$sdkRoot/Include/$sdk/ucrt")
$libs=@("/LIBPATH:$vc/lib/x64","/LIBPATH:$sdkRoot/Lib/$sdk/um/x64","/LIBPATH:$sdkRoot/Lib/$sdk/ucrt/x64")
& $cl /nologo /MT /EHsc /std:c++17 $includes "$PSScriptRoot/src/ProPresenterAssistant.Shell/TestCapture.cpp" "/Fo$output/Capture.obj" "/Fe$output/ProPresenterAssistant.exe" /link /SUBSYSTEM:WINDOWS $libs shell32.lib
if($LASTEXITCODE) { throw 'Capture test build failed' }
& $cl /nologo /MT /EHsc /std:c++17 $includes "$PSScriptRoot/src/ProPresenterAssistant.Shell/TestCommand.cpp" "/Fo$output/Test.obj" "/Fe$output/TestCommand.exe" /link $libs ole32.lib shell32.lib uuid.lib
if($LASTEXITCODE) { throw 'COM test build failed' }
$first=Join-Path $output 'First & slide.png'; $second=Join-Path $output 'Ünicode video.mp4'
Set-Content -LiteralPath $first 'test'; Set-Content -LiteralPath $second 'test'
$capture=Join-Path $output 'captured-request.txt'
if(Test-Path -LiteralPath $capture) { Remove-Item -LiteralPath $capture }
& "$output/TestCommand.exe" "$output/ProPresenterAssistant.Shell.dll" $first $second
if($LASTEXITCODE) { throw "Shell contract test failed: $LASTEXITCODE" }
$folder=Join-Path $output 'Folder with pictures'
New-Item -ItemType Directory -Force -Path $folder | Out-Null
Remove-Item -LiteralPath $capture
& "$output/TestCommand.exe" "$output/ProPresenterAssistant.Shell.dll" $folder $second
if($LASTEXITCODE) { throw "Shell folder handoff test failed: $LASTEXITCODE" }
