param([switch]$Test, [string]$OutputDirectory = 'dist')
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$outDir = Join-Path $taskRoot $OutputDirectory
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
Add-Type -AssemblyName System.Drawing
# Build a multi-resolution Windows icon from the selected transparent artwork.
$iconSource = Join-Path $taskRoot 'src\assets\app-icon.png'
$iconPath = Join-Path $taskRoot 'src\assets\ShaiTu.ico'
$iconSizes = @(16,20,24,32,40,48,64,128,256)
$sourceImage = [System.Drawing.Image]::FromFile($iconSource)
$iconStream = [System.IO.MemoryStream]::new()
$iconWriter = [System.IO.BinaryWriter]::new($iconStream)
$iconWriter.Write([uint16]0); $iconWriter.Write([uint16]1); $iconWriter.Write([uint16]$iconSizes.Count)
$pngs = @()
foreach ($size in $iconSizes) {
    $bmp = [System.Drawing.Bitmap]::new($size,$size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.PixelOffsetMode = 'HighQuality'
    $g.CompositingQuality = 'HighQuality'
    $g.DrawImage($sourceImage,[System.Drawing.Rectangle]::new(0,0,$size,$size))
    $ms = [System.IO.MemoryStream]::new(); $bmp.Save($ms,[System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += ,$ms.ToArray()
    $ms.Dispose(); $g.Dispose(); $bmp.Dispose()
}
$sourceImage.Dispose()
$offset = 6+16*$iconSizes.Count
for ($i=0; $i -lt $iconSizes.Count; $i++) {
    $size = $iconSizes[$i]; $dim = if ($size -eq 256) {0} else {$size}
    $iconWriter.Write([byte]$dim); $iconWriter.Write([byte]$dim); $iconWriter.Write([byte]0); $iconWriter.Write([byte]0)
    $iconWriter.Write([uint16]1); $iconWriter.Write([uint16]32)
    $iconWriter.Write([uint32]$pngs[$i].Length); $iconWriter.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($png in $pngs) { $iconWriter.Write([byte[]]$png) }
[System.IO.File]::WriteAllBytes($iconPath,$iconStream.ToArray())
$iconWriter.Dispose(); $iconStream.Dispose()
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'Windows .NET Framework 4.8 (64-bit) is required.' }
$refs = @('System.dll','System.Core.dll','System.Xaml.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Runtime.Serialization.dll','WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll')
$compilerArgs = @('/nologo','/target:winexe','/platform:anycpu','/optimize+','/codepage:65001',('/out:'+(Join-Path $outDir 'ShaiTu.exe')),('/win32manifest:'+(Join-Path $taskRoot 'src\app.manifest')),('/win32icon:'+$iconPath))
$compilerArgs += '/resource:'+$iconPath+',ShaiTu.AppIcon.ico'
foreach ($ref in $refs) { $compilerArgs += '/reference:'+(Join-Path $framework $ref) }
$compilerArgs += @(Get-ChildItem (Join-Path $taskRoot 'src') -Filter '*.cs' | ForEach-Object FullName)
& $compiler @compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
Copy-Item -LiteralPath (Join-Path $taskRoot 'src\App.config') -Destination (Join-Path $outDir 'ShaiTu.exe.config')
Write-Output "Built: $outDir\ShaiTu.exe"
if ($Test) {
    $testDir = Join-Path $taskRoot 'artifacts'
    New-Item -ItemType Directory -Force -Path $testDir | Out-Null
    $proc = Start-Process -FilePath (Join-Path $outDir 'ShaiTu.exe') -ArgumentList @('--self-test',('"'+$testDir+'"')) -WindowStyle Hidden -PassThru -Wait
    Get-Content -LiteralPath (Join-Path $testDir 'test-results.txt')
    if ($proc.ExitCode -ne 0) { throw "Validation failed, exit code $($proc.ExitCode)" }
}
