# Builds the release artifacts:
#   dist\app\                     self-contained build used by the installer
#   dist\PCCommandCenter.exe       portable single-file executable (no .NET install required)
#   dist\PCCommandCenter-Setup.exe installer (Inno Setup 6)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$project = 'src\PCCommandCenter.App\PCCommandCenter.App.csproj'

Remove-Item dist -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory dist | Out-Null

Write-Host '==> Publishing self-contained build' -ForegroundColor Cyan
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o dist\app -nologo
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

Write-Host '==> Publishing portable single-file executable' -ForegroundColor Cyan
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist\portable -nologo
if ($LASTEXITCODE -ne 0) { throw 'single-file publish failed' }
Copy-Item dist\portable\PCCommandCenter.exe dist\PCCommandCenter.exe
Remove-Item dist\portable -Recurse -Force

Write-Host '==> Building installer' -ForegroundColor Cyan
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 (ISCC.exe) not found' }
# Inno Setup does not support paths longer than 260 characters: compile through a short junction when the project lives deep.
$root = $PSScriptRoot
$junction = $null
if ($root.Length -gt 90) {
    $junction = Join-Path $env:TEMP "pcc-build-$PID"
    New-Item -ItemType Junction -Path $junction -Target $root | Out-Null
    $root = $junction
}
try {
    & $iscc /Q (Join-Path $root 'installer\PCCommandCenter.iss')
    if ($LASTEXITCODE -ne 0) { throw 'installer build failed' }
}
finally {
    if ($junction) { [IO.Directory]::Delete($junction) }
}

Get-ChildItem dist -File | Select-Object Name, @{ n = 'Size (MB)'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
