# ============================================================================
# NillyZip Pro — Clean Uninstaller (Zero Admin · HKCU only)
# created by Nilly graphics (Sotiris Christos) — https://nillygraphics.com/
# ============================================================================
param([switch]$Silent)

$ErrorActionPreference = 'SilentlyContinue'

foreach ($k in @(
    'HKCU:\Software\Classes\NillyZipPro.Archive',
    'HKCU:\Software\Classes\Applications\NillyZipPro.exe',
    'HKCU:\Software\NillyGraphics\NillyZipPro',
    'HKCU:\Software\Classes\Directory\shell\NillyZipCompress'
)) {
    if (Test-Path $k) { Remove-Item -Path $k -Recurse -Force }
}
& reg.exe delete "HKCU\Software\Classes\*\shell\NillyZipCompress" /f 2>$null | Out-Null

$exts = @('.zip', '.7z', '.rar', '.tar', '.gz', '.tgz', '.xz', '.txz', '.bz2', '.zst', '.cab')
foreach ($e in $exts) {
    Remove-ItemProperty -Path "HKCU:\Software\Classes\$e\OpenWithProgids" -Name 'NillyZipPro.Archive' -Force
    $exCtx = "HKCU:\Software\Classes\SystemFileAssociations\$e\shell\NillyZipExtract"
    if (Test-Path $exCtx) { Remove-Item -Path $exCtx -Recurse -Force }
}
Remove-ItemProperty -Path 'HKCU:\Software\RegisteredApplications' -Name 'NillyZipPro' -Force

Remove-Item (Join-Path ([Environment]::GetFolderPath('Desktop')) 'NillyZip Pro.lnk') -Force
Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'Nilly graphics\NillyZip Pro.lnk') -Force

try {
    Add-Type -Namespace NillyZipUnShell -Name Notify -MemberDefinition '[System.Runtime.InteropServices.DllImport("shell32.dll")] public static extern void SHChangeNotify(int wEventId, int uFlags, System.IntPtr dwItem1, System.IntPtr dwItem2);' -ErrorAction SilentlyContinue
    [NillyZipUnShell.Notify]::SHChangeNotify(0x08000000, 0x1000, [IntPtr]::Zero, [IntPtr]::Zero)
} catch {}

if (-not $Silent) { Write-Host '  Το NillyZip Pro απεγκαταστάθηκε καθαρά από τα Windows.' -ForegroundColor Green }
