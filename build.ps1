# Builds dist\WindowTrace.exe with the C# compiler that ships with .NET Framework 4, which is part of
# Windows. No SDK and no Visual Studio needed.
#
#   powershell -ExecutionPolicy Bypass -File build.ps1          build
#   powershell -ExecutionPolicy Bypass -File build.ps1 -Test    build, then run the self-test
param([switch]$Test)
$ErrorActionPreference = 'Stop'

$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe" }
$exe = "$PSScriptRoot\dist\WindowTrace.exe"
New-Item -ItemType Directory -Force (Split-Path $exe) | Out-Null

& $csc /nologo /target:winexe /optimize+ /codepage:65001 /r:System.Management.dll `
    "/win32icon:$PSScriptRoot\assets\app.ico" "/out:$exe" "$PSScriptRoot\src\WindowTrace.cs"
if ($LASTEXITCODE -ne 0) { throw "csc failed with $LASTEXITCODE" }
Write-Host "Built $exe"

if ($Test) {
    # opens a console window for about a second and checks that it was traced
    $p = Start-Process $exe '--selftest' -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "self-test failed with exit code $($p.ExitCode)" }
    Write-Host 'Self-test passed'
}
