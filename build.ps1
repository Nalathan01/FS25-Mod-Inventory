param(
    [string]$OutFile = (Join-Path $PSScriptRoot "dist\Mod Inventur.exe"),
    [string]$Version = "1.0.0.0"
)

$ErrorActionPreference = "Stop"

$fw = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319"
$csc = Join-Path $fw "csc.exe"
if (-not (Test-Path $csc)) { throw "C# compiler not found: $csc" }

$outDir = Split-Path $OutFile -Parent
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }
$buildDir = Join-Path $env:TEMP "ModInventurBuild"
if (-not (Test-Path $buildDir)) { New-Item -ItemType Directory -Path $buildDir | Out-Null }

$assemblyInfo = Join-Path $buildDir "AssemblyInfo.cs"
@"
using System.Reflection;
[assembly: AssemblyTitle("Mod Inventur")]
[assembly: AssemblyProduct("Mod Inventur")]
[assembly: AssemblyDescription("Mod Inventur for Farming Simulator 25")]
[assembly: AssemblyCompany("Nalathan")]
[assembly: AssemblyCopyright("Nalathan")]
[assembly: AssemblyVersion("$Version")]
[assembly: AssemblyFileVersion("$Version")]
"@ | Set-Content -Path $assemblyInfo -Encoding UTF8

$references = @(
    "System.dll", "System.Core.dll", "System.Xml.dll", "System.Web.Extensions.dll", "System.Windows.Forms.dll",
    "System.IO.Compression.dll", "System.IO.Compression.FileSystem.dll",
    (Join-Path $fw "WPF\PresentationCore.dll"), (Join-Path $fw "WPF\WindowsBase.dll"), "System.Xaml.dll"
)

if (Test-Path $OutFile) { Remove-Item $OutFile -Force }
$cscArgs = @("/nologo", "/target:winexe", "/optimize+", "/platform:anycpu", "/out:$OutFile", "/win32icon:$(Join-Path $PSScriptRoot 'assets\icon.ico')")
$cscArgs += $references | ForEach-Object { "/reference:$_" }
$cscArgs += "/resource:$(Join-Path $PSScriptRoot 'ui\index.html'),index.html"
$cscArgs += @(Get-ChildItem (Join-Path $PSScriptRoot "src") -Filter *.cs | ForEach-Object { $_.FullName })
$cscArgs += $assemblyInfo
& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit code $LASTEXITCODE)" }

Get-Item $OutFile | Select-Object FullName, Length, LastWriteTime
