# ============================================================================
# NillyZip Pro — Windows 10/11 Installer (Zero Admin · Portable · HKCU)
# created by Nilly graphics (Sotiris Christos) — https://nillygraphics.com/
# ============================================================================
param(
    [switch]$Silent,
    [switch]$OpenDefaultApps
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$exePath = Join-Path $root 'NillyZipPro.exe'
$icoPath = Join-Path $root 'NillyZipPro.ico'
$srcPath = Join-Path $root 'desktop\NillyZipPro.cs'

Write-Host ''
Write-Host '  ============================================================' -ForegroundColor Cyan
Write-Host '   NillyZip Pro — Εγκατάσταση & Σύνδεση με ZIP / RAR / 7Z / TAR' -ForegroundColor White
Write-Host '   created by Nilly graphics (Sotiris Christos)' -ForegroundColor DarkGray
Write-Host '   https://nillygraphics.com/' -ForegroundColor DarkGray
Write-Host '  ============================================================' -ForegroundColor Cyan
Write-Host ''

# 1. Unblock files if extracted from GitHub Portable ZIP
Get-ChildItem -Path $root -Recurse -File -ErrorAction SilentlyContinue | Unblock-File -ErrorAction SilentlyContinue

# 2. Auto-compile NillyZipPro.exe if missing or older than source
$needBuild = (-not (Test-Path $exePath)) -or ((Test-Path $srcPath) -and ((Get-Item $srcPath).LastWriteTimeUtc -gt (Get-Item $exePath).LastWriteTimeUtc))
if ($needBuild) {
    $csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (-not (Test-Path $csc)) { $csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' }
    if (-not (Test-Path $csc)) { throw 'Δεν βρέθηκε ο .NET Framework 4.x C# compiler (csc.exe).' }
    Write-Host '  [1/3] Μεταγλώττιση NillyZipPro.exe (Native Windows Archive Manager)...' -ForegroundColor Yellow
    $cscArgs = @(
        '/nologo', '/target:winexe', '/optimize+', '/utf8output', '/platform:anycpu', "/out:$exePath",
        '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll',
        '/r:System.IO.Compression.dll', '/r:System.IO.Compression.FileSystem.dll'
    )
    if (Test-Path $icoPath) { $cscArgs += "/win32icon:$icoPath" }
    $cscArgs += $srcPath
    & $csc @cscArgs
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $exePath)) {
        throw "Αποτυχία μεταγλώττισης NillyZipPro.exe (exit $LASTEXITCODE)"
    }
    Write-Host ("        OK -> " + [math]::Round((Get-Item $exePath).Length / 1KB, 1) + " KB") -ForegroundColor Green
} else {
    Write-Host '  [1/3] NillyZipPro.exe έτοιμο.' -ForegroundColor Green
}

# 3. Register NillyZip Pro in HKCU (Zero Admin!) + Explorer Context Menu
Write-Host '  [2/3] Εγγραφή του NillyZip Pro στα Windows (ZIP, RAR, 7Z, TAR, GZ, XZ, ZST)...' -ForegroundColor Yellow
$iconRef = if (Test-Path $icoPath) { "$icoPath,0" } else { "$exePath,0" }
$openCmd = "`"$exePath`" `"%1`""
$extHereCmd = "`"$exePath`" --extract-here `"%1`""
$compCmd = "`"$exePath`" --compress `"%1`""

$progKey = 'HKCU:\Software\Classes\NillyZipPro.Archive'
New-Item -Path "$progKey\DefaultIcon" -Force | Out-Null
New-Item -Path "$progKey\shell\open\command" -Force | Out-Null
New-Item -Path "$progKey\shell\NillyExtractHere\command" -Force | Out-Null
Set-ItemProperty -Path $progKey -Name '(Default)' -Value 'NillyZip Pro Archive'
Set-ItemProperty -Path "$progKey\DefaultIcon" -Name '(Default)' -Value $iconRef
Set-ItemProperty -Path "$progKey\shell\open" -Name 'FriendlyAppName' -Value 'NillyZip Pro'
Set-ItemProperty -Path "$progKey\shell\open\command" -Name '(Default)' -Value $openCmd
Set-ItemProperty -Path "$progKey\shell\NillyExtractHere" -Name '(Default)' -Value 'Εξαγωγή εδώ (NillyZip Pro)'
Set-ItemProperty -Path "$progKey\shell\NillyExtractHere" -Name 'Icon' -Value $iconRef
Set-ItemProperty -Path "$progKey\shell\NillyExtractHere\command" -Name '(Default)' -Value $extHereCmd

$appKey = 'HKCU:\Software\Classes\Applications\NillyZipPro.exe'
New-Item -Path "$appKey\DefaultIcon" -Force | Out-Null
New-Item -Path "$appKey\shell\open\command" -Force | Out-Null
New-Item -Path "$appKey\SupportedTypes" -Force | Out-Null
Set-ItemProperty -Path $appKey -Name 'FriendlyAppName' -Value 'NillyZip Pro'
Set-ItemProperty -Path "$appKey\DefaultIcon" -Name '(Default)' -Value $iconRef
Set-ItemProperty -Path "$appKey\shell\open\command" -Name '(Default)' -Value $openCmd

$capKey = 'HKCU:\Software\NillyGraphics\NillyZipPro\Capabilities'
New-Item -Path "$capKey\FileAssociations" -Force | Out-Null
Set-ItemProperty -Path $capKey -Name 'ApplicationName' -Value 'NillyZip Pro'
Set-ItemProperty -Path $capKey -Name 'ApplicationDescription' -Value 'Ultra-Light Native Archive Manager — created by Nilly graphics (Sotiris Christos) — https://nillygraphics.com/'

$exts = @('.zip', '.7z', '.rar', '.tar', '.gz', '.tgz', '.xz', '.txz', '.bz2', '.zst', '.cab')
foreach ($e in $exts) {
    $ow = "HKCU:\Software\Classes\$e\OpenWithProgids"
    New-Item -Path $ow -Force | Out-Null
    Set-ItemProperty -Path $ow -Name 'NillyZipPro.Archive' -Value ([byte[]]@()) -Type Binary
    Set-ItemProperty -Path "$appKey\SupportedTypes" -Name $e -Value ''
    Set-ItemProperty -Path "$capKey\FileAssociations" -Name $e -Value 'NillyZipPro.Archive'
    # Add "Εξαγωγή εδώ (NillyZip Pro)" right-click on archive extensions
    $exCtx = "HKCU:\Software\Classes\SystemFileAssociations\$e\shell\NillyZipExtract"
    New-Item -Path "$exCtx\command" -Force | Out-Null
    Set-ItemProperty -Path $exCtx -Name '(Default)' -Value 'Εξαγωγή εδώ (NillyZip Pro)'
    Set-ItemProperty -Path $exCtx -Name 'Icon' -Value $iconRef
    Set-ItemProperty -Path "$exCtx\command" -Name '(Default)' -Value $extHereCmd
}

# Right-click "Συμπίεση σε .ZIP (NillyZip Pro)" on files & folders (use reg.exe so literal '*' never expands as wildcard!)
foreach ($cls in @('*', 'Directory')) {
    $rKey = "HKCU\Software\Classes\$cls\shell\NillyZipCompress"
    & reg.exe add $rKey /ve /d "Συμπίεση σε .ZIP (NillyZip Pro)" /f | Out-Null
    & reg.exe add $rKey /v "Icon" /d "$iconRef" /f | Out-Null
    & reg.exe add "$rKey\command" /ve /d "$compCmd" /f | Out-Null
}

New-Item -Path 'HKCU:\Software\RegisteredApplications' -Force | Out-Null
Set-ItemProperty -Path 'HKCU:\Software\RegisteredApplications' -Name 'NillyZipPro' -Value 'Software\NillyGraphics\NillyZipPro\Capabilities'

try {
    Add-Type -Namespace NillyZipShell -Name Notify -MemberDefinition '[System.Runtime.InteropServices.DllImport("shell32.dll")] public static extern void SHChangeNotify(int wEventId, int uFlags, System.IntPtr dwItem1, System.IntPtr dwItem2);' -ErrorAction SilentlyContinue
    [NillyZipShell.Notify]::SHChangeNotify(0x08000000, 0x1000, [IntPtr]::Zero, [IntPtr]::Zero)
} catch {}

# 4. Create Desktop & Start Menu Shortcuts
Write-Host '  [3/3] Δημιουργία συντομεύσεων (Επιφάνεια Εργασίας & Start Menu)...' -ForegroundColor Yellow
$wsh = New-Object -ComObject WScript.Shell
$desktopDir = [Environment]::GetFolderPath('Desktop')
$startMenuDir = Join-Path ([Environment]::GetFolderPath('Programs')) 'Nilly graphics'
New-Item -ItemType Directory -Force -Path $startMenuDir | Out-Null

foreach ($lnkPath in @(
    (Join-Path $desktopDir 'NillyZip Pro.lnk'),
    (Join-Path $startMenuDir 'NillyZip Pro.lnk')
)) {
    $sc = $wsh.CreateShortcut($lnkPath)
    $sc.TargetPath = $exePath
    $sc.WorkingDirectory = $root
    $sc.Description = 'NillyZip Pro — Ultra-Light Archive Manager | created by Nilly graphics (Sotiris Christos) — https://nillygraphics.com/'
    if (Test-Path $icoPath) { $sc.IconLocation = "$icoPath,0" }
    $sc.Save()
}
Write-Host '        OK -> Έτοιμο το NillyZip Pro!' -ForegroundColor Green
if ($OpenDefaultApps) {
    Start-Process 'ms-settings:defaultapps?registeredAppUser=NillyZipPro'
}
