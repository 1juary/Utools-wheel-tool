$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
Push-Location $PSScriptRoot
try {
    & $compiler /nologo /target:winexe /out:plugin\Wheel.exe /r:System.Web.Extensions.dll /r:Microsoft.VisualBasic.dll .\native\Folders.cs .\native\DirectoryBranch.cs .\native\WheelScrollHook.cs .\native\QuickReminder.cs .\native\Program.cs
    if ($LASTEXITCODE -ne 0) { throw "Windows 组件编译失败 ($LASTEXITCODE)" }
} finally {
    Pop-Location
}
