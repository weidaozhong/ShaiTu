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
$taskLicense = Join-Path $taskRoot 'LICENSE'
$taskNotice = Join-Path $taskRoot 'NOTICE'
$taskThirdParty = Join-Path $taskRoot 'THIRD-PARTY-NOTICES.txt'
foreach ($taskFile in @($taskExe, $taskConfig, $taskLicense, $taskNotice, $taskThirdParty)) {
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
    foreach ($taskPair in @(@($taskExe, 'ShaiTu.exe'), @($taskConfig, 'ShaiTu.exe.config'), @($taskLicense, 'LICENSE.txt'), @($taskNotice, 'NOTICE.txt'), @($taskThirdParty, 'THIRD-PARTY-NOTICES.txt'))) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($taskZip, $taskPair[0], $taskPair[1], [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
    $taskEntry = $taskZip.CreateEntry('START-HERE.txt')
    $taskWriter = [IO.StreamWriter]::new($taskEntry.Open(), [Text.UTF8Encoding]::new($true))
    try {
        $taskWriter.WriteLine('ShaiTu ' + $Version)
        $taskWriter.WriteLine('License: Apache License 2.0. See LICENSE.txt, NOTICE.txt and THIRD-PARTY-NOTICES.txt.')
        $taskWriter.WriteLine('Requirements: 64-bit Windows 10 1903+ / Windows 11, .NET Framework 4.8. Snipaste is optional.')
        $taskWriter.WriteLine('Extract the entire ZIP to a permanent folder, then run ShaiTu.exe. Keep ShaiTu.exe.config beside it.')
        $taskWriter.WriteLine('Extract and double-click ShaiTu.exe: no Snipaste or OCR path configuration is required.')
        $taskWriter.WriteLine('A first-run welcome window appears; later the app stays in the system tray.')
        $taskWriter.WriteLine('The app starts in the system tray. Capture with your existing Snipaste shortcut, then pin the image.')
        $taskWriter.WriteLine('Text recognition: right-click a photo, use the photo menu, or choose Capture and recognize text in the tray.')
        $taskWriter.WriteLine('OCR runs locally with Windows OCR or the oneocr components installed by the Windows 11 Snipping Tool.')
        $taskWriter.WriteLine('No OCR model or Microsoft OCR binary is bundled or downloaded by this ZIP.')
        $taskWriter.WriteLine('Snipaste is optional. When it is not running, choose Windows Capture in the tray to open Win+Shift+S.')
        $taskWriter.WriteLine('Windows capture receives one screenshot for this request, without enabling permanent clipboard reception.')
        $taskWriter.WriteLine('The Windows Snipping Tool must be available and copy the image to the clipboard. Esc cancels; the request expires after 2 minutes.')
        $taskWriter.WriteLine('Default show/hide shortcut: Ctrl + Alt + T; customize it in the tray menu.')
        $taskWriter.WriteLine('Snipaste desktop pins remain available for dragging and comparison.')
        $taskWriter.WriteLine('New photos enter from the left. At eight photos, the current rightmost photo is removed when another arrives.')
        $taskWriter.WriteLine('Dropping a photo outside the clothesline removes its ShaiTu cache. Tray exit clears ShaiTu screenshots.')
        $taskWriter.WriteLine('Snipaste originals and explicitly saved images remain untouched.')
        $taskWriter.WriteLine('With photos present, the right-edge handle stays visible: click it to expand or collapse; hovering reveals when hidden.')
        $taskWriter.WriteLine('The clothesline auto-hides 0.6 seconds after the pointer leaves it.')
        $taskWriter.WriteLine('Startup is enabled on first run; the app opens 15 seconds after Windows logon. Disable it in the tray if unwanted.')
        $taskWriter.WriteLine('Source and Chinese guide: https://github.com/weidaozhong/ShaiTu')
    } finally { $taskWriter.Dispose() }
} finally { $taskZip.Dispose() }
$taskHash = (Get-FileHash -LiteralPath $taskZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'SHA256SUMS.txt'), $taskHash + '  ' + $taskZipName + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
Write-Output "Packaged: $taskZipPath"
