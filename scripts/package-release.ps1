param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$InputDirectory = '',
    [string]$OutputDirectory = ''
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must use major.minor.patch.' }
$taskRoot = Split-Path -Parent $PSScriptRoot
if (!$InputDirectory) { $InputDirectory = Join-Path $taskRoot 'dist' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $taskRoot 'release' }
$InputDirectory = [IO.Path]::GetFullPath($InputDirectory)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$taskExe = Join-Path $InputDirectory 'ShaiTu.exe'
$taskConfig = Join-Path $InputDirectory 'ShaiTu.exe.config'
$taskLicense = Join-Path $taskRoot 'src\LICENSE'
foreach ($taskFile in @($taskExe, $taskConfig, $taskLicense)) {
    if (!(Test-Path -LiteralPath $taskFile -PathType Leaf)) { throw "Required file missing: $taskFile" }
}
if (([Version](Get-Item -LiteralPath $taskExe).VersionInfo.FileVersion).ToString(3) -ne $Version) {
    throw 'Executable version does not match the release version.'
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$taskZipName = 'ShaiTu-Windows-v' + $Version + '-portable.zip'
$taskZipPath = Join-Path $OutputDirectory $taskZipName
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$taskStream = [IO.File]::Open($taskZipPath, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
$taskZip = [IO.Compression.ZipArchive]::new($taskStream, [IO.Compression.ZipArchiveMode]::Create, $false)
try {
    foreach ($taskPair in @(@($taskExe, 'ShaiTu.exe'), @($taskConfig, 'ShaiTu.exe.config'), @($taskLicense, 'LICENSE.txt'))) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($taskZip, $taskPair[0], $taskPair[1], [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
    $taskEntry = $taskZip.CreateEntry('START-HERE.txt')
    $taskWriter = [IO.StreamWriter]::new($taskEntry.Open(), [Text.UTF8Encoding]::new($true))
    try {
        $taskWriter.WriteLine('ShaiTu ' + $Version)
        $taskWriter.WriteLine('Requirements: Windows 10 / 11, .NET Framework 4.8, Snipaste for automatic pin reception.')
        $taskWriter.WriteLine('Extract the entire ZIP to a permanent folder, then run ShaiTu.exe. Keep ShaiTu.exe.config beside it.')
        $taskWriter.WriteLine('The app starts in the system tray. Capture with your existing Snipaste shortcut, then pin the image.')
        $taskWriter.WriteLine('Default show/hide shortcut: Ctrl + Alt + T; customize it in the tray menu.')
        $taskWriter.WriteLine('Snipaste desktop pins remain available for dragging and comparison.')
        $taskWriter.WriteLine('Dropping a photo outside the clothesline removes its ShaiTu cache. Tray exit clears ShaiTu screenshots.')
        $taskWriter.WriteLine('Snipaste originals and explicitly saved images remain untouched.')
        $taskWriter.WriteLine('Optional startup: enable it in the tray menu; the app opens 15 seconds after Windows logon.')
        $taskWriter.WriteLine('Source and Chinese guide: https://github.com/weidaozhong/ShaiTu')
    } finally { $taskWriter.Dispose() }
} finally { $taskZip.Dispose() }
$taskHash = (Get-FileHash -LiteralPath $taskZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'SHA256SUMS.txt'), $taskHash + '  ' + $taskZipName + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
Write-Output "Packaged: $taskZipPath"
