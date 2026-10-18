param([Parameter(Mandatory)][string]$Output)
$ErrorActionPreference = 'Stop'
$vswhere = 'C:/Program Files (x86)/Microsoft Visual Studio/Installer/vswhere.exe'
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Visual Studio C++ x64 build tools are required for the Explorer extension.' }
$vc = (Get-ChildItem (Join-Path $vs 'VC/Tools/MSVC') -Directory | Sort-Object Name -Descending | Select-Object -First 1).FullName
$sdkRoot = 'C:/Program Files (x86)/Windows Kits/10'
$sdk = (Get-ChildItem (Join-Path $sdkRoot 'Include') -Directory | Sort-Object Name -Descending | Select-Object -First 1).Name
New-Item -ItemType Directory -Force -Path $Output | Out-Null
$native = Join-Path $PSScriptRoot 'artifacts/shell-build'
New-Item -ItemType Directory -Force -Path $native | Out-Null
$cl = Join-Path $vc 'bin/Hostx64/x64/cl.exe'
$includes = @("/I$vc/include", "/I$sdkRoot/Include/$sdk/um", "/I$sdkRoot/Include/$sdk/shared", "/I$sdkRoot/Include/$sdk/ucrt")
[xml]$shellManifest = Get-Content (Join-Path $PSScriptRoot 'src/ProPresenterAssistant.Shell/AppxManifest.xml') -Raw
$shellDll = $shellManifest.SelectSingleNode("//*[local-name()='Class']").Path
if ($shellDll -notmatch '^ProPresenterAssistant\.Shell-[0-9.]+\.dll$') { throw 'Invalid shell DLL filename.' }
& $cl /nologo /LD /MT /O2 /std:c++17 /EHsc /W4 $includes (Join-Path $PSScriptRoot 'src/ProPresenterAssistant.Shell/Command.cpp') "/Fo$native/Command.obj" /link /Brepro /EXPORT:DllGetClassObject,PRIVATE /EXPORT:DllCanUnloadNow,PRIVATE "/IMPLIB:$native/Command.lib" "/OUT:$Output/$shellDll" "/LIBPATH:$vc/lib/x64" "/LIBPATH:$sdkRoot/Lib/$sdk/um/x64" "/LIBPATH:$sdkRoot/Lib/$sdk/ucrt/x64" ole32.lib shell32.lib shlwapi.lib uuid.lib
if ($LASTEXITCODE) { throw 'Explorer extension build failed.' }
# Keep already-loaded Explorer DLLs out of replacement; each shell revision has its own filename.
Get-ChildItem -LiteralPath $Output -Filter 'ProPresenterAssistant.Shell*.dll' | Where-Object Name -ne $shellDll | Remove-Item
$package = Join-Path $native 'package'
New-Item -ItemType Directory -Force -Path "$package/Assets", "$Output/Assets" | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'src/ProPresenterAssistant.Shell/AppxManifest.xml') "$package/AppxManifest.xml"
Copy-Item (Join-Path $PSScriptRoot 'assets/app-icon-150.png') "$package/Assets/Logo.png"
Copy-Item "$package/Assets/Logo.png" "$Output/Assets/Logo.png"
$makeappx = Join-Path $sdkRoot "bin/$sdk/x64/makeappx.exe"
# Sparse package intentionally keeps native binaries at the installer-controlled external location.
& $makeappx pack /d $package /p "$Output/ProPresenterAssistant.Shell.msix" /o /nv
if ($LASTEXITCODE) { throw 'Explorer identity package build failed.' }
$certificateId = Join-Path $PSScriptRoot '.tools/shell-signing-thumbprint.txt'
$certificate = $null
if (Test-Path $certificateId) { $thumbprint = (Get-Content $certificateId -Raw).Trim(); $certificate = Get-Item "Cert:/CurrentUser/My/$thumbprint" -ErrorAction SilentlyContinue }
if (-not $certificate) {
    $certificate = New-SelfSignedCertificate -Type Custom -Subject 'CN=ProPresenter Assistant' -FriendlyName 'ProPresenter Assistant local development signing' -KeyUsage DigitalSignature -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3','2.5.29.19={text}') -CertStoreLocation 'Cert:/CurrentUser/My' -NotAfter (Get-Date).AddYears(3)
    Set-Content $certificateId $certificate.Thumbprint
}
# Only the public certificate ships. The signing key stays in the build account's certificate store.
Export-Certificate -Cert $certificate -FilePath "$Output/ProPresenterAssistant.Shell.cer" | Out-Null
$sign = Join-Path $sdkRoot "bin/$sdk/x64/signtool.exe"
& $sign sign /fd SHA256 /sha1 $certificate.Thumbprint "$Output/ProPresenterAssistant.Shell.msix"
if ($LASTEXITCODE) { throw 'Explorer identity package signing failed.' }
