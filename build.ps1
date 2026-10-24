$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
$out = Join-Path $root 'PropriaClick.exe'
$src = Join-Path $root 'Program.cs'
$man = Join-Path $root 'app.manifest'
$about = Join-Path $root 'About.md'
$license = Join-Path $root 'LICENSE'
$ico = Join-Path $root 'PropriaClick.ico'

& $csc /nologo /target:winexe /out:"$out" /codepage:65001 /win32icon:"$ico" /win32manifest:"$man" `
    /resource:"$about" /resource:"$license" `
    /r:System.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll `
    /r:"$fw\WPF\UIAutomationClient.dll" `
    /r:"$fw\WPF\UIAutomationTypes.dll" `
    /r:"$fw\WPF\WindowsBase.dll" `
    "$src"

if ($LASTEXITCODE -ne 0) { throw "csc failed: $LASTEXITCODE" }
Write-Host "Built: $out"
