// ============================================================================
// NillyZip Pro v1.2.2 — Ultra-Light Native Archive Manager for Windows 10/11
// created by Nilly graphics (Sotiris Christos) — https://nillygraphics.com/
// Strictly C# 5 compatible (.NET Framework 4.x csc.exe — 0 external DLLs)
// Native ZIP + Windows 11 libarchive (RAR, 7Z, TAR, GZ, XZ, BZ2, ZST, CAB)
// ============================================================================

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("NillyZip Pro")]
[assembly: AssemblyDescription("Ultra-Light Native Archive Manager (ZIP, RAR, 7Z, TAR, GZ, XZ, ZST) — created by Nilly graphics (Sotiris Christos)")]
[assembly: AssemblyCompany("Nilly graphics (Sotiris Christos) — https://nillygraphics.com/")]
[assembly: AssemblyProduct("NillyZip Pro")]
[assembly: AssemblyCopyright("Copyright (c) 2026 Nilly graphics (Sotiris Christos) — https://nillygraphics.com/")]
[assembly: AssemblyTrademark("Nilly graphics / Nillygraphics — Sotiris Christos")]
[assembly: AssemblyVersion("1.2.2.0")]
[assembly: AssemblyFileVersion("1.2.2.0")]

namespace NillyZipPro
{
    internal static class Native
    {
        private const int EM_SETCUEBANNER = 0x1501;

        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int sz);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr minSize, IntPtr maxSize);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        public static void SetDarkTitlebar(IntPtr hwnd, bool dark)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                int v = dark ? 1 : 0;
                if (DwmSetWindowAttribute(hwnd, 20, ref v, 4) != 0)
                    DwmSetWindowAttribute(hwnd, 19, ref v, 4);
            }
            catch { }
        }

        public static void SetCueBanner(TextBox tb, string text)
        {
            if (tb == null || !tb.IsHandleCreated) return;
            try { SendMessage(tb.Handle, EM_SETCUEBANNER, (IntPtr)1, text); } catch { }
        }

        public static void TrimWorkingSet()
        {
            try
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, false);
                SetProcessWorkingSetSize(GetCurrentProcess(), (IntPtr)(-1), (IntPtr)(-1));
            }
            catch { }
        }
    }

    internal sealed class ArchiveEntryItem
    {
        public string FullPath;       // normalized with '/'
        public string RawPath;        // original path as reported by tar/zip
        public string Name;
        public bool IsDir;
        public long UncompressedSize;
        public long CompressedSize;
        public DateTime Modified;
    }

    // ========================================================================
    // DUAL ARCHIVE ENGINE (Native .NET ZipArchive + Windows 11 bsdtar/libarchive)
    // ========================================================================
    internal static class ArchiveEngine
    {
        private static readonly string[] ArchiveExts = new string[]
        {
            ".zip", ".7z", ".rar", ".tar", ".tgz", ".gz", ".txz", ".xz", ".bz2", ".tbz2", ".zst", ".tzst", ".cab", ".iso"
        };

        public static string TarExePath
        {
            get
            {
                string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");
                return File.Exists(p) ? p : null;
            }
        }

        public static bool IsSupportedArchive(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string low = path.ToLowerInvariant();
            for (int i = 0; i < ArchiveExts.Length; i++)
                if (low.EndsWith(ArchiveExts[i])) return true;
            return false;
        }

        public static bool IsNativeZip(string path)
        {
            return !string.IsNullOrEmpty(path) && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        }

        public static string NormalizeRelPath(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string s = raw.Replace('\\', '/').Trim();
            while (s.StartsWith("./")) s = s.Substring(2);
            while (s.StartsWith("/")) s = s.Substring(1);
            return s;
        }

        // Strict Zip-Slip guard: blocks .. traversal, drive letters, rooted paths
        public static bool IsSafeRelativePath(string rel)
        {
            if (string.IsNullOrEmpty(rel)) return false;
            string s = rel.Replace('\\', '/');
            if (s.StartsWith("/") || s.IndexOf(':') >= 0) return false;
            string[] segs = s.Split('/');
            for (int i = 0; i < segs.Length; i++)
                if (segs[i] == "..") return false;
            return true;
        }

        public static string SafeCombine(string rootDir, string relPath)
        {
            if (!IsSafeRelativePath(relPath))
                throw new InvalidDataException("Μη ασφαλής διαδρομή (Zip-Slip blocked): " + relPath);
            string fullRoot = Path.GetFullPath(rootDir).TrimEnd('\\') + "\\";
            string dest = Path.GetFullPath(Path.Combine(rootDir, relPath.Replace('/', '\\')));
            if (!dest.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Μη ασφαλής διαδρομή εξαγωγής: " + relPath);
            return dest;
        }

        public static List<ArchiveEntryItem> ListEntries(string archivePath)
        {
            if (IsNativeZip(archivePath)) return ListZipEntries(archivePath);
            return ListTarEntries(archivePath);
        }

        private static List<ArchiveEntryItem> ListZipEntries(string zipPath)
        {
            List<ArchiveEntryItem> list = new List<ArchiveEntryItem>();
            using (ZipArchive arch = ZipFile.OpenRead(zipPath))
            {
                for (int i = 0; i < arch.Entries.Count; i++)
                {
                    ZipArchiveEntry e = arch.Entries[i];
                    string norm = NormalizeRelPath(e.FullName);
                    if (string.IsNullOrEmpty(norm)) continue;
                    bool isDir = norm.EndsWith("/");
                    string clean = norm.TrimEnd('/');
                    ArchiveEntryItem item = new ArchiveEntryItem();
                    item.FullPath = clean;
                    int sl = clean.LastIndexOf('/');
                    item.Name = sl >= 0 ? clean.Substring(sl + 1) : clean;
                    item.IsDir = isDir;
                    item.UncompressedSize = e.Length;
                    item.CompressedSize = e.CompressedLength;
                    item.Modified = e.LastWriteTime.LocalDateTime;
                    list.Add(item);
                }
            }
            return list;
        }

        private static List<ArchiveEntryItem> ListTarEntries(string archivePath)
        {
            string tar = TarExePath;
            if (tar == null) throw new NotSupportedException("Δεν βρέθηκε το tar.exe των Windows 11.");

            List<ArchiveEntryItem> list = new List<ArchiveEntryItem>();
            string stdout, stderr;
            int code = RunProcess(tar, "-tvf \"" + archivePath + "\"", Path.GetDirectoryName(archivePath), out stdout, out stderr);
            if (code != 0 && string.IsNullOrWhiteSpace(stdout))
                throw new InvalidDataException("Αδυναμία ανάγνωσης αρχείου:\r\n" + stderr.Trim());

            string[] lines = stdout.Split(new string[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            // bsdtar -tvf format: "drwxr-xr-x  0 user group       0 Nov 24 22:30 path/to/file"
            Regex rx = new Regex(@"^([\-dlcbps][rwxstST\-]{9})\s+\d+\s+\S+\s+\S+\s+(\d+)\s+([A-Za-z]{3}\s+\d+\s+[\d:]+|\d{4}-\d{2}-\d{2}\s+[\d:]+)\s+(.+)$");
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd();
                if (string.IsNullOrWhiteSpace(line)) continue;
                Match m = rx.Match(line);
                if (m.Success)
                {
                    string mode = m.Groups[1].Value;
                    long sz = 0;
                    long.TryParse(m.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out sz);
                    string origPath = m.Groups[4].Value.Trim();
                    string rawPath = NormalizeRelPath(origPath);
                    if (string.IsNullOrEmpty(rawPath)) continue;
                    bool isDir = mode.StartsWith("d") || rawPath.EndsWith("/");
                    string clean = rawPath.TrimEnd('/');
                    ArchiveEntryItem it = new ArchiveEntryItem();
                    it.FullPath = clean;
                    it.RawPath = origPath;
                    int sl = clean.LastIndexOf('/');
                    it.Name = sl >= 0 ? clean.Substring(sl + 1) : clean;
                    it.IsDir = isDir;
                    it.UncompressedSize = sz;
                    it.CompressedSize = -1;
                    it.Modified = DateTime.MinValue;
                    list.Add(it);
                }
                else
                {
                    string origPath = line.Trim();
                    string rawPath = NormalizeRelPath(origPath);
                    if (string.IsNullOrEmpty(rawPath)) continue;
                    bool isDir = rawPath.EndsWith("/");
                    string clean = rawPath.TrimEnd('/');
                    ArchiveEntryItem it = new ArchiveEntryItem();
                    it.FullPath = clean;
                    it.RawPath = origPath;
                    int sl = clean.LastIndexOf('/');
                    it.Name = sl >= 0 ? clean.Substring(sl + 1) : clean;
                    it.IsDir = isDir;
                    it.UncompressedSize = 0;
                    it.CompressedSize = -1;
                    it.Modified = DateTime.MinValue;
                    list.Add(it);
                }
            }
            return list;
        }

        // Smart target folder: if archive has >1 root item, wraps neatly inside "<ArchiveName>\"!
        public static string ResolveSmartExtractDir(string archivePath, string baseParentDir, List<ArchiveEntryItem> entries)
        {
            HashSet<string> roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < entries.Count; i++)
            {
                string fp = entries[i].FullPath;
                int sl = fp.IndexOf('/');
                roots.Add(sl > 0 ? fp.Substring(0, sl) : fp);
                if (roots.Count > 1) break;
            }
            if (roots.Count == 1) return baseParentDir;
            string stem = StripArchiveExts(Path.GetFileName(archivePath));
            return Path.Combine(baseParentDir, stem);
        }

        public static string StripArchiveExts(string fileName)
        {
            string s = fileName ?? "archive";
            string[] multi = new string[] { ".tar.gz", ".tar.xz", ".tar.bz2", ".tar.zst" };
            for (int i = 0; i < multi.Length; i++)
                if (s.EndsWith(multi[i], StringComparison.OrdinalIgnoreCase))
                    return s.Substring(0, s.Length - multi[i].Length);
            return Path.GetFileNameWithoutExtension(s);
        }

        public static int ExtractArchive(string archivePath, string destDir, List<string> onlyRelPaths, Action<int, int, string> onProgress)
        {
            Directory.CreateDirectory(destDir);
            if (IsNativeZip(archivePath))
            {
                int extracted = 0;
                using (ZipArchive arch = ZipFile.OpenRead(archivePath))
                {
                    int total = arch.Entries.Count;
                    for (int i = 0; i < total; i++)
                    {
                        ZipArchiveEntry e = arch.Entries[i];
                        string norm = NormalizeRelPath(e.FullName);
                        if (string.IsNullOrEmpty(norm)) continue;
                        string clean = norm.TrimEnd('/');
                        if (onlyRelPaths != null && onlyRelPaths.Count > 0 && !MatchesAnyPrefix(clean, onlyRelPaths))
                            continue;
                        if (!IsSafeRelativePath(clean))
                            throw new InvalidDataException("Απορρίφθηκε μη ασφαλής διαδρομή (Zip-Slip): " + norm);
                        string target = SafeCombine(destDir, clean);
                        if (norm.EndsWith("/"))
                        {
                            Directory.CreateDirectory(target);
                        }
                        else
                        {
                            string parent = Path.GetDirectoryName(target);
                            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                            e.ExtractToFile(target, true);
                            extracted++;
                        }
                        if (onProgress != null && (i % 8 == 0 || i == total - 1))
                            onProgress(i + 1, total, clean);
                    }
                }
                return extracted;
            }
            else
            {
                // Validate ALL paths before calling tar.exe (strict Zip-Slip defense!)
                List<ArchiveEntryItem> all = ListTarEntries(archivePath);
                for (int i = 0; i < all.Count; i++)
                    if (!IsSafeRelativePath(all[i].FullPath))
                        throw new InvalidDataException("Απορρίφθηκε μη ασφαλής διαδρομή (Zip-Slip): " + all[i].FullPath);

                string tar = TarExePath;
                if (tar == null) throw new NotSupportedException("Δεν βρέθηκε το tar.exe των Windows 11.");

                StringBuilder args = new StringBuilder();
                args.Append("-xf \"").Append(archivePath).Append("\" -C \"").Append(destDir).Append("\"");
                if (onlyRelPaths != null && onlyRelPaths.Count > 0)
                {
                    bool hasDotSlash = false;
                    for (int i = 0; i < all.Count; i++)
                    {
                        if (!string.IsNullOrEmpty(all[i].RawPath) && all[i].RawPath.StartsWith("./"))
                        { hasDotSlash = true; break; }
                    }
                    for (int i = 0; i < onlyRelPaths.Count; i++)
                    {
                        string pArg = onlyRelPaths[i].TrimEnd('/');
                        if (hasDotSlash && !pArg.StartsWith("./")) pArg = "./" + pArg;
                        args.Append(" \"").Append(pArg).Append("\"");
                    }
                }
                if (onProgress != null) onProgress(1, 2, Path.GetFileName(archivePath));
                string stdout, stderr;
                int code = RunProcess(tar, args.ToString(), destDir, out stdout, out stderr);
                if (code != 0) throw new IOException("Σφάλμα αποσυμπίεσης:\r\n" + stderr.Trim());
                if (onProgress != null) onProgress(2, 2, "Ολοκληρώθηκε");
                return onlyRelPaths != null && onlyRelPaths.Count > 0 ? onlyRelPaths.Count : all.Count;
            }
        }

        public static string DragTempRoot
        {
            get { return Path.Combine(Path.GetTempPath(), "NillyZipDrag"); }
        }

        public static void CleanupOldDragDirs(int olderThanMinutes)
        {
            try
            {
                string root = DragTempRoot;
                if (!Directory.Exists(root)) return;
                DateTime cutoff = DateTime.UtcNow.AddMinutes(-olderThanMinutes);
                string[] dirs = Directory.GetDirectories(root);
                for (int i = 0; i < dirs.Length; i++)
                {
                    try
                    {
                        if (Directory.GetLastWriteTimeUtc(dirs[i]) < cutoff)
                            Directory.Delete(dirs[i], true);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static string UniqueFlatFileName(string stageDir, string fileName)
        {
            string safeName = Path.GetFileName(fileName);
            if (string.IsNullOrEmpty(safeName)) safeName = "file";
            string candidate = safeName;
            string stem = Path.GetFileNameWithoutExtension(safeName);
            string ext = Path.GetExtension(safeName);
            int idx = 2;
            while (File.Exists(Path.Combine(stageDir, candidate)) || Directory.Exists(Path.Combine(stageDir, candidate)))
            {
                candidate = stem + " (" + idx + ")" + ext;
                idx++;
            }
            return candidate;
        }

        // Extracts ONLY selected items relative to currentFolder into stageDir for Drag & Drop OUT / Clipboard Copy
        public static string[] ExtractForDrag(string archivePath, string stageDir, List<string> selectedRelPaths, string currentFolder, bool isSearching)
        {
            if (string.IsNullOrEmpty(archivePath) || !File.Exists(archivePath) || selectedRelPaths == null || selectedRelPaths.Count == 0)
                return new string[0];

            Directory.CreateDirectory(stageDir);
            string basePrefix = (!isSearching && !string.IsNullOrEmpty(currentFolder))
                ? (NormalizeRelPath(currentFolder).TrimEnd('/') + "/")
                : "";
            List<string> topPaths = new List<string>();
            HashSet<string> seenTop = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (IsNativeZip(archivePath))
            {
                using (ZipArchive arch = ZipFile.OpenRead(archivePath))
                {
                    for (int i = 0; i < arch.Entries.Count; i++)
                    {
                        ZipArchiveEntry e = arch.Entries[i];
                        string norm = NormalizeRelPath(e.FullName);
                        if (string.IsNullOrEmpty(norm)) continue;
                        string clean = norm.TrimEnd('/');
                        if (!MatchesAnyPrefix(clean, selectedRelPaths)) continue;
                        if (!IsSafeRelativePath(clean))
                            throw new InvalidDataException("Απορρίφθηκε μη ασφαλής διαδρομή (Zip-Slip): " + norm);

                        string relOut;
                        if (isSearching)
                        {
                            if (norm.EndsWith("/")) continue;
                            relOut = UniqueFlatFileName(stageDir, Path.GetFileName(clean));
                        }
                        else
                        {
                            relOut = (basePrefix.Length > 0 && clean.StartsWith(basePrefix, StringComparison.OrdinalIgnoreCase))
                                ? clean.Substring(basePrefix.Length)
                                : clean;
                            relOut = relOut.TrimStart('/');
                            if (string.IsNullOrEmpty(relOut)) continue;
                        }

                        string target = SafeCombine(stageDir, relOut);
                        if (norm.EndsWith("/"))
                        {
                            Directory.CreateDirectory(target);
                        }
                        else
                        {
                            string parent = Path.GetDirectoryName(target);
                            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                            e.ExtractToFile(target, true);
                        }

                        int sl = relOut.IndexOf('/');
                        string topName = sl > 0 ? relOut.Substring(0, sl) : relOut;
                        string topFull = SafeCombine(stageDir, topName);
                        if ((File.Exists(topFull) || Directory.Exists(topFull)) && seenTop.Add(topFull))
                            topPaths.Add(topFull);
                    }
                }
            }
            else
            {
                string rawDir = Path.Combine(stageDir, "__raw");
                Directory.CreateDirectory(rawDir);
                try
                {
                    ExtractArchive(archivePath, rawDir, selectedRelPaths, null);
                    for (int i = 0; i < selectedRelPaths.Count; i++)
                    {
                        string sel = NormalizeRelPath(selectedRelPaths[i]).TrimEnd('/');
                        if (string.IsNullOrEmpty(sel) || !IsSafeRelativePath(sel)) continue;
                        string src = SafeCombine(rawDir, sel);
                        if (File.Exists(src))
                        {
                            string outRel = isSearching
                                ? UniqueFlatFileName(stageDir, Path.GetFileName(src))
                                : ((basePrefix.Length > 0 && sel.StartsWith(basePrefix, StringComparison.OrdinalIgnoreCase))
                                    ? sel.Substring(basePrefix.Length)
                                    : Path.GetFileName(src));
                            outRel = outRel.TrimStart('/');
                            if (string.IsNullOrEmpty(outRel)) continue;
                            string dst = SafeCombine(stageDir, outRel);
                            string parent = Path.GetDirectoryName(dst);
                            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                            if (File.Exists(dst)) File.Delete(dst);
                            File.Move(src, dst);
                            int sl = outRel.IndexOf('/');
                            string topFull = SafeCombine(stageDir, sl > 0 ? outRel.Substring(0, sl) : outRel);
                            if (seenTop.Add(topFull)) topPaths.Add(topFull);
                        }
                        else if (Directory.Exists(src))
                        {
                            string outRel = (basePrefix.Length > 0 && sel.StartsWith(basePrefix, StringComparison.OrdinalIgnoreCase))
                                ? sel.Substring(basePrefix.Length)
                                : Path.GetFileName(src);
                            outRel = outRel.TrimStart('/');
                            if (string.IsNullOrEmpty(outRel)) continue;
                            string dst = SafeCombine(stageDir, outRel);
                            string parent = Path.GetDirectoryName(dst);
                            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                            if (Directory.Exists(dst)) Directory.Delete(dst, true);
                            Directory.Move(src, dst);
                            int sl = outRel.IndexOf('/');
                            string topFull = SafeCombine(stageDir, sl > 0 ? outRel.Substring(0, sl) : outRel);
                            if (seenTop.Add(topFull)) topPaths.Add(topFull);
                        }
                    }
                }
                finally
                {
                    try { if (Directory.Exists(rawDir)) Directory.Delete(rawDir, true); } catch { }
                }
            }

            return topPaths.ToArray();
        }

        private static bool MatchesAnyPrefix(string cleanPath, List<string> targets)
        {
            for (int i = 0; i < targets.Count; i++)
            {
                string t = targets[i].TrimEnd('/');
                if (cleanPath.Equals(t, StringComparison.OrdinalIgnoreCase) ||
                    cleanPath.StartsWith(t + "/", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        public static void CreateZipFromPaths(string zipOutPath, string[] inputPaths, CompressionLevel level, Action<int, int, string> onProgress)
        {
            List<KeyValuePair<string, string>> pairs = CollectSourceFiles(inputPaths);
            if (pairs.Count == 0) throw new InvalidOperationException("Δεν επιλέχθηκαν αρχεία για συμπίεση.");

            string tmp = zipOutPath + ".nillytmp";
            if (File.Exists(tmp)) File.Delete(tmp);
            try
            {
                using (FileStream fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.ReadWrite))
                using (ZipArchive arch = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    for (int i = 0; i < pairs.Count; i++)
                    {
                        string srcFile = pairs[i].Key;
                        string entryName = pairs[i].Value;
                        arch.CreateEntryFromFile(srcFile, entryName, level);
                        if (onProgress != null && (i % 5 == 0 || i == pairs.Count - 1))
                            onProgress(i + 1, pairs.Count, entryName);
                    }
                }
                if (File.Exists(zipOutPath)) File.Delete(zipOutPath);
                File.Move(tmp, zipOutPath);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                throw;
            }
        }

        public static void CreateMultiFormatArchive(string outPath, string[] inputPaths, CompressionLevel level, Action<int, int, string> onProgress)
        {
            if (IsNativeZip(outPath))
            {
                CreateZipFromPaths(outPath, inputPaths, level, onProgress);
                return;
            }
            string tar = TarExePath;
            if (tar == null) throw new NotSupportedException("Δεν βρέθηκε το tar.exe των Windows 11.");

            // Determine common base directory so paths inside archive stay clean & relative
            string baseDir = Path.GetDirectoryName(Path.GetFullPath(inputPaths[0]));
            StringBuilder args = new StringBuilder();
            args.Append("-acf \"").Append(outPath).Append("\" -C \"").Append(baseDir).Append("\"");
            for (int i = 0; i < inputPaths.Length; i++)
            {
                string full = Path.GetFullPath(inputPaths[i]);
                if (!Path.GetDirectoryName(full).Equals(baseDir, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Για .7z / .tar.gz όλα τα αρχεία πρέπει να είναι στον ίδιο φάκελο.");
                args.Append(" \"").Append(Path.GetFileName(full)).Append("\"");
            }
            if (onProgress != null) onProgress(1, 2, Path.GetFileName(outPath));
            string stdout, stderr;
            int code = RunProcess(tar, args.ToString(), baseDir, out stdout, out stderr);
            if (code != 0)
            {
                bool hasNonAscii = false;
                for (int i = 0; i < inputPaths.Length; i++)
                    for (int c = 0; c < inputPaths[i].Length; c++)
                        if (inputPaths[i][c] > 127) { hasNonAscii = true; break; }
                if (hasNonAscii)
                    throw new IOException("Το tar.exe των Windows δεν υποστηρίζει ελληνικά ονόματα αρχείων στη δημιουργία .7z/.tar σε μη-UTF8 locale.\r\nΔιάλεξε μορφή .ZIP για 100% πλήρη υποστήριξη ελληνικών!");
                throw new IOException("Σφάλμα δημιουργίας αρχείου:\r\n" + stderr.Trim());
            }
            if (onProgress != null) onProgress(2, 2, "Ολοκληρώθηκε");
        }

        public static int AddFilesToZip(string zipPath, string[] inputPaths, string targetPrefix, CompressionLevel level)
        {
            List<KeyValuePair<string, string>> pairs = CollectSourceFiles(inputPaths);
            int added = 0;
            string pref = string.IsNullOrEmpty(targetPrefix) ? "" : (targetPrefix.Trim('/') + "/");
            using (ZipArchive arch = ZipFile.Open(zipPath, ZipArchiveMode.Update))
            {
                for (int i = 0; i < pairs.Count; i++)
                {
                    string entryName = pref + pairs[i].Value;
                    ZipArchiveEntry existing = arch.GetEntry(entryName);
                    if (existing != null) existing.Delete();
                    arch.CreateEntryFromFile(pairs[i].Key, entryName, level);
                    added++;
                }
            }
            return added;
        }

        public static int DeleteFromZip(string zipPath, List<string> relTargets)
        {
            int deleted = 0;
            using (ZipArchive arch = ZipFile.Open(zipPath, ZipArchiveMode.Update))
            {
                List<ZipArchiveEntry> toDel = new List<ZipArchiveEntry>();
                for (int i = 0; i < arch.Entries.Count; i++)
                {
                    string clean = NormalizeRelPath(arch.Entries[i].FullName).TrimEnd('/');
                    if (MatchesAnyPrefix(clean, relTargets)) toDel.Add(arch.Entries[i]);
                }
                for (int i = 0; i < toDel.Count; i++) { toDel[i].Delete(); deleted++; }
            }
            return deleted;
        }

        public static byte[] ReadZipEntryBytes(string zipPath, string relPath, int maxBytes)
        {
            if (!IsNativeZip(zipPath)) return null;
            using (ZipArchive arch = ZipFile.OpenRead(zipPath))
            {
                for (int i = 0; i < arch.Entries.Count; i++)
                {
                    ZipArchiveEntry e = arch.Entries[i];
                    if (NormalizeRelPath(e.FullName).TrimEnd('/').Equals(relPath, StringComparison.OrdinalIgnoreCase))
                    {
                        if (e.Length > maxBytes) return null;
                        using (Stream s = e.Open())
                        using (MemoryStream ms = new MemoryStream())
                        {
                            byte[] buf = new byte[16384];
                            int n;
                            while ((n = s.Read(buf, 0, buf.Length)) > 0)
                            {
                                ms.Write(buf, 0, n);
                                if (ms.Length > maxBytes) return null;
                            }
                            return ms.ToArray();
                        }
                    }
                }
            }
            return null;
        }

        public static string TestIntegrityAndSha256(string archivePath, out int verifiedCount)
        {
            verifiedCount = 0;
            if (IsNativeZip(archivePath))
            {
                byte[] buf = new byte[32768];
                using (ZipArchive arch = ZipFile.OpenRead(archivePath))
                {
                    for (int i = 0; i < arch.Entries.Count; i++)
                    {
                        ZipArchiveEntry e = arch.Entries[i];
                        if (e.FullName.EndsWith("/")) continue;
                        using (Stream s = e.Open())
                            while (s.Read(buf, 0, buf.Length) > 0) { }
                        verifiedCount++;
                    }
                }
            }
            else
            {
                verifiedCount = ListTarEntries(archivePath).Count;
            }
            return ComputeFileSha256(archivePath);
        }

        public static string ComputeFileSha256(string filePath)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream fs = File.OpenRead(filePath))
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
        }

        private static List<KeyValuePair<string, string>> CollectSourceFiles(string[] inputPaths)
        {
            List<KeyValuePair<string, string>> list = new List<KeyValuePair<string, string>>();
            for (int i = 0; i < inputPaths.Length; i++)
            {
                string p = inputPaths[i];
                if (File.Exists(p))
                {
                    list.Add(new KeyValuePair<string, string>(p, Path.GetFileName(p)));
                }
                else if (Directory.Exists(p))
                {
                    string rootName = Path.GetFileName(p.TrimEnd('\\', '/'));
                    string fullDir = Path.GetFullPath(p).TrimEnd('\\') + "\\";
                    string[] files = Directory.GetFiles(p, "*", SearchOption.AllDirectories);
                    for (int f = 0; f < files.Length; f++)
                    {
                        string rel = files[f].Substring(fullDir.Length).Replace('\\', '/');
                        list.Add(new KeyValuePair<string, string>(files[f], rootName + "/" + rel));
                    }
                }
            }
            return list;
        }

        public static string FriendlyError(Exception ex)
        {
            if (ex == null) return "Άγνωστο σφάλμα.";
            string raw = (ex.Message ?? "").Trim();
            string low = raw.ToLowerInvariant();
            if (low.Contains("encrypt") || low.Contains("password") || low.Contains("passphrase") ||
                low.Contains("unsupported compression method") || low.Contains("decryption"))
                return "Το αρχείο προστατεύεται με κωδικό πρόσβασης (κρυπτογραφημένο ZIP/RAR/7Z).\r\nΠρος το παρόν δεν υποστηρίζεται αποσυμπίεση αρχείων με κωδικό.";
            if (ex is UnauthorizedAccessException || low.Contains("access to the path") || low.Contains("access is denied"))
                return "Δεν υπάρχει δικαίωμα πρόσβασης σε αυτόν τον φάκελο ή αρχείο.\r\nΔιάλεξε άλλον φάκελο (π.χ. Επιφάνεια Εργασίας ή Έγγραφα).";
            if (ex is PathTooLongException || low.Contains("path too long"))
                return "Η διαδρομή του αρχείου είναι υπερβολικά μεγάλη για τα Windows.\r\nΔοκίμασε εξαγωγή σε πιο σύντομο φάκελο (π.χ. C:\\Temp).";
            if (low.Contains("being used by another process") || low.Contains("sharing violation"))
                return "Το αρχείο χρησιμοποιείται αυτή τη στιγμή από άλλο πρόγραμμα.\r\nΚλείσε το και δοκίμασε ξανά.";
            if (low.Contains("end of central directory") || low.Contains("corrupt") || low.Contains("truncated") || low.Contains("unrecognized archive format"))
                return "Το συμπιεσμένο αρχείο φαίνεται κατεστραμμένο, ημιτελές ή μη υποστηριζόμενης μορφής.";
            return raw;
        }

        private static int RunProcess(string exe, string args, string workDir, out string stdout, out string stderr)
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.WorkingDirectory = string.IsNullOrEmpty(workDir) ? Environment.CurrentDirectory : workDir;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            using (Process p = Process.Start(psi))
            {
                StringBuilder ob = new StringBuilder();
                StringBuilder eb = new StringBuilder();
                p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) ob.AppendLine(e.Data); };
                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) eb.AppendLine(e.Data); };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                p.WaitForExit(180000);
                stdout = ob.ToString();
                stderr = eb.ToString();
                return p.ExitCode;
            }
        }
    }

    internal sealed class DoubleBufferedListView : ListView
    {
        public DoubleBufferedListView()
        {
            DoubleBuffered = true;
        }
    }

    // ========================================================================
    // CREATE ARCHIVE DIALOG (ZIP / 7Z / TAR.GZ + Compression Preset)
    // ========================================================================
    internal sealed class CreateArchiveDialog : Form
    {
        public string OutputPath { get; private set; }
        public CompressionLevel SelectedLevel { get; private set; }

        private TextBox _txtOut;
        private ComboBox _cmbFmt, _cmbLevel;

        public CreateArchiveDialog(string[] sources, bool dark)
        {
            Text = "NillyZip Pro — Δημιουργία Συμπιεσμένου Αρχείου";
            ClientSize = new Size(510, 212);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9f);
            BackColor = dark ? Color.FromArgb(22, 24, 29) : Color.White;
            ForeColor = dark ? Color.FromArgb(240, 244, 250) : Color.FromArgb(22, 24, 30);
            HandleCreated += delegate { Native.SetDarkTitlebar(Handle, dark); };

            string first = sources[0].TrimEnd('\\', '/');
            string parent = Path.GetDirectoryName(Path.GetFullPath(first));
            string stem = sources.Length == 1
                ? (Directory.Exists(first) ? Path.GetFileName(first) : Path.GetFileNameWithoutExtension(first))
                : (Path.GetFileName(parent) + "_archive");
            string defaultZip = Path.Combine(parent, stem + ".zip");

            Label lblInfo = new Label { Left = 18, Top = 14, Width = 474, Height = 20, Text = "Επιλεγμένα στοιχεία: " + sources.Length + " (" + Path.GetFileName(first) + (sources.Length > 1 ? " …" : "") + ")" };
            Label lblPath = new Label { Left = 18, Top = 44, Width = 140, Height = 18, Text = "Αρχείο προορισμού:" };
            _txtOut = new TextBox
            {
                Left = 18, Top = 66, Width = 384, Height = 25, Text = defaultZip, BorderStyle = BorderStyle.FixedSingle,
                BackColor = dark ? Color.FromArgb(30, 33, 40) : Color.White, ForeColor = ForeColor
            };
            Button btnBrowse = new Button
            {
                Left = 408, Top = 65, Width = 84, Height = 26, Text = "Αναζήτηση", FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
                BackColor = dark ? Color.FromArgb(40, 44, 52) : Color.FromArgb(236, 240, 246), ForeColor = ForeColor
            };
            btnBrowse.FlatAppearance.BorderColor = dark ? Color.FromArgb(64, 70, 82) : Color.FromArgb(200, 206, 216);
            btnBrowse.Click += delegate
            {
                using (SaveFileDialog sfd = new SaveFileDialog())
                {
                    sfd.Filter = "ZIP Archive (*.zip)|*.zip|7-Zip Archive (*.7z)|*.7z|TAR.GZ Archive (*.tar.gz)|*.tar.gz|TAR.ZST Archive (*.tar.zst)|*.tar.zst";
                    sfd.FileName = Path.GetFileName(_txtOut.Text);
                    sfd.InitialDirectory = Path.GetDirectoryName(_txtOut.Text);
                    if (sfd.ShowDialog(this) == DialogResult.OK) _txtOut.Text = sfd.FileName;
                }
            };

            Label lblFmt = new Label { Left = 18, Top = 104, Width = 140, Height = 18, Text = "Μορφή:" };
            _cmbFmt = new ComboBox { Left = 18, Top = 124, Width = 220, DropDownStyle = ComboBoxStyle.DropDownList };
            _cmbFmt.Items.AddRange(new object[] { ".zip (Καθολική συμβατότητα)", ".7z (Υψηλή συμπίεση)", ".tar.gz (Web / Linux)", ".tar.zst (Υπεργρήγορη Zstd)" });
            _cmbFmt.SelectedIndex = 0;
            _cmbFmt.SelectedIndexChanged += delegate
            {
                string ext = _cmbFmt.SelectedIndex == 1 ? ".7z" : (_cmbFmt.SelectedIndex == 2 ? ".tar.gz" : (_cmbFmt.SelectedIndex == 3 ? ".tar.zst" : ".zip"));
                string cur = _txtOut.Text.Trim();
                if (!string.IsNullOrEmpty(cur))
                    _txtOut.Text = Path.Combine(Path.GetDirectoryName(cur), ArchiveEngine.StripArchiveExts(Path.GetFileName(cur)) + ext);
            };

            Label lblLvl = new Label { Left = 258, Top = 104, Width = 160, Height = 18, Text = "Επίπεδο συμπίεσης (ZIP):" };
            _cmbLevel = new ComboBox { Left = 258, Top = 124, Width = 234, DropDownStyle = ComboBoxStyle.DropDownList };
            _cmbLevel.Items.AddRange(new object[] { "Optimal (Ισορροπημένο)", "Fastest (Ταχύτερο)", "Store 0% (Χωρίς συμπίεση)" });
            _cmbLevel.SelectedIndex = 0;

            Button btnOk = new Button
            {
                Left = 286, Top = 166, Width = 120, Height = 32, Text = "Συμπίεση τώρα",
                FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
                BackColor = Color.FromArgb(252, 150, 31), ForeColor = Color.FromArgb(14, 18, 24),
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            btnOk.FlatAppearance.BorderSize = 0;
            btnOk.Click += delegate
            {
                OutputPath = _txtOut.Text.Trim();
                SelectedLevel = _cmbLevel.SelectedIndex == 1 ? CompressionLevel.Fastest : (_cmbLevel.SelectedIndex == 2 ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                if (string.IsNullOrEmpty(OutputPath)) return;
                DialogResult = DialogResult.OK;
                Close();
            };

            Button btnCancel = new Button
            {
                Left = 412, Top = 166, Width = 80, Height = 32, Text = "Άκυρο",
                FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
                BackColor = dark ? Color.FromArgb(40, 44, 52) : Color.FromArgb(236, 240, 246), ForeColor = ForeColor
            };
            btnCancel.FlatAppearance.BorderColor = dark ? Color.FromArgb(64, 70, 82) : Color.FromArgb(200, 206, 216);
            btnCancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };

            Controls.AddRange(new Control[] { lblInfo, lblPath, _txtOut, btnBrowse, lblFmt, _cmbFmt, lblLvl, _cmbLevel, btnOk, btnCancel });
            AcceptButton = btnOk;
            CancelButton = btnCancel;
        }
    }

    // ========================================================================
    // MAIN WINDOW — NILLY SUITE DARK / LIGHT STUDIO UI
    // ========================================================================
    internal sealed class MainForm : Form
    {
        private static readonly Font FontBrand = new Font("Segoe UI", 11.2f, FontStyle.Bold);
        private static readonly Font FontSub = new Font("Segoe UI", 7.8f);
        private static readonly Font FontPill = new Font("Segoe UI", 8.4f, FontStyle.Bold);
        private static readonly Font FontUi = new Font("Segoe UI", 9f);
        private static readonly Font FontMono = new Font("Consolas", 9f);
        private static readonly Font FontFooter = new Font("Segoe UI", 8.2f);

        private bool _dark = true;
        private string _currentArchive;
        private List<ArchiveEntryItem> _allEntries = new List<ArchiveEntryItem>();
        private string _currentFolder = ""; // normalized e.g. "docs/images"
        private int _sortCol = 0;
        private bool _sortAsc = true;
        private static readonly string[] ColBaseTitles = new string[] { "Όνομα", "Μέγεθος", "Συμπιεσμένο", "Κέρδος", "Τροποποίηση" };
        private bool _busy = false;
        private bool _internalDrag = false;
        private readonly List<KeyValuePair<string, DateTime>> _dragTempDirs = new List<KeyValuePair<string, DateTime>>();
        private Bitmap _logoBmp;
        private Image _previewImg;

        private Panel _header, _pathBar, _footer, _previewPanel, _emptyPanel;
        private Label _lblTitle, _lblSub, _lblPath, _lblStatus, _lblCredit, _lblPrevTitle, _lblEmptyHead, _lblEmptySub;
        private TextBox _txtSearch, _txtPreview;
        private PictureBox _picPreview;
        private Button _btnOpen, _btnNew, _btnExtract, _btnSmartExt, _btnAdd, _btnDel, _btnTest, _btnTheme, _btnUp, _btnEmptyOpen, _btnEmptyNew;
        private DoubleBufferedListView _list;
        private ContextMenuStrip _ctxList;
        private ToolStripMenuItem _miOpen, _miExtSel, _miCopyFiles, _miDelSel;
        private SplitContainer _split;
        private ProgressBar _prog;

        public MainForm(string initialArchive)
        {
            _dark = Program.ReadSuiteDark(true);
            Text = "NillyZip Pro — Nilly graphics";
            Size = new Size(1060, 660);
            MinimumSize = new Size(760, 460);
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            AllowDrop = true;

            string icoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "NillyZipPro.ico");
            try
            {
                if (File.Exists(icoPath)) Icon = new Icon(icoPath);
                else Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch { }
            _logoBmp = LoadCrispLogo(icoPath, 30);

            BuildUi();
            ApplyTheme();

            DragEnter += OnDragEnterHandler;
            DragDrop += OnDragDropHandler;

            Shown += delegate
            {
                Native.SetCueBanner(_txtSearch, "Αναζήτηση αρχείου… (Ctrl+F)");
                if (!string.IsNullOrEmpty(initialArchive) && File.Exists(initialArchive))
                    OpenArchiveAsync(initialArchive);
                ThreadPool.QueueUserWorkItem(delegate { ArchiveEngine.CleanupOldDragDirs(15); });
                Native.TrimWorkingSet();
            };

            FormClosed += delegate
            {
                // Clean up only completed drag staging folders older than 15s so active Explorer async copies never fail
                DateTime now = DateTime.UtcNow;
                for (int i = 0; i < _dragTempDirs.Count; i++)
                {
                    if ((now - _dragTempDirs[i].Value).TotalSeconds >= 15.0)
                    {
                        try { if (Directory.Exists(_dragTempDirs[i].Key)) Directory.Delete(_dragTempDirs[i].Key, true); } catch { }
                    }
                }
            };
        }

        private static Bitmap LoadCrispLogo(string icoPath, int px)
        {
            try
            {
                if (File.Exists(icoPath))
                {
                    byte[] data = File.ReadAllBytes(icoPath);
                    if (data.Length > 22)
                    {
                        int count = BitConverter.ToUInt16(data, 4);
                        int bestOff = -1, bestLen = 0, bestDim = -1;
                        for (int i = 0; i < count; i++)
                        {
                            int e = 6 + i * 16;
                            if (e + 16 > data.Length) break;
                            int w = data[e] == 0 ? 256 : data[e];
                            int len = BitConverter.ToInt32(data, e + 8);
                            int off = BitConverter.ToInt32(data, e + 12);
                            if (off > 0 && len > 8 && off + len <= data.Length &&
                                data[off] == 0x89 && data[off + 1] == 0x50 && data[off + 2] == 0x4E && data[off + 3] == 0x47 && w > bestDim)
                            { bestDim = w; bestOff = off; bestLen = len; }
                        }
                        if (bestOff >= 0)
                        {
                            using (MemoryStream ms = new MemoryStream(data, bestOff, bestLen, false))
                            using (Image src = Image.FromStream(ms))
                            {
                                Bitmap dst = new Bitmap(px, px);
                                using (Graphics g = Graphics.FromImage(dst))
                                {
                                    g.SmoothingMode = SmoothingMode.HighQuality;
                                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                                    g.DrawImage(src, 0, 0, px, px);
                                }
                                return dst;
                            }
                        }
                    }
                }
            }
            catch { }
            // Crisp fallback vector emblem if run standalone without .ico
            Bitmap bmp = new Bitmap(px, px);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (SolidBrush br = new SolidBrush(Color.FromArgb(28, 31, 38)))
                using (Pen p = new Pen(Color.FromArgb(252, 150, 31), 2f))
                { g.FillRectangle(br, 2, 2, px - 4, px - 4); g.DrawRectangle(p, 2, 2, px - 4, px - 4); }
                using (Pen cp = new Pen(Color.FromArgb(102, 217, 239), 1.8f))
                    g.DrawRectangle(cp, 8, 7, px - 16, px - 14);
                using (SolidBrush ab = new SolidBrush(Color.FromArgb(252, 150, 31)))
                    g.FillRectangle(ab, px / 2 - 2, 10, 4, px - 18);
            }
            return bmp;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.O) { e.SuppressKeyPress = true; BrowseOpenArchive(); return; }
            if (e.Control && e.KeyCode == Keys.N) { e.SuppressKeyPress = true; CreateNewArchiveInteractive(); return; }
            if (e.Control && e.KeyCode == Keys.E) { e.SuppressKeyPress = true; ExtractInteractive(false); return; }
            if (e.Control && e.KeyCode == Keys.F) { e.SuppressKeyPress = true; _txtSearch.Focus(); _txtSearch.SelectAll(); return; }
            if (e.Control && e.KeyCode == Keys.A && _list.Focused && _list.Items.Count > 0)
            {
                e.SuppressKeyPress = true;
                _list.BeginUpdate();
                for (int i = 0; i < _list.Items.Count; i++) _list.Items[i].Selected = true;
                _list.EndUpdate();
                return;
            }
            if (e.Control && e.KeyCode == Keys.C && _list.Focused && _list.SelectedItems.Count > 0)
            {
                e.SuppressKeyPress = true;
                CopySelectedFilesToClipboard();
                return;
            }
            if (e.KeyCode == Keys.Escape && !string.IsNullOrEmpty(_txtSearch.Text)) { e.SuppressKeyPress = true; _txtSearch.Text = ""; return; }
            if (e.KeyCode == Keys.Back && !_txtSearch.Focused && !string.IsNullOrEmpty(_currentFolder)) { NavigateUp(); return; }
            if (e.KeyCode == Keys.Delete && _list.Focused && _list.SelectedItems.Count > 0) { DeleteSelectedFromZip(); return; }
            base.OnKeyDown(e);
        }

        private void BuildUi()
        {
            // 1. Top Toolbar Header
            _header = new Panel { Dock = DockStyle.Top, Height = 54, Padding = new Padding(14, 10, 14, 10) };
            Panel leftBrand = new Panel { Dock = DockStyle.Left, Width = 175 };
            PictureBox picLogo = new PictureBox { Left = 0, Top = 2, Width = 30, Height = 30, Image = _logoBmp, SizeMode = PictureBoxSizeMode.Zoom };
            _lblTitle = new Label { Left = 38, Top = 0, Width = 136, Height = 20, Font = FontBrand, Text = "NillyZip Pro", UseMnemonic = false };
            _lblSub = new Label { Left = 39, Top = 20, Width = 136, Height = 14, Font = FontSub, Text = "ZIP · RAR · 7Z · TAR", UseMnemonic = false };
            leftBrand.Controls.AddRange(new Control[] { picLogo, _lblTitle, _lblSub });

            FlowLayoutPanel actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Right, Width = 855, FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false, Padding = new Padding(0, 3, 0, 0)
            };

            _btnTheme = MkBtn("Light", 48, delegate { _dark = !_dark; ApplyTheme(); });
            _btnTest = MkBtn("✓ Έλεγχος", 88, delegate { TestCurrentArchiveAsync(); });
            _btnDel = MkBtn("✕ Διαγρ.", 80, delegate { DeleteSelectedFromZip(); });
            _btnAdd = MkBtn("+ Προσθήκη", 100, delegate { AddFilesToOpenZip(); });
            _btnSmartExt = MkBtn("⚡ Εξαγωγή Εδώ", 118, delegate { SmartExtractHereAsync(); });
            _btnExtract = MkBtn("↓ Εξαγωγή...", 98, delegate { ExtractInteractive(false); });
            _btnNew = MkBtn("+ Συμπίεση", 96, delegate { CreateNewArchiveInteractive(); });
            _btnOpen = MkBtn("Άνοιγμα", 78, delegate { BrowseOpenArchive(); });

            actions.Controls.AddRange(new Control[] { _btnTheme, _btnTest, _btnDel, _btnAdd, _btnSmartExt, _btnExtract, _btnNew, _btnOpen });
            _header.Controls.Add(actions);
            _header.Controls.Add(leftBrand);

            // 2. Path & Search Bar
            _pathBar = new Panel { Dock = DockStyle.Top, Height = 38, Padding = new Padding(14, 6, 14, 6) };
            _btnUp = MkBtn("↑ Πάνω", 66, delegate { NavigateUp(); });
            _btnUp.Dock = DockStyle.Left;
            _lblPath = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 10, 0), Font = FontUi, UseMnemonic = false, Text = "Σύρε οποιοδήποτε .zip / .rar / .7z / .tar ή αρχεία για συμπίεση" };
            _txtSearch = new TextBox { Dock = DockStyle.Right, Width = 210, BorderStyle = BorderStyle.FixedSingle, Font = FontUi };
            _txtSearch.TextChanged += delegate { PopulateCurrentView(); };
            _pathBar.Controls.Add(_lblPath);
            _pathBar.Controls.Add(_btnUp);
            _pathBar.Controls.Add(_txtSearch);

            // 3. Footer Status + Progress + SEO Credit
            _footer = new Panel { Dock = DockStyle.Bottom, Height = 28, Padding = new Padding(14, 2, 14, 2) };
            _lblStatus = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = FontFooter, UseMnemonic = false, Text = "Έτοιμο · 0 MB background RAM" };
            _prog = new ProgressBar { Dock = DockStyle.Right, Width = 130, Visible = false, Style = ProgressBarStyle.Continuous };
            _lblCredit = new Label { Dock = DockStyle.Right, Width = 365, TextAlign = ContentAlignment.MiddleRight, Font = FontFooter, Cursor = Cursors.Hand, UseMnemonic = false, Text = "created by Nilly graphics (Sotiris Christos) · nillygraphics.com" };
            _lblCredit.Click += delegate { try { Process.Start(new ProcessStartInfo("https://nillygraphics.com/") { UseShellExecute = true }); } catch { } };
            _footer.Controls.Add(_lblStatus);
            _footer.Controls.Add(_prog);
            _footer.Controls.Add(_lblCredit);

            // 4. Split View: Virtual File Table (Left) + Live Preview Pane (Right)
            _split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = 5, FixedPanel = FixedPanel.Panel2, Width = 1040, Height = 500 };
            _split.Panel1MinSize = 360;
            _split.Panel2MinSize = 220;
            _split.SplitterDistance = 740;

            _list = new DoubleBufferedListView
            {
                Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = true,
                BorderStyle = BorderStyle.None, Font = FontUi, HideSelection = false, OwnerDraw = true, AllowDrop = true
            };
            _list.DrawColumnHeader += delegate(object s, DrawListViewColumnHeaderEventArgs e)
            {
                Color hdrBg = _dark ? Color.FromArgb(30, 33, 40) : Color.FromArgb(234, 238, 245);
                Color hdrFg = _dark ? Color.FromArgb(170, 178, 194) : Color.FromArgb(60, 68, 82);
                Color hdrDiv = _dark ? Color.FromArgb(46, 50, 60) : Color.FromArgb(210, 216, 226);
                using (SolidBrush br = new SolidBrush(hdrBg)) e.Graphics.FillRectangle(br, e.Bounds);
                using (Pen p = new Pen(hdrDiv))
                {
                    e.Graphics.DrawLine(p, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                    e.Graphics.DrawLine(p, e.Bounds.Right - 1, e.Bounds.Top + 4, e.Bounds.Right - 1, e.Bounds.Bottom - 4);
                }
                TextFormatFlags fl = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix |
                    (e.Header.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left);
                Rectangle tr = new Rectangle(e.Bounds.X + 8, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 14), e.Bounds.Height);
                TextRenderer.DrawText(e.Graphics, e.Header.Text, FontPill, tr, hdrFg, fl);
            };
            _list.DrawItem += delegate(object s, DrawListViewItemEventArgs e) { e.DrawDefault = true; };
            _list.DrawSubItem += delegate(object s, DrawListViewSubItemEventArgs e) { e.DrawDefault = true; };
            _list.Columns.Add("Όνομα", 340);
            _list.Columns.Add("Μέγεθος", 95, HorizontalAlignment.Right);
            _list.Columns.Add("Συμπιεσμένο", 104, HorizontalAlignment.Right);
            _list.Columns.Add("Κέρδος", 68, HorizontalAlignment.Right);
            _list.Columns.Add("Τροποποίηση", 156);
            UpdateSortHeaders();
            _list.ColumnClick += delegate(object s, ColumnClickEventArgs e)
            {
                if (e.Column == _sortCol) _sortAsc = !_sortAsc;
                else { _sortCol = e.Column; _sortAsc = (e.Column == 0); }
                UpdateSortHeaders();
                PopulateCurrentView();
            };
            EventHandler fitZipCols = delegate
            {
                int cw = _list.ClientSize.Width;
                if (cw <= 320 || _list.Columns.Count < 5) return;
                int c1 = 92, c2 = 100, c3 = 66, c4 = 152;
                int c0 = Math.Max(140, cw - c1 - c2 - c3 - c4 - 2);
                _list.Columns[0].Width = c0;
                _list.Columns[1].Width = c1;
                _list.Columns[2].Width = c2;
                _list.Columns[3].Width = c3;
                _list.Columns[4].Width = c4;
            };
            _list.Resize += fitZipCols;
            _list.HandleCreated += fitZipCols;
            _list.DoubleClick += delegate { OnListDoubleClick(); };
            _list.SelectedIndexChanged += delegate { UpdatePreviewAndButtons(); };
            _list.KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) OnListDoubleClick(); };
            _list.ItemDrag += OnListItemDrag;
            _list.DragEnter += OnDragEnterHandler;
            _list.DragDrop += OnDragDropHandler;

            _ctxList = new ContextMenuStrip { ShowImageMargin = false, Font = FontUi };
            _miOpen = new ToolStripMenuItem("Άνοιγμα", null, delegate { OnListDoubleClick(); });
            _miExtSel = new ToolStripMenuItem("Εξαγωγή επιλεγμένων...", null, delegate { ExtractInteractive(true); });
            _miCopyFiles = new ToolStripMenuItem("Αντιγραφή αρχείων (Ctrl+C)", null, delegate { CopySelectedFilesToClipboard(); });
            _miDelSel = new ToolStripMenuItem("Διαγραφή από το .ZIP", null, delegate { DeleteSelectedFromZip(); });
            _ctxList.Items.AddRange(new ToolStripItem[] { _miOpen, _miExtSel, _miCopyFiles, new ToolStripSeparator(), _miDelSel });
            _ctxList.Opening += delegate(object s, System.ComponentModel.CancelEventArgs ce)
            {
                bool hasSel = _list.SelectedItems.Count > 0 && !_busy && !string.IsNullOrEmpty(_currentArchive);
                if (!hasSel) { ce.Cancel = true; return; }
                bool hasReal = GetSelectedRelPaths().Count > 0;
                _miOpen.Enabled = _list.SelectedItems.Count == 1;
                _miExtSel.Enabled = hasReal;
                _miCopyFiles.Enabled = hasReal;
                _miDelSel.Enabled = hasReal && ArchiveEngine.IsNativeZip(_currentArchive);
            };
            _list.ContextMenuStrip = _ctxList;
            _split.Panel1.Controls.Add(_list);

            // Empty Drop Welcome Panel (overlaid cleanly on Panel1 when no archive is open)
            _emptyPanel = new Panel { Dock = DockStyle.Fill, AllowDrop = true };
            _emptyPanel.DragEnter += OnDragEnterHandler;
            _emptyPanel.DragDrop += OnDragDropHandler;
            Panel card = new Panel { Width = 470, Height = 210 };
            _lblEmptyHead = new Label { Left = 0, Top = 12, Width = 470, Height = 32, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 14f, FontStyle.Bold), Text = "Σύρε αρχεία ή φακέλους εδώ" };
            _lblEmptySub = new Label { Left = 20, Top = 50, Width = 430, Height = 44, TextAlign = ContentAlignment.MiddleCenter, Font = FontUi, Text = "Άνοιγμα .zip, .rar, .7z, .tar, .gz, .xz, .zst, .cab — ή σύρε αρχεία για άμεση συμπίεση χωρίς διαφημίσεις & popups." };
            _btnEmptyOpen = MkBtn("Άνοιγμα Αρχείου", 150, delegate { BrowseOpenArchive(); });
            _btnEmptyOpen.Height = 36; _btnEmptyOpen.Left = 78; _btnEmptyOpen.Top = 114;
            _btnEmptyNew = MkBtn("+ Δημιουργία ZIP / 7Z", 168, delegate { CreateNewArchiveInteractive(); });
            _btnEmptyNew.Height = 36; _btnEmptyNew.Left = 240; _btnEmptyNew.Top = 114;
            card.Controls.AddRange(new Control[] { _lblEmptyHead, _lblEmptySub, _btnEmptyOpen, _btnEmptyNew });
            _emptyPanel.Controls.Add(card);
            _emptyPanel.Resize += delegate { card.Left = Math.Max(0, (_emptyPanel.Width - card.Width) / 2); card.Top = Math.Max(0, (_emptyPanel.Height - card.Height) / 2); };
            _split.Panel1.Controls.Add(_emptyPanel);
            _emptyPanel.BringToFront();

            // Right Live Preview Pane
            _previewPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10), AllowDrop = true };
            _previewPanel.DragEnter += OnDragEnterHandler;
            _previewPanel.DragDrop += OnDragDropHandler;
            _lblPrevTitle = new Label { Dock = DockStyle.Top, Height = 26, Font = FontPill, TextAlign = ContentAlignment.MiddleLeft, Text = "Προεπισκόπηση", UseMnemonic = false };
            _txtPreview = new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                WordWrap = true, BorderStyle = BorderStyle.None, Font = FontMono,
                Text = "Επίλεξε αρχείο για άμεση προεπισκόπηση, ή σύρε αρχεία/φακέλους έξω στον Windows Explorer (ή Ctrl+C → Ctrl+V)."
            };
            _picPreview = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, Visible = false };
            _previewPanel.Controls.Add(_txtPreview);
            _previewPanel.Controls.Add(_picPreview);
            _previewPanel.Controls.Add(_lblPrevTitle);
            _split.Panel2.Controls.Add(_previewPanel);

            // Exact WinForms Z-Order: _header (top 1st), _pathBar (top 2nd), _footer (bottom), _split (Fill frontmost z=0!)
            Controls.Add(_split);
            Controls.Add(_footer);
            Controls.Add(_pathBar);
            Controls.Add(_header);

            UpdateActionButtons();
        }

        private static Button MkBtn(string txt, int w, EventHandler click)
        {
            Button b = new Button();
            b.UseMnemonic = false; b.Text = txt; b.Width = w; b.Height = 27;
            b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderSize = 1;
            b.Font = FontPill; b.Cursor = Cursors.Hand; b.TabStop = false;
            b.Margin = new Padding(3, 1, 3, 0);
            b.Click += click;
            return b;
        }

        public void ApplyTheme()
        {
            Native.SetDarkTitlebar(Handle, _dark);
            Color bgMain = _dark ? Color.FromArgb(20, 21, 25) : Color.FromArgb(244, 246, 250);
            Color bgTop = _dark ? Color.FromArgb(26, 28, 34) : Color.White;
            Color bgBar = _dark ? Color.FromArgb(23, 25, 30) : Color.FromArgb(238, 241, 246);
            Color fgMain = _dark ? Color.FromArgb(240, 244, 250) : Color.FromArgb(22, 24, 30);
            Color fgMuted = _dark ? Color.FromArgb(144, 152, 168) : Color.FromArgb(95, 105, 118);
            Color btnBg = _dark ? Color.FromArgb(36, 39, 46) : Color.FromArgb(236, 240, 246);
            Color border = _dark ? Color.FromArgb(52, 56, 66) : Color.FromArgb(210, 216, 226);
            Color orange = Color.FromArgb(252, 150, 31);
            Color cyan = Color.FromArgb(102, 217, 239);

            BackColor = bgMain;
            _header.BackColor = bgTop;
            _pathBar.BackColor = bgBar;
            _footer.BackColor = bgTop;
            _split.BackColor = border;
            _emptyPanel.BackColor = bgMain;
            _previewPanel.BackColor = bgBar;
            _list.BackColor = bgMain;
            _list.ForeColor = fgMain;
            _txtPreview.BackColor = bgBar;
            _txtPreview.ForeColor = fgMuted;
            _picPreview.BackColor = bgBar;
            _txtSearch.BackColor = _dark ? Color.FromArgb(20, 22, 26) : Color.White;
            _txtSearch.ForeColor = fgMain;

            _lblTitle.ForeColor = fgMain;
            _lblSub.ForeColor = fgMuted;
            _lblPath.ForeColor = fgMain;
            _lblStatus.ForeColor = fgMuted;
            _lblCredit.ForeColor = orange;
            _lblPrevTitle.ForeColor = cyan;
            _lblEmptyHead.ForeColor = fgMain;
            _lblEmptySub.ForeColor = fgMuted;
            _btnTheme.Text = _dark ? "Light" : "Dark";

            Button[] std = new Button[] { _btnOpen, _btnAdd, _btnDel, _btnTest, _btnTheme, _btnUp };
            for (int i = 0; i < std.Length; i++)
            {
                std[i].BackColor = btnBg; std[i].ForeColor = fgMain; std[i].FlatAppearance.BorderColor = border;
            }
            _btnNew.BackColor = cyan; _btnNew.ForeColor = Color.FromArgb(14, 18, 24); _btnNew.FlatAppearance.BorderColor = cyan;
            _btnEmptyNew.BackColor = cyan; _btnEmptyNew.ForeColor = Color.FromArgb(14, 18, 24); _btnEmptyNew.FlatAppearance.BorderColor = cyan;
            _btnExtract.BackColor = orange; _btnExtract.ForeColor = Color.FromArgb(14, 18, 24); _btnExtract.FlatAppearance.BorderColor = orange;
            _btnEmptyOpen.BackColor = orange; _btnEmptyOpen.ForeColor = Color.FromArgb(14, 18, 24); _btnEmptyOpen.FlatAppearance.BorderColor = orange;
            _btnSmartExt.BackColor = _dark ? Color.FromArgb(44, 34, 20) : Color.FromArgb(254, 243, 226);
            _btnSmartExt.ForeColor = _dark ? Color.FromArgb(255, 185, 95) : Color.FromArgb(146, 64, 14);
            _btnSmartExt.FlatAppearance.BorderColor = orange;
            if (_ctxList != null)
            {
                _ctxList.BackColor = bgTop;
                _ctxList.ForeColor = fgMain;
            }
        }

        private void UpdateSortHeaders()
        {
            if (_list == null || _list.Columns.Count < ColBaseTitles.Length) return;
            for (int i = 0; i < ColBaseTitles.Length; i++)
                _list.Columns[i].Text = ColBaseTitles[i] + (i == _sortCol ? (_sortAsc ? " ▲" : " ▼") : "");
        }

        private void UpdateActionButtons()
        {
            bool hasArch = !string.IsNullOrEmpty(_currentArchive) && File.Exists(_currentArchive);
            bool isZip = hasArch && ArchiveEngine.IsNativeZip(_currentArchive);
            _btnExtract.Enabled = hasArch && !_busy;
            _btnSmartExt.Enabled = hasArch && !_busy;
            _btnTest.Enabled = hasArch && !_busy;
            _btnAdd.Enabled = isZip && !_busy;
            _btnDel.Enabled = isZip && !_busy && GetSelectedRelPaths().Count > 0;
            _btnUp.Enabled = hasArch && !string.IsNullOrEmpty(_currentFolder);
            _emptyPanel.Visible = !hasArch;
        }

        private void SetBusy(bool busy, string msg)
        {
            _busy = busy;
            _prog.Visible = busy;
            if (busy) _prog.Style = ProgressBarStyle.Marquee;
            if (!string.IsNullOrEmpty(msg)) _lblStatus.Text = msg;
            UpdateActionButtons();
        }

        private void BrowseOpenArchive()
        {
            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Title = "NillyZip Pro — Άνοιγμα Συμπιεσμένου Αρχείου";
                ofd.Filter = "Όλα τα Συμπιεσμένα (*.zip;*.rar;*.7z;*.tar;*.gz;*.tgz;*.xz;*.bz2;*.zst;*.cab)|*.zip;*.rar;*.7z;*.tar;*.gz;*.tgz;*.xz;*.bz2;*.zst;*.cab;*.iso|Όλα τα αρχεία (*.*)|*.*";
                if (ofd.ShowDialog(this) == DialogResult.OK) OpenArchiveAsync(ofd.FileName);
            }
        }

        public void OpenArchiveSyncForTest(string path)
        {
            _currentArchive = path;
            _currentFolder = "";
            _allEntries = ArchiveEngine.ListEntries(path);
            Text = Path.GetFileName(path) + " — NillyZip Pro";
            PopulateCurrentView();
            UpdateActionButtons();
        }

        private void OpenArchiveAsync(string path)
        {
            if (_busy || !File.Exists(path)) return;
            SetBusy(true, "Ανάγνωση " + Path.GetFileName(path) + "...");
            Thread th = new Thread(delegate()
            {
                List<ArchiveEntryItem> items = null;
                string err = null;
                try { items = ArchiveEngine.ListEntries(path); }
                catch (Exception ex) { err = ArchiveEngine.FriendlyError(ex); }
                try
                {
                    BeginInvoke((Action)delegate
                    {
                        SetBusy(false, null);
                        if (err != null)
                        {
                            _lblStatus.Text = "Σφάλμα ανοίγματος.";
                            MessageBox.Show(this, err, "NillyZip Pro", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }
                        _currentArchive = path;
                        _currentFolder = "";
                        _allEntries = items ?? new List<ArchiveEntryItem>();
                        Text = Path.GetFileName(path) + " — NillyZip Pro";
                        PopulateCurrentView();
                        UpdateActionButtons();
                        Native.TrimWorkingSet();
                    });
                }
                catch { }
            });
            th.IsBackground = true;
            th.Start();
        }

        private void PopulateCurrentView()
        {
            _list.BeginUpdate();
            _list.Items.Clear();

            if (string.IsNullOrEmpty(_currentArchive))
            {
                _list.EndUpdate();
                return;
            }

            string q = NormalizeGreek(_txtSearch.Text ?? "").Trim();
            bool searching = q.Length > 0;
            long totalRaw = 0, totalComp = 0;
            int fileCount = 0;

            HashSet<string> shownFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<ListViewItem> folderRows = new List<ListViewItem>();
            List<ListViewItem> fileRows = new List<ListViewItem>();

            string prefix = string.IsNullOrEmpty(_currentFolder) ? "" : (_currentFolder + "/");

            for (int i = 0; i < _allEntries.Count; i++)
            {
                ArchiveEntryItem it = _allEntries[i];
                if (!it.IsDir)
                {
                    fileCount++;
                    totalRaw += it.UncompressedSize;
                    if (it.CompressedSize >= 0) totalComp += it.CompressedSize;
                }

                if (searching)
                {
                    if (it.IsDir) continue;
                    if (NormalizeGreek(it.FullPath).IndexOf(q, StringComparison.Ordinal) < 0) continue;
                    fileRows.Add(MakeFileRow(it, true));
                    continue;
                }

                if (prefix.Length > 0 && !it.FullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                string rem = prefix.Length > 0 ? it.FullPath.Substring(prefix.Length) : it.FullPath;
                if (string.IsNullOrEmpty(rem)) continue;

                int sl = rem.IndexOf('/');
                if (sl > 0 || it.IsDir)
                {
                    string folderName = sl > 0 ? rem.Substring(0, sl) : rem;
                    if (shownFolders.Add(folderName))
                    {
                        ListViewItem lvi = new ListViewItem("📁  " + folderName);
                        lvi.SubItems.Add("Φάκελος");
                        lvi.SubItems.Add("");
                        lvi.SubItems.Add("");
                        lvi.SubItems.Add("");
                        lvi.Tag = "DIR:" + prefix + folderName;
                        lvi.ForeColor = Color.FromArgb(252, 150, 31);
                        folderRows.Add(lvi);
                    }
                }
                else
                {
                    fileRows.Add(MakeFileRow(it, false));
                }
            }

            int dirSign = (_sortCol == 0 && !_sortAsc) ? -1 : 1;
            folderRows.Sort(delegate(ListViewItem a, ListViewItem b)
            {
                return dirSign * string.Compare(a.Text, b.Text, StringComparison.OrdinalIgnoreCase);
            });
            int sign = _sortAsc ? 1 : -1;
            fileRows.Sort(delegate(ListViewItem a, ListViewItem b)
            {
                ArchiveEntryItem ia = a.Tag as ArchiveEntryItem;
                ArchiveEntryItem ib = b.Tag as ArchiveEntryItem;
                int cmp = 0;
                if (ia != null && ib != null)
                {
                    if (_sortCol == 1) cmp = ia.UncompressedSize.CompareTo(ib.UncompressedSize);
                    else if (_sortCol == 2) cmp = ia.CompressedSize.CompareTo(ib.CompressedSize);
                    else if (_sortCol == 3)
                    {
                        double ra = (ia.UncompressedSize > 0 && ia.CompressedSize >= 0) ? (1.0 - (double)ia.CompressedSize / ia.UncompressedSize) : -1.0;
                        double rb = (ib.UncompressedSize > 0 && ib.CompressedSize >= 0) ? (1.0 - (double)ib.CompressedSize / ib.UncompressedSize) : -1.0;
                        cmp = ra.CompareTo(rb);
                    }
                    else if (_sortCol == 4) cmp = ia.Modified.CompareTo(ib.Modified);
                    else cmp = string.Compare(ia.Name, ib.Name, StringComparison.OrdinalIgnoreCase);
                }
                if (cmp == 0) cmp = string.Compare(a.Text, b.Text, StringComparison.OrdinalIgnoreCase);
                return sign * cmp;
            });

            if (!searching && !string.IsNullOrEmpty(_currentFolder))
            {
                ListViewItem upRow = new ListViewItem("↩  ..");
                upRow.SubItems.Add("Επάνω φάκελος");
                upRow.SubItems.Add("");
                upRow.SubItems.Add("");
                upRow.SubItems.Add("");
                upRow.Tag = "UP";
                upRow.ForeColor = Color.FromArgb(102, 217, 239);
                _list.Items.Add(upRow);
            }
            for (int i = 0; i < folderRows.Count; i++) _list.Items.Add(folderRows[i]);
            for (int i = 0; i < fileRows.Count; i++) _list.Items.Add(fileRows[i]);
            _list.EndUpdate();

            string archName = Path.GetFileName(_currentArchive);
            _lblPath.Text = string.IsNullOrEmpty(_currentFolder) ? archName : (archName + "  ›  " + _currentFolder.Replace("/", "  ›  "));
            long archDisk = 0;
            try { archDisk = new FileInfo(_currentArchive).Length; } catch { }
            string ratioStr = (totalRaw > 0 && totalComp > 0) ? string.Format(" · εξοικονόμηση {0:0}%", Math.Max(0, (1.0 - (double)totalComp / totalRaw) * 100.0)) : "";
            _lblStatus.Text = string.Format("{0} αρχεία · {1} (αρχείο: {2}){3}", fileCount, FormatBytes(totalRaw), FormatBytes(archDisk), ratioStr);
            UpdateActionButtons();
        }

        private ListViewItem MakeFileRow(ArchiveEntryItem it, bool showFullPath)
        {
            ListViewItem lvi = new ListViewItem("📄  " + (showFullPath ? it.FullPath : it.Name));
            lvi.SubItems.Add(FormatBytes(it.UncompressedSize));
            lvi.SubItems.Add(it.CompressedSize >= 0 ? FormatBytes(it.CompressedSize) : "—");
            string ratio = (it.UncompressedSize > 0 && it.CompressedSize >= 0)
                ? string.Format("{0:0}%", Math.Max(0, (1.0 - (double)it.CompressedSize / it.UncompressedSize) * 100.0))
                : "—";
            lvi.SubItems.Add(ratio);
            lvi.SubItems.Add(it.Modified > DateTime.MinValue ? it.Modified.ToString("dd/MM/yyyy HH:mm") : "—");
            lvi.Tag = it;
            return lvi;
        }

        private void NavigateUp()
        {
            if (string.IsNullOrEmpty(_currentFolder)) return;
            int sl = _currentFolder.LastIndexOf('/');
            _currentFolder = sl > 0 ? _currentFolder.Substring(0, sl) : "";
            PopulateCurrentView();
        }

        private void OnListDoubleClick()
        {
            if (_list.SelectedItems.Count == 0) return;
            object tag = _list.SelectedItems[0].Tag;
            string dirTag = tag as string;
            if (dirTag == "UP")
            {
                NavigateUp();
                return;
            }
            if (dirTag != null && dirTag.StartsWith("DIR:"))
            {
                _currentFolder = dirTag.Substring(4);
                PopulateCurrentView();
                return;
            }
            ArchiveEntryItem item = tag as ArchiveEntryItem;
            if (item != null && !item.IsDir)
                ExtractSingleAndOpenTemp(item);
        }

        private void ExtractSingleAndOpenTemp(ArchiveEntryItem item)
        {
            if (string.IsNullOrEmpty(_currentArchive) || item == null) return;
            try
            {
                string tmpDir = Path.Combine(Path.GetTempPath(), "NillyZipPreview");
                Directory.CreateDirectory(tmpDir);
                string outFile;
                if (ArchiveEngine.IsNativeZip(_currentArchive))
                {
                    outFile = Path.Combine(tmpDir, Path.GetFileName(item.Name));
                    byte[] data = ArchiveEngine.ReadZipEntryBytes(_currentArchive, item.FullPath, 64 * 1024 * 1024);
                    if (data == null) return;
                    File.WriteAllBytes(outFile, data);
                }
                else
                {
                    string sub = Path.Combine(tmpDir, Guid.NewGuid().ToString("N").Substring(0, 8));
                    string[] extracted = ArchiveEngine.ExtractForDrag(_currentArchive, sub, new List<string> { item.FullPath }, "", true);
                    if (extracted.Length == 0) return;
                    outFile = extracted[0];
                }
                Process.Start(new ProcessStartInfo(outFile) { UseShellExecute = true });
            }
            catch (Exception ex) { MessageBox.Show(this, ArchiveEngine.FriendlyError(ex), "NillyZip Pro", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        private string[] StageSelectedForExport(out string stageDir)
        {
            stageDir = null;
            if (_busy || string.IsNullOrEmpty(_currentArchive) || !File.Exists(_currentArchive)) return new string[0];
            List<string> rels = GetSelectedRelPaths();
            if (rels.Count == 0) return new string[0];
            bool searching = !string.IsNullOrWhiteSpace(_txtSearch.Text);
            stageDir = Path.Combine(ArchiveEngine.DragTempRoot, Guid.NewGuid().ToString("N").Substring(0, 12));
            return ArchiveEngine.ExtractForDrag(_currentArchive, stageDir, rels, _currentFolder, searching);
        }

        private void OnListItemDrag(object sender, ItemDragEventArgs e)
        {
            if (e.Button != MouseButtons.Left || _busy || string.IsNullOrEmpty(_currentArchive)) return;
            ListViewItem draggedItem = e.Item as ListViewItem;
            if (draggedItem != null && !draggedItem.Selected)
                draggedItem.Selected = true;
            if (GetSelectedRelPaths().Count == 0) return;

            string prevStatus = _lblStatus.Text;
            string stageDir = null;
            try
            {
                Cursor = Cursors.WaitCursor;
                _lblStatus.Text = "Προετοιμασία " + _list.SelectedItems.Count + " στοιχείων για σύρσιμο...";
                _lblStatus.Refresh(); // Repaint status without pumping mouse-up messages!

                string[] staged = StageSelectedForExport(out stageDir);
                Cursor = Cursors.Default;
                if (staged == null || staged.Length == 0)
                {
                    _lblStatus.Text = prevStatus;
                    if (!string.IsNullOrEmpty(stageDir)) try { Directory.Delete(stageDir, true); } catch { }
                    return;
                }

                DataObject dataObj = new DataObject(DataFormats.FileDrop, staged);
                _internalDrag = true;
                DragDropEffects effect = DragDropEffects.None;
                try
                {
                    effect = _list.DoDragDrop(dataObj, DragDropEffects.Copy);
                }
                finally
                {
                    _internalDrag = false;
                }

                if (effect != DragDropEffects.None)
                {
                    _dragTempDirs.Add(new KeyValuePair<string, DateTime>(stageDir, DateTime.UtcNow));
                    _lblStatus.Text = "✓ Εξήχθησαν με σύρσιμο " + staged.Length + " στοιχεία!";
                }
                else
                {
                    try { if (Directory.Exists(stageDir)) Directory.Delete(stageDir, true); } catch { }
                    _lblStatus.Text = prevStatus;
                }
            }
            catch (Exception ex)
            {
                Cursor = Cursors.Default;
                _internalDrag = false;
                if (!string.IsNullOrEmpty(stageDir)) try { if (Directory.Exists(stageDir)) Directory.Delete(stageDir, true); } catch { }
                _lblStatus.Text = prevStatus;
                MessageBox.Show(this, "Σφάλμα κατά το σύρσιμο αρχείων:\r\n" + ArchiveEngine.FriendlyError(ex), "NillyZip Pro", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void CopySelectedFilesToClipboard()
        {
            if (_busy || string.IsNullOrEmpty(_currentArchive) || _list.SelectedItems.Count == 0) return;
            string stageDir = null;
            try
            {
                Cursor = Cursors.WaitCursor;
                _lblStatus.Text = "Αντιγραφή επιλεγμένων αρχείων στο πρόχειρο...";
                _lblStatus.Refresh();
                string[] staged = StageSelectedForExport(out stageDir);
                Cursor = Cursors.Default;
                if (staged == null || staged.Length == 0) return;

                StringCollection sc = new StringCollection();
                sc.AddRange(staged);
                Clipboard.SetFileDropList(sc);
                _dragTempDirs.Add(new KeyValuePair<string, DateTime>(stageDir, DateTime.UtcNow));
                _lblStatus.Text = "✓ Αντιγράφηκαν " + staged.Length + " στοιχεία — πάτησε Ctrl+V σε οποιονδήποτε φάκελο!";
            }
            catch (Exception ex)
            {
                Cursor = Cursors.Default;
                if (!string.IsNullOrEmpty(stageDir)) try { if (Directory.Exists(stageDir)) Directory.Delete(stageDir, true); } catch { }
                MessageBox.Show(this, "Σφάλμα αντιγραφής:\r\n" + ArchiveEngine.FriendlyError(ex), "NillyZip Pro", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void UpdatePreviewAndButtons()
        {
            UpdateActionButtons();
            if (_previewImg != null) { _picPreview.Image = null; _previewImg.Dispose(); _previewImg = null; }
            _picPreview.Visible = false;
            _txtPreview.Visible = true;

            if (_list.SelectedItems.Count != 1) return;
            if ((_list.SelectedItems[0].Tag as string) == "UP")
            {
                _lblPrevTitle.Text = "Επάνω φάκελος (..)";
                _txtPreview.Text = "Διπλό κλικ ή Backspace για επιστροφή στον προηγούμενο φάκελο.";
                return;
            }
            ArchiveEntryItem it = _list.SelectedItems[0].Tag as ArchiveEntryItem;
            if (it == null || it.IsDir) { _lblPrevTitle.Text = "Φάκελος"; _txtPreview.Text = "Διπλό κλικ για άνοιγμα φακέλου."; return; }

            _lblPrevTitle.Text = it.Name + " (" + FormatBytes(it.UncompressedSize) + ")";
            if (!ArchiveEngine.IsNativeZip(_currentArchive))
            {
                _txtPreview.Text = "Η άμεση προεπισκόπηση χωρίς αποσυμπίεση υποστηρίζεται σε αρχεία .ZIP.\r\nΠάτησε «Εξαγωγή» για αποσυμπίεση.";
                return;
            }

            string ext = Path.GetExtension(it.Name).ToLowerInvariant();
            if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".gif" || ext == ".bmp")
            {
                byte[] bytes = ArchiveEngine.ReadZipEntryBytes(_currentArchive, it.FullPath, 8 * 1024 * 1024);
                if (bytes != null)
                {
                    try
                    {
                        using (MemoryStream ms = new MemoryStream(bytes))
                        using (Image tmp = Image.FromStream(ms))
                            _previewImg = new Bitmap(tmp);
                        _picPreview.Image = _previewImg;
                        _txtPreview.Visible = false;
                        _picPreview.Visible = true;
                        return;
                    }
                    catch { }
                }
            }

            string[] textExts = new string[] { ".txt", ".md", ".json", ".xml", ".csv", ".html", ".htm", ".css", ".js", ".ts", ".cs", ".ps1", ".bat", ".vbs", ".ini", ".cfg", ".log", ".yml", ".yaml", ".svg", ".php", ".py", ".sql" };
            bool isText = Array.IndexOf(textExts, ext) >= 0;
            if (isText)
            {
                byte[] bytes = ArchiveEngine.ReadZipEntryBytes(_currentArchive, it.FullPath, 256 * 1024);
                if (bytes != null)
                {
                    _txtPreview.Text = Encoding.UTF8.GetString(bytes);
                    return;
                }
            }

            _txtPreview.Text = "Αρχείο: " + it.FullPath + "\r\nΜέγεθος: " + FormatBytes(it.UncompressedSize) +
                (it.CompressedSize >= 0 ? ("\r\nΣυμπιεσμένο: " + FormatBytes(it.CompressedSize)) : "") +
                "\r\n\r\nΚάνε διπλό κλικ για άνοιγμα ή «Εξαγωγή».";
        }

        private List<string> GetSelectedRelPaths()
        {
            List<string> list = new List<string>();
            for (int i = 0; i < _list.SelectedItems.Count; i++)
            {
                object tag = _list.SelectedItems[i].Tag;
                string d = tag as string;
                if (d != null && d.StartsWith("DIR:")) list.Add(d.Substring(4));
                else
                {
                    ArchiveEntryItem it = tag as ArchiveEntryItem;
                    if (it != null) list.Add(it.FullPath);
                }
            }
            return list;
        }

        private void SmartExtractHereAsync()
        {
            if (_busy || string.IsNullOrEmpty(_currentArchive)) return;
            string parent = Path.GetDirectoryName(_currentArchive);
            string targetDir = ArchiveEngine.ResolveSmartExtractDir(_currentArchive, parent, _allEntries);
            RunExtractAsync(_currentArchive, targetDir, null, true);
        }

        private void ExtractInteractive(bool onlySelected)
        {
            if (_busy || string.IsNullOrEmpty(_currentArchive)) return;
            using (FolderBrowserDialog fbd = new FolderBrowserDialog())
            {
                fbd.Description = "Επίλεξε φάκελο αποσυμπίεσης — NillyZip Pro";
                fbd.SelectedPath = Path.GetDirectoryName(_currentArchive);
                if (fbd.ShowDialog(this) != DialogResult.OK) return;
                List<string> sel = onlySelected ? GetSelectedRelPaths() : null;
                RunExtractAsync(_currentArchive, fbd.SelectedPath, sel, true);
            }
        }

        private void RunExtractAsync(string archPath, string destDir, List<string> onlyRel, bool openFolder)
        {
            SetBusy(true, "Αποσυμπίεση...");
            Thread th = new Thread(delegate()
            {
                int count = 0;
                string err = null;
                try
                {
                    count = ArchiveEngine.ExtractArchive(archPath, destDir, onlyRel, delegate(int cur, int tot, string name)
                    {
                        try { BeginInvoke((Action)delegate { _lblStatus.Text = string.Format("Αποσυμπίεση ({0}/{1}): {2}", cur, tot, Path.GetFileName(name)); }); } catch { }
                    });
                }
                catch (Exception ex) { err = ArchiveEngine.FriendlyError(ex); }
                try
                {
                    BeginInvoke((Action)delegate
                    {
                        SetBusy(false, null);
                        if (err != null)
                        {
                            _lblStatus.Text = "Σφάλμα αποσυμπίεσης.";
                            MessageBox.Show(this, err, "NillyZip Pro", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                        else
                        {
                            _lblStatus.Text = "✓ Εξήχθησαν " + count + " στοιχεία -> " + destDir;
                            if (openFolder && Directory.Exists(destDir))
                                Program.OpenFolderSmart(destDir);
                        }
                    });
                }
                catch { }
            });
            th.IsBackground = true;
            th.Start();
        }

        private void CreateNewArchiveInteractive()
        {
            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Title = "Επίλεξε αρχεία για συμπίεση (ή σύρε φακέλους στο παράθυρο)";
                ofd.Multiselect = true;
                if (ofd.ShowDialog(this) == DialogResult.OK && ofd.FileNames.Length > 0)
                    PromptAndCreateArchive(ofd.FileNames);
            }
        }

        private void PromptAndCreateArchive(string[] sources)
        {
            if (_busy || sources == null || sources.Length == 0) return;
            using (CreateArchiveDialog dlg = new CreateArchiveDialog(sources, _dark))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string outPath = dlg.OutputPath;
                CompressionLevel lvl = dlg.SelectedLevel;
                SetBusy(true, "Συμπίεση " + Path.GetFileName(outPath) + "...");
                Thread th = new Thread(delegate()
                {
                    string err = null;
                    try
                    {
                        ArchiveEngine.CreateMultiFormatArchive(outPath, sources, lvl, delegate(int c, int t, string n)
                        {
                            try { BeginInvoke((Action)delegate { _lblStatus.Text = string.Format("Συμπίεση ({0}/{1}): {2}", c, t, n); }); } catch { }
                        });
                    }
                    catch (Exception ex) { err = ArchiveEngine.FriendlyError(ex); }
                    try
                    {
                        BeginInvoke((Action)delegate
                        {
                            SetBusy(false, null);
                            if (err != null) MessageBox.Show(this, err, "NillyZip Pro", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            else OpenArchiveAsync(outPath);
                        });
                    }
                    catch { }
                });
                th.IsBackground = true;
                th.Start();
            }
        }

        private void AddFilesToOpenZip()
        {
            if (_busy || !ArchiveEngine.IsNativeZip(_currentArchive)) return;
            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Title = "Προσθήκη αρχείων στο " + Path.GetFileName(_currentArchive);
                ofd.Multiselect = true;
                if (ofd.ShowDialog(this) != DialogResult.OK || ofd.FileNames.Length == 0) return;
                try
                {
                    int n = ArchiveEngine.AddFilesToZip(_currentArchive, ofd.FileNames, _currentFolder, CompressionLevel.Optimal);
                    OpenArchiveAsync(_currentArchive);
                    _lblStatus.Text = "✓ Προστέθηκαν " + n + " αρχεία.";
                }
                catch (Exception ex) { MessageBox.Show(this, ArchiveEngine.FriendlyError(ex), "NillyZip Pro", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }

        private void DeleteSelectedFromZip()
        {
            if (_busy || !ArchiveEngine.IsNativeZip(_currentArchive)) return;
            List<string> targets = GetSelectedRelPaths();
            if (targets.Count == 0) return;
            if (MessageBox.Show(this, "Διαγραφή " + targets.Count + " επιλεγμένων στοιχείων από το " + Path.GetFileName(_currentArchive) + ";",
                "NillyZip Pro", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            try
            {
                int n = ArchiveEngine.DeleteFromZip(_currentArchive, targets);
                OpenArchiveAsync(_currentArchive);
                _lblStatus.Text = "Διαγράφηκαν " + n + " στοιχεία.";
            }
            catch (Exception ex) { MessageBox.Show(this, ArchiveEngine.FriendlyError(ex), "NillyZip Pro", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        private void TestCurrentArchiveAsync()
        {
            if (_busy || string.IsNullOrEmpty(_currentArchive)) return;
            string arch = _currentArchive;
            SetBusy(true, "Έλεγχος ακεραιότητας & SHA-256...");
            Thread th = new Thread(delegate()
            {
                string hash = null, err = null;
                int cnt = 0;
                try { hash = ArchiveEngine.TestIntegrityAndSha256(arch, out cnt); }
                catch (Exception ex) { err = ArchiveEngine.FriendlyError(ex); }
                try
                {
                    BeginInvoke((Action)delegate
                    {
                        SetBusy(false, null);
                        if (err != null)
                        {
                            MessageBox.Show(this, "Βρέθηκε σφάλμα ακεραιότητας:\r\n" + err, "NillyZip Pro — Έλεγχος", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return;
                        }
                        _lblStatus.Text = "✓ Επαληθεύτηκαν " + cnt + " αρχεία · SHA-256: " + hash.Substring(0, 16) + "...";
                        if (MessageBox.Show(this,
                            "✓ Το αρχείο είναι 100% έγκυρο (" + cnt + " αρχεία επαληθεύτηκαν χωρίς σφάλματα)!\r\n\r\nSHA-256:\r\n" + hash + "\r\n\r\nΘες αντιγραφή του SHA-256 στο πρόχειρο;",
                            "NillyZip Pro — Έλεγχος Ακεραιότητας", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                        {
                            try { Clipboard.SetText(hash); } catch { }
                        }
                    });
                }
                catch { }
            });
            th.IsBackground = true;
            th.Start();
        }

        private void OnDragEnterHandler(object sender, DragEventArgs e)
        {
            if (_internalDrag) { e.Effect = DragDropEffects.None; return; }
            if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
        }

        private void OnDragDropHandler(object sender, DragEventArgs e)
        {
            if (_internalDrag || e.Data == null) return;
            string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return;
            if (files.Length == 1 && File.Exists(files[0]) && ArchiveEngine.IsSupportedArchive(files[0]))
            {
                OpenArchiveAsync(files[0]);
                return;
            }
            if (!string.IsNullOrEmpty(_currentArchive) && ArchiveEngine.IsNativeZip(_currentArchive))
            {
                DialogResult dr = MessageBox.Show(this,
                    "Προσθήκη των " + files.Length + " στοιχείων στο ανοιχτό " + Path.GetFileName(_currentArchive) + ";\r\n(Όχι = Δημιουργία νέου αρχείου)",
                    "NillyZip Pro", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (dr == DialogResult.Yes)
                {
                    try { ArchiveEngine.AddFilesToZip(_currentArchive, files, _currentFolder, CompressionLevel.Optimal); OpenArchiveAsync(_currentArchive); }
                    catch (Exception ex) { MessageBox.Show(this, ArchiveEngine.FriendlyError(ex), "NillyZip Pro", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                }
                else if (dr == DialogResult.No)
                {
                    PromptAndCreateArchive(files);
                }
                return;
            }
            PromptAndCreateArchive(files);
        }

        private static string NormalizeGreek(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string norm = s.Normalize(NormalizationForm.FormD);
            StringBuilder sb = new StringBuilder(norm.Length);
            for (int i = 0; i < norm.Length; i++)
            {
                UnicodeCategory uc = CharUnicodeInfo.GetUnicodeCategory(norm[i]);
                if (uc != UnicodeCategory.NonSpacingMark)
                {
                    char c = char.ToLowerInvariant(norm[i]);
                    sb.Append(c == 'ς' ? 'σ' : c);
                }
            }
            return sb.ToString();
        }

        private static string FormatBytes(long b)
        {
            if (b < 0) return "—";
            if (b < 1024) return b + " B";
            if (b < 1048576) return (b / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
            if (b < 1073741824) return (b / 1048576.0).ToString("0.00", CultureInfo.InvariantCulture) + " MB";
            return (b / 1073741824.0).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_logoBmp != null) { _logoBmp.Dispose(); _logoBmp = null; }
                if (_previewImg != null) { _previewImg.Dispose(); _previewImg = null; }
            }
            base.Dispose(disposing);
        }
    }

    internal static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            try { Native.SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (args != null && args.Length > 0)
            {
                if (args[0].Equals("--test-smoke", StringComparison.OrdinalIgnoreCase))
                    return RunSmokeTest(args.Length > 1 ? args[1] : null, args.Length > 2 ? args[2] : null);

                if (args[0].Equals("--extract-here", StringComparison.OrdinalIgnoreCase) && args.Length > 1)
                    return RunCliExtract(args[1], true);

                if (args[0].Equals("--extract-folder", StringComparison.OrdinalIgnoreCase) && args.Length > 1)
                    return RunCliExtract(args[1], false);

                if (args[0].Equals("--compress", StringComparison.OrdinalIgnoreCase) && args.Length > 1)
                {
                    string[] srcs = new string[args.Length - 1];
                    Array.Copy(args, 1, srcs, 0, srcs.Length);
                    return RunCliCompress(srcs);
                }

                if (File.Exists(args[0]))
                {
                    Application.Run(new MainForm(args[0]));
                    return 0;
                }
            }

            Application.Run(new MainForm(null));
            return 0;
        }

        internal static bool ReadSuiteDark(bool fallback)
        {
            try
            {
                string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"NillySuite\suite.theme");
                if (File.Exists(p))
                {
                    string s = File.ReadAllText(p, Encoding.ASCII).Trim();
                    if (s.Equals("light", StringComparison.OrdinalIgnoreCase)) return false;
                    if (s.Equals("dark", StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch { }
            return fallback;
        }

        internal static void OpenFolderSmart(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            try
            {
                string filesExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"NillySuite\NillyFilesPro\NillyFilesPro.exe");
                if (!File.Exists(filesExe))
                {
                    DirectoryInfo par = Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'));
                    if (par != null)
                    {
                        string sib = Path.Combine(Path.Combine(par.FullName, "NillyFilesPro"), "NillyFilesPro.exe");
                        if (File.Exists(sib)) filesExe = sib;
                    }
                }
                if (File.Exists(filesExe))
                {
                    Process.Start(new ProcessStartInfo(filesExe, "\"" + dir + "\"") { WorkingDirectory = Path.GetDirectoryName(filesExe), UseShellExecute = true });
                    return;
                }
            }
            catch { }
            try { Process.Start("explorer.exe", "\"" + dir + "\""); } catch { }
        }

        private static int RunCliExtract(string archivePath, bool smartHere)
        {
            try
            {
                if (!File.Exists(archivePath)) return 2;
                List<ArchiveEntryItem> entries = ArchiveEngine.ListEntries(archivePath);
                string parent = Path.GetDirectoryName(Path.GetFullPath(archivePath));
                string dest = smartHere
                    ? ArchiveEngine.ResolveSmartExtractDir(archivePath, parent, entries)
                    : Path.Combine(parent, ArchiveEngine.StripArchiveExts(Path.GetFileName(archivePath)));
                ArchiveEngine.ExtractArchive(archivePath, dest, null, null);
                OpenFolderSmart(dest);
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Σφάλμα αποσυμπίεσης:\r\n" + ArchiveEngine.FriendlyError(ex), "NillyZip Pro", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        private static int RunCliCompress(string[] srcs)
        {
            try
            {
                string first = Path.GetFullPath(srcs[0].TrimEnd('\\', '/'));
                string parent = Path.GetDirectoryName(first);
                string stem = srcs.Length == 1
                    ? (Directory.Exists(first) ? Path.GetFileName(first) : Path.GetFileNameWithoutExtension(first))
                    : (Path.GetFileName(parent) + "_archive");
                string outZip = Path.Combine(parent, stem + ".zip");
                int n = 1;
                while (File.Exists(outZip)) outZip = Path.Combine(parent, stem + "_" + (n++) + ".zip");
                ArchiveEngine.CreateZipFromPaths(outZip, srcs, CompressionLevel.Optimal, null);
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Σφάλμα συμπίεσης:\r\n" + ArchiveEngine.FriendlyError(ex), "NillyZip Pro", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        private static int RunSmokeTest(string logPath, string previewPngPath)
        {
            StringBuilder sb = new StringBuilder();
            string temp = Path.Combine(Path.GetTempPath(), "NillyZipSmoke-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(temp);
                string srcDir = Path.Combine(temp, "Πρότζεκτ_Nilly");
                Directory.CreateDirectory(Path.Combine(srcDir, "Έγγραφα"));
                string txtFile = Path.Combine(srcDir, "Έγγραφα", "Προσφορά.txt");
                string sampleText = "NillyZip Pro — created by Nilly graphics (Sotiris Christos) — https://nillygraphics.com/\r\nΕλληνικά UTF-8 OK!";
                File.WriteAllText(txtFile, sampleText, Encoding.UTF8);
                string jsonFile = Path.Combine(srcDir, "manifest.json");
                File.WriteAllText(jsonFile, "{\"app\":\"NillyZip Pro\",\"author\":\"Nilly graphics (Sotiris Christos)\"}", Encoding.UTF8);

                // 1. Create & test native ZIP
                string zipPath = Path.Combine(temp, "NillyTest.zip");
                ArchiveEngine.CreateZipFromPaths(zipPath, new string[] { srcDir }, CompressionLevel.Optimal, null);
                List<ArchiveEntryItem> zipItems = ArchiveEngine.ListEntries(zipPath);
                int verified;
                string sha = ArchiveEngine.TestIntegrityAndSha256(zipPath, out verified);
                sb.AppendLine("ZIP_ENTRIES=" + zipItems.Count + " | VERIFIED=" + verified + " | SHA256=" + sha.Substring(0, 16));
                if (zipItems.Count != 2 || verified != 2) throw new InvalidDataException("ZIP count mismatch.");

                // 2. Test Zip-Slip defense
                bool blocked = !ArchiveEngine.IsSafeRelativePath("../../Windows/evil.txt") && !ArchiveEngine.IsSafeRelativePath("C:/evil.txt");
                sb.AppendLine("ZIP_SLIP_BLOCKED=" + blocked);
                if (!blocked) throw new InvalidOperationException("Zip-Slip check failed.");

                // 3. Test extract & UTF-8 roundtrip match
                string extDir = Path.Combine(temp, "extracted");
                int extCnt = ArchiveEngine.ExtractArchive(zipPath, extDir, null, null);
                string roundTrip = File.ReadAllText(Path.Combine(extDir, "Πρότζεκτ_Nilly", "Έγγραφα", "Προσφορά.txt"), Encoding.UTF8);
                bool utf8Ok = roundTrip == sampleText;
                sb.AppendLine("EXTRACTED=" + extCnt + " | GREEK_UTF8_MATCH=" + utf8Ok);
                if (!utf8Ok) throw new InvalidDataException("UTF-8 content mismatch.");

                // 3b. Test Drag-OUT extraction (nested single file, nested folder, and search-mode flat file)
                string dragFileStage = Path.Combine(temp, "drag_file");
                string[] draggedFile = ArchiveEngine.ExtractForDrag(zipPath, dragFileStage,
                    new List<string> { "Πρότζεκτ_Nilly/Έγγραφα/Προσφορά.txt" }, "Πρότζεκτ_Nilly/Έγγραφα", false);
                bool dragFileOk = draggedFile.Length == 1 &&
                    Path.GetFileName(draggedFile[0]) == "Προσφορά.txt" &&
                    File.ReadAllText(draggedFile[0], Encoding.UTF8) == sampleText;

                string dragDirStage = Path.Combine(temp, "drag_dir");
                string[] draggedDir = ArchiveEngine.ExtractForDrag(zipPath, dragDirStage,
                    new List<string> { "Πρότζεκτ_Nilly/Έγγραφα" }, "Πρότζεκτ_Nilly", false);
                bool dragDirOk = draggedDir.Length == 1 &&
                    Directory.Exists(draggedDir[0]) &&
                    Path.GetFileName(draggedDir[0]) == "Έγγραφα" &&
                    File.ReadAllText(Path.Combine(draggedDir[0], "Προσφορά.txt"), Encoding.UTF8) == sampleText;

                string dragSearchStage = Path.Combine(temp, "drag_search");
                string[] draggedSearch = ArchiveEngine.ExtractForDrag(zipPath, dragSearchStage,
                    new List<string> { "Πρότζεκτ_Nilly/Έγγραφα/Προσφορά.txt", "Πρότζεκτ_Nilly/manifest.json" }, "", true);
                bool dragSearchOk = draggedSearch.Length == 2 &&
                    File.Exists(Path.Combine(dragSearchStage, "Προσφορά.txt")) &&
                    File.Exists(Path.Combine(dragSearchStage, "manifest.json"));

                sb.AppendLine("DRAG_OUT_ZIP_FILE=" + dragFileOk + " | DRAG_OUT_ZIP_DIR=" + dragDirOk + " | DRAG_OUT_SEARCH=" + dragSearchOk);
                if (!dragFileOk || !dragDirOk || !dragSearchOk)
                    throw new InvalidDataException("Drag-out ZIP extraction test failed.");

                // 4. Test native Windows 11 tar.exe .7z creation, reading & drag-out (ASCII paths for universal locale support)
                bool sevenZOk = false;
                bool sevenZDragOk = false;
                if (ArchiveEngine.TarExePath != null)
                {
                    string asciiDir = Path.Combine(temp, "Nilly_7z_Project");
                    Directory.CreateDirectory(asciiDir);
                    File.WriteAllText(Path.Combine(asciiDir, "readme.txt"), sampleText, Encoding.UTF8);
                    File.WriteAllText(Path.Combine(asciiDir, "data.json"), "{\"ok\":true}", Encoding.UTF8);
                    string szPath = Path.Combine(temp, "NillyTest.7z");
                    ArchiveEngine.CreateMultiFormatArchive(szPath, new string[] { asciiDir }, CompressionLevel.Optimal, null);
                    List<ArchiveEntryItem> szItems = ArchiveEngine.ListEntries(szPath);
                    sevenZOk = szItems.Count >= 2;
                    string drag7zStage = Path.Combine(temp, "drag_7z");
                    string[] dragged7z = ArchiveEngine.ExtractForDrag(szPath, drag7zStage,
                        new List<string> { "Nilly_7z_Project/readme.txt" }, "Nilly_7z_Project", false);
                    sevenZDragOk = dragged7z.Length == 1 &&
                        Path.GetFileName(dragged7z[0]) == "readme.txt" &&
                        File.ReadAllText(dragged7z[0], Encoding.UTF8) == sampleText;
                    sb.AppendLine("7Z_NATIVE_ENTRIES=" + szItems.Count + " | DRAG_OUT_7Z=" + sevenZDragOk);
                }
                sb.AppendLine("7Z_OK=" + sevenZOk);
                if (ArchiveEngine.TarExePath != null && (!sevenZOk || !sevenZDragOk))
                    throw new InvalidDataException("7Z drag-out test failed.");

                // 5. Open GUI, load archive, capture preview PNG & measure RAM
                using (MainForm f = new MainForm(null))
                {
                    f.Show();
                    f.OpenArchiveSyncForTest(zipPath);
                    Application.DoEvents();
                    Native.TrimWorkingSet();
                    if (!string.IsNullOrEmpty(previewPngPath))
                    {
                        using (Bitmap shot = new Bitmap(f.Width, f.Height))
                        {
                            f.DrawToBitmap(shot, new Rectangle(0, 0, f.Width, f.Height));
                            shot.Save(previewPngPath, System.Drawing.Imaging.ImageFormat.Png);
                        }
                    }
                    long ramMb = Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024);
                    sb.AppendLine("WINDOW_OPENED_OK=" + f.IsHandleCreated);
                    sb.AppendLine("WORKING_SET_MB=" + ramMb);
                    f.Close();
                }

                sb.AppendLine("SMOKE_RESULT=PASS");
            }
            catch (Exception ex)
            {
                sb.AppendLine("SMOKE_RESULT=FAIL");
                sb.AppendLine(ex.ToString());
                if (!string.IsNullOrEmpty(logPath)) File.WriteAllText(logPath, sb.ToString(), Encoding.UTF8);
                return 1;
            }
            finally
            {
                try { Directory.Delete(temp, true); } catch { }
            }
            if (!string.IsNullOrEmpty(logPath)) File.WriteAllText(logPath, sb.ToString(), Encoding.UTF8);
            return 0;
        }
    }
}
