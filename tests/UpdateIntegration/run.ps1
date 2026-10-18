param([string]$Dotnet = "dotnet")
$ErrorActionPreference = 'Stop'
$repository = Split-Path (Split-Path $PSScriptRoot)
$work = Join-Path $repository ('artifacts/update-integration-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$env:DOTNET_ROOT = Split-Path (Resolve-Path $Dotnet)
$assistant = Join-Path $work 'assistant'
& $Dotnet publish (Join-Path $PSScriptRoot 'Assistant') -c Release -r win-x64 --self-contained false -o $assistant
if ($LASTEXITCODE) { throw 'Fixture app build failed' }
$payloadRoot = Join-Path $work 'payload'
New-Item -ItemType Directory -Path $payloadRoot | Out-Null
Copy-Item "$assistant/*" $payloadRoot
Copy-Item (Join-Path $repository 'dist/portable/ProPresenterAssistant.Shell.cer') $payloadRoot
'test shell DLL' | Set-Content (Join-Path $payloadRoot 'ProPresenterAssistant.Shell-1.0.1.dll')
'test MSIX' | Set-Content (Join-Path $payloadRoot 'ProPresenterAssistant.Shell.msix')
New-Item -ItemType Directory -Path (Join-Path $payloadRoot 'Assets') | Out-Null
'test logo' | Set-Content (Join-Path $payloadRoot 'Assets/Logo.png')
'new' | Set-Content (Join-Path $payloadRoot 'version.txt') -NoNewline
$payload = Join-Path $work 'payload.zip'
Compress-Archive "$payloadRoot/*" $payload
$installer = Join-Path $work 'installer'
& $Dotnet publish (Join-Path $PSScriptRoot 'Installer') -c Release -r win-x64 --self-contained false "-p:PayloadPath=$payload" -o $installer
if ($LASTEXITCODE) { throw 'Fixture installer build failed' }
$checks = @()
foreach ($mode in @('success', 'cancel', 'registration-failure', 'startup-failure')) {
    $root = Join-Path $work $mode
    New-Item -ItemType Directory -Path $root | Out-Null
    Copy-Item "$assistant/*" $root
    'old' | Set-Content (Join-Path $root 'version.txt') -NoNewline
    'preserved' | Set-Content (Join-Path $root 'user-settings.txt') -NoNewline
    $env:PPA_TEST_ROOT = $root
    $env:PPA_TEST_MODE = $mode
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $root 'ProPresenterAssistant.exe'))
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.ArgumentList.Add('--parent-run')
    $start.ArgumentList.Add((Join-Path $installer 'UpdateInstallerFixture.exe'))
    $parent = [Diagnostics.Process]::Start($start)
    if (-not $parent.WaitForExit(45000) -or $parent.ExitCode -ne 0) { throw "Parent failed: $mode" }
    $installerId = [int](Get-Content (Join-Path $root 'installer-pid.txt'))
    $child = Get-Process -Id $installerId -ErrorAction SilentlyContinue
    if ($child -and -not $child.WaitForExit(60000)) { throw "Detached installer timed out: $mode" }
    $expected = if ($mode -eq 'success') { 'new' } else { 'old' }
    if ((Get-Content (Join-Path $root 'version.txt') -Raw) -ne $expected) { throw "Wrong installed version: $mode" }
    if ((Get-Content (Join-Path $root 'user-settings.txt') -Raw) -ne 'preserved') { throw "Settings changed: $mode" }
    if ($mode -eq 'success' -and -not (Test-Path (Join-Path $root 'healthy.txt'))) { throw 'No successful restart acknowledgement' }
    if ($mode -like '*failure') {
        $deadline = [DateTime]::UtcNow.AddSeconds(15)
        while (-not (Test-Path (Join-Path $root 'recovered.txt')) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 200 }
        if (-not (Test-Path (Join-Path $root 'recovered.txt'))) { throw "Recovery did not reopen the previous app: $mode" }
    }
    $checks += "$mode passed"
    Write-Output "$mode passed"
}
$checks | ConvertTo-Json | Set-Content (Join-Path $repository 'artifacts/update-integration-results.json')
Remove-Item Env:/PPA_TEST_ROOT, Env:/PPA_TEST_MODE
