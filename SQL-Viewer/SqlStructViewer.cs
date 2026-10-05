// SqlStructViewer - 极小 Windows 桌面程序：打开 SQL DDL 文件，GUI 展示表结构。
// 编译： csc /target:winexe /optimize+ /out:SqlStructViewer.exe SqlStructViewer.cs
// 依赖： .NET Framework 4.x（Windows 10/11 自带），无需安装运行时。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace SqlStructViewer
{
    internal sealed class DbColumn
    {
        public string Name = "";
        public string Type = "";
        public string Comment = "";
        public bool NotNull;
        public bool IsPk;
    }

    internal sealed class DbTable
    {
        public string Name = "";
        public string Source = "";
        public string Description = "";
        public readonly List<DbColumn> Columns = new List<DbColumn>();
    }

    internal static class SqlParser
    {
        private static readonly Regex CreateRe = new Regex(
            "^\\s*CREATE\\s+(?:GLOBAL\\s+TEMPORARY\\s+|TEMPORARY\\s+|TEMP\\s+)?TABLE\\s+(?:IF\\s+NOT\\s+EXISTS\\s+)?(?<name>\"[^\"]+\"|`[^`]+`|\\[[^\\]]+\\]|[A-Za-z_][A-Za-z0-9_$#]*)",
            RegexOptions.IgnoreCase);

        private static readonly Regex PkRe = new Regex(
            "PRIMARY\\s+KEY\\s*\\((?<cols>[^)]*)\\)",
            RegexOptions.IgnoreCase);

        private static readonly Regex CommentOnRe = new Regex(
            "^\\s*COMMENT\\s+ON\\s+COLUMN\\s+(?<tbl>[A-Za-z0-9_$#\"\\[\\]`.]+)\\s+IS\\s+'(?<txt>(?:[^']|'')*)'",
            RegexOptions.IgnoreCase);

        private static readonly string[] Modifiers = new string[]
        {
            "NOT", "NULL", "DEFAULT", "PRIMARY", "UNIQUE", "REFERENCES", "CHECK",
            "CONSTRAINT", "COLLATE", "COMMENT", "GENERATED", "AUTO_INCREMENT",
            "IDENTITY", "STORAGE", "ENCODE", "DISTKEY", "SORTKEY", "WITH", "ON"
        };

        private static readonly string[] TableConstraint = new string[]
        {
            "PRIMARY", "UNIQUE", "FOREIGN", "CHECK", "CONSTRAINT", "KEY", "INDEX", "EXCLUDE"
        };

        public static string ReadAllTextAuto(string path)
        {
            byte[] b = File.ReadAllBytes(path);
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
                return Encoding.UTF8.GetString(b, 3, b.Length - 3);
            try
            {
                UTF8Encoding strict = new UTF8Encoding(false, true);
                return strict.GetString(b);
            }
            catch
            {
                try { return Encoding.GetEncoding(936).GetString(b); }
                catch { return Encoding.Default.GetString(b); }
            }
        }

        public static List<DbTable> Parse(string text)
        {
            List<DbTable> tables = new List<DbTable>();
            Dictionary<string, string> columnComments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            string norm = text.Replace("\r\n", "\n").Replace('\r', '\n');
            string[] lines = norm.Split('\n');

            // pass 1: COMMENT ON COLUMN ...
            for (int i = 0; i < lines.Length; i++)
            {
                Match cm = CommentOnRe.Match(lines[i]);
                if (cm.Success)
                {
                    string key = Unquote(cm.Groups["tbl"].Value);
                    string txt = cm.Groups["txt"].Value.Replace("''", "'");
                    columnComments[key] = txt;
                }
            }

            DbTable cur = null;
            List<string> pending = new List<string>();
            List<string> pkCols = new List<string>();
            int depth = 0;
            bool inBody = false;

            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].Trim();

                if (trimmed.Length == 0) continue;

                if (trimmed.StartsWith("--") || trimmed.StartsWith("//"))
                {
                    string c = trimmed.Substring(2).Trim();
                    if (!inBody && cur == null) { pending.Add(c); continue; }
                    if (!IsSqlCommentNoise(c)) pending.Add(c);
                    continue;
                }

                string work = StripTrailingComment(lines[i]);

                if (cur == null)
                {
                    Match cm = CreateRe.Match(work);
                    if (!cm.Success) continue;

                    cur = new DbTable();
                    cur.Name = Unquote(cm.Groups["name"].Value);
                    ApplyHeader(cur, pending);
                    pending.Clear();
                    pkCols.Clear();

                    work = work.Substring(cm.Index + cm.Length);
                    int open = work.IndexOf('(');
                    if (open < 0) { inBody = false; depth = 0; continue; }
                    inBody = true;
                    depth = 1;
                    work = work.Substring(open + 1);
                    if (work.Trim().Length == 0) continue;
                }
                else if (!inBody)
                {
                    int open = work.IndexOf('(');
                    if (open < 0) continue;
                    inBody = true;
                    depth = 1;
                    work = work.Substring(open + 1);
                    if (work.Trim().Length == 0) continue;
                }

                // consume the body, tracking paren depth across the remainder of the line
                int consumed = 0;
                List<string> parts = SplitTopLevel(work, depth, out consumed);
                depth = consumed;

                for (int p = 0; p < parts.Count; p++)
                {
                    string seg = parts[p].Trim();
                    if (seg.Length == 0) continue;
                    if (seg.StartsWith("--")) continue;

                    Match pk = PkRe.Match(seg);
                    bool isConstraint = StartsWithAny(seg, TableConstraint);

                    if (isConstraint)
                    {
                        if (pk.Success)
                        {
                            string[] names = pk.Groups["cols"].Value.Split(',');
                            for (int k = 0; k < names.Length; k++)
                            {
                                string n = Unquote(names[k].Trim());
                                if (n.Length > 0) pkCols.Add(n);
                            }
                        }
                        continue;
                    }

                    DbColumn col = ParseColumn(seg);
                    if (col != null) cur.Columns.Add(col);
                }

                // statement finished
                if (depth <= 0)
                {
                    FinishTable(cur, pkCols, columnComments);
                    tables.Add(cur);
                    cur = null;
                    pending.Clear();
                    inBody = false;
                    depth = 0;
                }
            }

            if (cur != null) { FinishTable(cur, pkCols, columnComments); tables.Add(cur); }
            return tables;
        }

        private static void FinishTable(DbTable t, List<string> pkCols, Dictionary<string, string> columnComments)
        {
            for (int i = 0; i < pkCols.Count; i++)
            {
                string key = t.Name + "." + pkCols[i];
                for (int c = 0; c < t.Columns.Count; c++)
                    if (string.Equals(t.Columns[c].Name, pkCols[i], StringComparison.OrdinalIgnoreCase))
                        t.Columns[c].IsPk = true;
                if (columnComments.ContainsKey(key)) { }
            }
            for (int c = 0; c < t.Columns.Count; c++)
            {
                string key = t.Name + "." + t.Columns[c].Name;
                if (columnComments.ContainsKey(key) && t.Columns[c].Comment.Length == 0)
                    t.Columns[c].Comment = columnComments[key];
            }
        }

        private static bool IsSqlCommentNoise(string c)
        {
            return c.StartsWith("====") || c.StartsWith("----");
        }

        private static void ApplyHeader(DbTable t, List<string> pending)
        {
            for (int i = 0; i < pending.Count; i++)
            {
                string s = pending[i];
                int k = s.IndexOf(':');
                if (k <= 0) continue;
                string key = s.Substring(0, k).Trim().ToLowerInvariant();
                string val = s.Substring(k + 1).Trim();
                if (key == "description" && val.Length > 0 && t.Description.Length == 0) t.Description = val;
                else if (key == "source" && val.Length > 0 && t.Source.Length == 0) t.Source = val;
            }
        }

        private static string StripTrailingComment(string line)
        {
            bool inS = false, inD = false;
            for (int i = 0; i < line.Length - 1; i++)
            {
                char ch = line[i];
                if (ch == '\'' && !inD) inS = !inS;
                else if (ch == '"' && !inS) inD = !inD;
                else if (ch == '-' && line[i + 1] == '-' && !inS && !inD) return line.Substring(0, i);
            }
            return line;
        }

        // Splits the inside of a CREATE TABLE body at top-level commas.
        // startDepth is the depth at entry (1 = directly inside the table parens).
        private static List<string> SplitTopLevel(string s, int startDepth, out int endDepth)
        {
            List<string> outParts = new List<string>();
            int d = startDepth;
            bool inS = false, inD = false, inB = false;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if (ch == '\'' && !inD && !inB) { inS = !inS; sb.Append(ch); continue; }
                if (ch == '"' && !inS && !inB) { inD = !inD; sb.Append(ch); continue; }
                if (!inS && !inD)
                {
                    if (ch == '[') { inB = true; sb.Append(ch); continue; }
                    if (ch == ']') { inB = false; sb.Append(ch); continue; }
                }
                if (!inS && !inD && !inB)
                {
                    if (ch == '(') { d++; sb.Append(ch); continue; }
                    if (ch == ')')
                    {
                        d--;
                        if (d < startDepth)
                        {
                            endDepth = d;
                            outParts.Add(sb.ToString());
                            return outParts;
                        }
                        sb.Append(ch);
                        continue;
                    }
                    if (ch == ',' && d == startDepth)
                    {
                        outParts.Add(sb.ToString());
                        sb.Length = 0;
                        continue;
                    }
                }
                sb.Append(ch);
            }
            endDepth = d;
            outParts.Add(sb.ToString());
            return outParts;
        }

        private static bool StartsWithAny(string seg, string[] words)
        {
            for (int i = 0; i < words.Length; i++)
            {
                if (seg.Length < words[i].Length) continue;
                if (string.Compare(seg, 0, words[i], 0, words[i].Length, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    if (seg.Length == words[i].Length) return true;
                    char nc = seg[words[i].Length];
                    if (!char.IsLetterOrDigit(nc) && nc != '_') return true;
                }
            }
            return false;
        }

        private static DbColumn ParseColumn(string seg)
        {
            List<string> tok = Tokenize(seg);
            if (tok.Count < 2) return null;
            if (tok[0] == ")" || tok[0] == "(") return null;

            DbColumn col = new DbColumn();
            col.Name = Unquote(tok[0]);
            if (col.Name.Length == 0) return null;

            col.Type = tok[1];
            int i = 2;
            while (i < tok.Count && !IsModifier(tok[i]))
            {
                if (tok[i].StartsWith("(")) col.Type += tok[i];
                else if (col.Type.EndsWith(")")) break;
                else col.Type += " " + tok[i];
                i++;
            }
            for (; i < tok.Count; i++)
            {
                string u = tok[i].ToUpperInvariant();
                if (u == "NOT")
                {
                    if (i + 1 < tok.Count && tok[i + 1].Equals("NULL", StringComparison.OrdinalIgnoreCase))
                    { col.NotNull = true; i++; }
                }
                else if (u == "PRIMARY")
                {
                    if (i + 1 < tok.Count && tok[i + 1].Equals("KEY", StringComparison.OrdinalIgnoreCase))
                    { col.IsPk = true; i++; }
                }
            }
            return col;
        }

        private static bool IsModifier(string t)
        {
            for (int i = 0; i < Modifiers.Length; i++)
                if (string.Equals(t, Modifiers[i], StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static List<string> Tokenize(string s)
        {
            List<string> outTok = new List<string>();
            StringBuilder sb = new StringBuilder();
            bool inS = false, inD = false, inB = false;
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if (ch == '\'' && !inD && !inB) { inS = !inS; sb.Append(ch); continue; }
                if (ch == '"' && !inS && !inB) { inD = !inD; sb.Append(ch); continue; }
                if (!inS && !inD)
                {
                    if (ch == '[') { inB = true; sb.Append(ch); continue; }
                    if (ch == ']') { inB = false; sb.Append(ch); continue; }
                }
                if (!inS && !inD && !inB && char.IsWhiteSpace(ch))
                {
                    if (sb.Length > 0) { outTok.Add(sb.ToString()); sb.Length = 0; }
                    continue;
                }
                sb.Append(ch);
            }
            if (sb.Length > 0) outTok.Add(sb.ToString());
            return outTok;
        }

        private static string Unquote(string s)
        {
            s = s.Trim();
            if (s.Length >= 2)
            {
                char a = s[0], b = s[s.Length - 1];
                if ((a == '"' && b == '"') || (a == '`' && b == '`') || (a == '[' && b == ']'))
                    return s.Substring(1, s.Length - 2).Trim();
            }
            int dot = s.LastIndexOf('.');
            if (dot >= 0 && dot < s.Length - 1) return s.Substring(dot + 1).Trim();
            return s;
        }
    }

    // 现代文件夹选择器（IFileOpenDialog + FOS_PICKFOLDERS）。
    // 与旧的 FolderBrowserDialog 不同，这个对话框有地址栏，可以直接 Ctrl+V 粘贴路径，
    // 且支持多选文件夹。Windows Vista 及以上均可用。
    internal static class FolderPicker
    {
        private const uint FOS_PICKFOLDERS = 0x00000020;
        private const uint FOS_FORCEFILESYSTEM = 0x00000040;
        private const uint FOS_ALLOWMULTISELECT = 0x00000200;
        private const uint FOS_PATHMUSTEXIST = 0x00000800;
        private const uint FOS_NOCHANGEDIR = 0x00000008;
        private const uint SIGDN_FILESYSPATH = 0x80058000;

        private static readonly Guid CLSID_FileOpenDialog =
            new Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");

        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileDialog
        {
            [PreserveSig] int Show(IntPtr hwndParent);
            void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
            void SetFileTypeIndex(uint iFileType);
            void GetFileTypeIndex(out uint piFileType);
            void Advise(IntPtr pfde, out uint pdwCookie);
            void Unadvise(uint dwCookie);
            void SetOptions(uint fos);
            void GetOptions(out uint pfos);
            void SetDefaultFolder(IShellItem psi);
            void SetFolder(IShellItem psi);
            void GetFolder(out IShellItem ppsi);
            void GetCurrentSelection(out IShellItem ppsi);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
            void GetResult(out IShellItem ppsi);
            void AddPlace(IShellItem psi, int fdap);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr pFilter);
        }

        [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
        {
            // IFileDialog 的 24 个方法（顺序必须与 vtable 一致）
            [PreserveSig] int Show(IntPtr hwndParent);
            void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
            void SetFileTypeIndex(uint iFileType);
            void GetFileTypeIndex(out uint piFileType);
            void Advise(IntPtr pfde, out uint pdwCookie);
            void Unadvise(uint dwCookie);
            void SetOptions(uint fos);
            void GetOptions(out uint pfos);
            void SetDefaultFolder(IShellItem psi);
            void SetFolder(IShellItem psi);
            void GetFolder(out IShellItem ppsi);
            void GetCurrentSelection(out IShellItem ppsi);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
            void GetResult(out IShellItem ppsi);
            void AddPlace(IShellItem psi, int fdap);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr pFilter);
            // IFileOpenDialog 新增
            void GetResults(out IShellItemArray ppenum);
            void GetSelectedItems(out IShellItemArray ppsai);
        }

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }

        [ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemArray
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
            void GetPropertyDescriptionList(IntPtr keyType, ref Guid riid, out IntPtr ppv);
            void GetAttributes(int attribFlags, uint sfgaoMask, out uint psfgaoAttribs);
            void GetCount(out uint pdwNumItems);
            void GetItemAt(uint dwIndex, out IShellItem ppsi);
            void EnumItems(out IntPtr ppenumShellItems);
        }

        // 返回所选文件夹的完整路径；用户取消或调用失败时返回 null（调用方回退到旧对话框）。
        public static string[] Pick(IWin32Window owner, string title)
        {
            IFileOpenDialog dlg = null;
            try
            {
                object o = Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_FileOpenDialog));
                dlg = (IFileOpenDialog)o;
                uint opt;
                dlg.GetOptions(out opt);
                dlg.SetOptions(opt | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST |
                               FOS_ALLOWMULTISELECT | FOS_NOCHANGEDIR);
                if (title != null && title.Length > 0) dlg.SetTitle(title);

                int hr = dlg.Show(owner == null ? IntPtr.Zero : owner.Handle);
                if (hr != 0) return new string[0];   // 用户取消

                IShellItemArray arr;
                dlg.GetResults(out arr);
                if (arr == null) return new string[0];
                uint n;
                arr.GetCount(out n);
                List<string> res = new List<string>();
                for (uint i = 0; i < n; i++)
                {
                    IShellItem it = null;
                    try
                    {
                        arr.GetItemAt(i, out it);
                        if (it == null) continue;
                        string p;
                        it.GetDisplayName(SIGDN_FILESYSPATH, out p);
                        if (p != null && p.Length > 0) res.Add(p);
                    }
                    finally { if (it != null) Marshal.ReleaseComObject(it); }
                }
                Marshal.ReleaseComObject(arr);
                return res.ToArray();
            }
            catch { return null; }
            finally { if (dlg != null) Marshal.ReleaseComObject(dlg); }
        }
    }

    internal static class AppInfo
    {
        public const string Name = "SQL 表结构查看器";
        public const string NameEn = "SQL Structure Viewer";
        public const string Version = "1.0.0";
    }

    internal enum UiLang { Zh, En }

    // 全部界面文案集中在此。加语言只需在此新增一个分支。
    internal static class S
    {
        public static UiLang Lang = UiLang.Zh;

        public static bool IsZh { get { return Lang == UiLang.Zh; } }

        private static string P(string zh, string en) { return Lang == UiLang.Zh ? zh : en; }

        // ---------- 语言偏好持久化 ----------
        private static string PrefDir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "SqlStructViewer");
            }
        }

        private static string PrefFile { get { return Path.Combine(PrefDir, "lang.txt"); } }

        public static bool TryParseLang(string s, out UiLang lang)
        {
            lang = UiLang.Zh;
            if (s == null) return false;
            s = s.Trim().ToLowerInvariant();
            if (s == "zh" || s == "zh-cn" || s == "cn" || s == "chinese") { lang = UiLang.Zh; return true; }
            if (s == "en" || s == "en-us" || s == "english") { lang = UiLang.En; return true; }
            return false;
        }

        // 优先读取已保存的偏好；没有则按系统界面语言自动判断
        public static void LoadPreference()
        {
            try
            {
                if (File.Exists(PrefFile))
                {
                    UiLang saved;
                    string text = File.ReadAllText(PrefFile).Trim();
                    if (TryParseLang(text, out saved)) { Lang = saved; return; }
                }
            }
            catch { }
            try
            {
                Lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh" ? UiLang.Zh : UiLang.En;
            }
            catch { Lang = UiLang.En; }
        }

        public static void SavePreference()
        {
            try
            {
                Directory.CreateDirectory(PrefDir);
                File.WriteAllText(PrefFile, IsZh ? "zh" : "en");
            }
            catch { }
        }

        // ---------- 名称 ----------
        public static string AppName { get { return P(AppInfo.Name, AppInfo.NameEn); } }
        public static string AppNameOther { get { return P(AppInfo.NameEn, AppInfo.Name); } }

        // ---------- 菜单 ----------
        public static string MenuFile { get { return P("文件(&F)", "&File"); } }
        public static string MenuOpen { get { return P("打开 SQL 文件...(&O)", "&Open SQL File..."); } }
        public static string MenuFolder { get { return P("打开文件夹...(&D)", "Open &Folder..."); } }
        public static string MenuPath { get { return P("从路径打开...(&P)", "Open from &Path..."); } }
        public static string MenuCloseAll { get { return P("关闭全部(&C)", "&Close All"); } }
        public static string MenuExit { get { return P("退出(&X)", "E&xit"); } }
        public static string MenuHelp { get { return P("帮助(&H)", "&Help"); } }
        public static string MenuAbout { get { return P("关于(&A)", "&About"); } }
        public static string MenuLanguage { get { return P("语言(&L)", "&Language"); } }
        public static string MenuLangZh { get { return "中文"; } }
        public static string MenuLangEn { get { return "English"; } }

        // ---------- 工具栏 ----------
        public static string TbOpenFile { get { return P("打开文件", "Open File"); } }
        public static string TbOpenFolder { get { return P("打开文件夹", "Open Folder"); } }
        public static string TbOpenPath { get { return P("打开路径", "Open Path"); } }
        public static string TbOpenPathTip { get { return P("打开「路径」框中的文件或文件夹", "Open the file or folder in the Path box"); } }
        public static string TbPathLabel { get { return P("路径:", "Path:"); } }
        public static string TbPathTip
        {
            get
            {
                return P("在此粘贴文件或文件夹路径，按回车打开（支持含引号、分号的多个路径）",
                         "Paste a file or folder path and press Enter (several allowed, separated by ; or newlines)");
            }
        }
        public static string TbFilterLabel { get { return P("过滤:", "Filter:"); } }
        public static string TbFilterTip { get { return P("按表名过滤", "Filter by table name"); } }

        // ---------- 表清单列 ----------
        public static string ColTable { get { return P("表名", "Table"); } }
        public static string ColCols { get { return P("字段", "Cols"); } }
        public static string ColPk { get { return P("主键", "PK"); } }
        public static string ColSource { get { return P("来源", "Source"); } }

        // ---------- 字段网格列 ----------
        public static string ColNo { get { return P("#", "#"); } }
        public static string ColName { get { return P("字段名", "Column"); } }
        public static string ColType { get { return P("类型", "Type"); } }
        public static string ColNull { get { return P("空", "Null"); } }
        public static string ColPkMark { get { return P("PK", "PK"); } }
        public static string ColComment { get { return P("注释", "Comment"); } }

        // ---------- 上下文菜单 ----------
        public static string CtxCopySelected { get { return P("复制选中行", "Copy Selected Rows"); } }
        public static string CtxCopyAll { get { return P("复制全部行", "Copy All Rows"); } }

        // ---------- 头部与状态 ----------
        public static string NoFile { get { return P("未打开文件", "No file opened"); } }
        public static string NoMatch { get { return P("无匹配的表", "No matching table"); } }
        public static string ColumnUnit { get { return P(" 字段", " columns"); } }
        public static string Ready
        {
            get
            {
                return P("就绪。可拖放 .sql 文件到窗口，或在「路径」框粘贴路径后回车。",
                         "Ready. Drop .sql files onto the window, or paste a path into the Path box and press Enter.");
            }
        }
        public static string NoSqlFound { get { return P("未找到 .sql 文件。", "No .sql files found."); } }
        public static string FolderNoSql { get { return P("所选文件夹下没有 .sql 文件。", "No .sql files in the selected folder."); } }
        public static string PasteHint { get { return P("请在「路径」框粘贴文件或文件夹路径。", "Paste a file or folder path into the Path box."); } }
        public static string PathInvalid { get { return P("路径不存在或其中没有可读文件：", "Path not found or contains no readable files: "); } }
        public static string Cleared { get { return P("已清空。", "Cleared."); } }
        public static string LoadedFmt { get { return P("已载入 {0} 张表 / {1} 个字段", "Loaded {0} tables / {1} columns"); } }
        public static string FailedFmt { get { return P("（{0} 个文件读取失败）", " ({0} file(s) failed to load)"); } }
        public static string FilesFmt { get { return P("{0} 个文件", "{0} files"); } }
        public static string MissingFmt { get { return P("（{0} 个路径不存在）", " ({0} path(s) not found)"); } }
        public static string CopiedFmt { get { return P("已复制 {0} 行到剪贴板。", "Copied {0} rows to clipboard."); } }
        public static string StatsFmt { get { return P("表 {0} / {1}    字段 {2}", "Tables {0} / {1}    Columns {2}"); } }
        public static string OthersFmt { get { return P("{0} 个文件", "{0} files"); } }
        public static string ClipboardFail { get { return P("复制失败。", "Copy failed."); } }

        // ---------- 对话框 ----------
        public static string DlgOpenTitle { get { return P("选择 SQL DDL 文件", "Select SQL DDL Files"); } }
        public static string DlgOpenFilter
        {
            get
            {
                return P("SQL 文件 (*.sql;*.ddl;*.txt)|*.sql;*.ddl;*.txt|所有文件 (*.*)|*.*",
                         "SQL files (*.sql;*.ddl;*.txt)|*.sql;*.ddl;*.txt|All files (*.*)|*.*");
            }
        }
        public static string DlgFolderTitle
        {
            get
            {
                return P("选择包含 .sql 文件的文件夹（地址栏可 Ctrl+V 粘贴路径，支持多选）",
                         "Select folders containing .sql files (paste a path into the address bar; multi-select supported)");
            }
        }
        public static string DlgFolderDesc
        {
            get { return P("选择包含 .sql 文件的文件夹", "Select a folder containing .sql files"); }
        }
        public static string AboutTitle { get { return P("关于", "About"); } }

        public static string AboutBody
        {
            get
            {
                string head = AppName + "  v" + AppInfo.Version + "\r\n" + AppNameOther + "\r\n\r\n";
                if (IsZh)
                {
                    return head +
                        "打开 SQL DDL 文件（CREATE TABLE），以表格展示字段名、类型、可空、主键与注释。\r\n\r\n" +
                        "打开方式：\r\n" +
                        "  • Ctrl+O  打开文件（可多选）\r\n" +
                        "  • 「路径」框粘贴文件或文件夹路径，回车打开；可一次粘贴多个（用 ; 或换行分隔），带空格的路径可加引号\r\n" +
                        "  • 「打开文件夹」对话框带地址栏，可在其中 Ctrl+V 粘贴路径，并支持多选文件夹\r\n" +
                        "  • 直接把文件或文件夹拖进窗口\r\n\r\n" +
                        "过滤框按表名筛选；右侧列表 Ctrl+C 复制选中行，Ctrl+A 全选。\r\n" +
                        "菜单「语言」可切换中文 / English。\r\n\r\n" +
                        "单文件独立 EXE，无需安装。\r\nMIT License";
                }
                return head +
                    "Opens SQL DDL (CREATE TABLE) files and shows column name, type, nullability, primary key and comments in a grid.\r\n\r\n" +
                    "How to open:\r\n" +
                    "  - Ctrl+O  open files (multi-select)\r\n" +
                    "  - Paste a file or folder path into the Path box and press Enter; several paths allowed (separated by ; or newlines); quote paths that contain spaces\r\n" +
                    "  - The Open Folder dialog has an address bar where you can paste a path; multi-select is supported\r\n" +
                    "  - Drag files or folders straight onto the window\r\n\r\n" +
                    "The Filter box narrows the table list; Ctrl+C copies selected rows, Ctrl+A selects all.\r\n" +
                    "Use the Language menu to switch between English and Chinese.\r\n\r\n" +
                    "Single-file standalone EXE, no installation required.\r\nMIT License";
            }
        }
    }

    internal sealed class MainForm : Form
    {
        private MenuStrip menu;
        private ToolStrip tool;
        private ToolStripButton btnOpenFile;
        private ToolStripButton btnOpenFolder;
        private ToolStripButton btnGo;
        private ToolStripTextBox txtPath;
        private ToolStripTextBox txtFilter;
        private ToolStripLabel lblStats;
        private SplitContainer split;
        private ListView lstTables;
        private ListView lstColumns;
        private Label lblTableHeader;
        private StatusStrip status;
        private ToolStripStatusLabel lblStatus;
        private ContextMenuStrip ctxColumns;

        // 需要在切换语言时重新设置文案的控件
        private ToolStripMenuItem miFile;
        private ToolStripMenuItem miOpen;
        private ToolStripMenuItem miFolder;
        private ToolStripMenuItem miPath;
        private ToolStripMenuItem miClose;
        private ToolStripMenuItem miExit;
        private ToolStripMenuItem miHelp;
        private ToolStripMenuItem miAbout;
        private ToolStripMenuItem miLanguage;
        private ToolStripMenuItem miLangZh;
        private ToolStripMenuItem miLangEn;
        private ToolStripLabel lblPathCaption;
        private ToolStripLabel lblFilterCaption;
        private ToolStripMenuItem miCopySelected;
        private ToolStripMenuItem miCopyAll;

        private readonly List<DbTable> allTables = new List<DbTable>();
        private string currentPath = "";
        private int lastFailed;

        public MainForm(string[] args)
        {
            Text = S.AppName + " v" + AppInfo.Version;
            Width = 1180;
            Height = 720;
            StartPosition = FormStartPosition.CenterScreen;
            AllowDrop = true;
            try { Font = new Font("Microsoft YaHei UI", 9f); }
            catch { Font = SystemFonts.MessageBoxFont; }

            BuildMenu();
            BuildToolbar();
            BuildBody();
            BuildStatus();

            DragEnter += new DragEventHandler(OnDragEnter);
            DragDrop += new DragEventHandler(OnDragDrop);

            if (args != null && args.Length > 0)
            {
                List<string> files = new List<string>();
                for (int i = 0; i < args.Length; i++)
                {
                    if (Directory.Exists(args[i])) files.AddRange(CollectSql(args[i]));
                    else if (File.Exists(args[i]) && IsSqlLike(args[i])) files.Add(args[i]);
                }
                if (files.Count > 0) LoadFiles(files.ToArray());
            }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // 必须在窗口获得真实宽度后再设置，否则 SplitContainer 会因
            // Panel1MinSize/Panel2MinSize 与当前 Width 冲突而抛异常。
            try
            {
                split.Panel1MinSize = 160;
                split.Panel2MinSize = 240;
                int max = split.Width - split.Panel2MinSize;
                if (max < split.Panel1MinSize) max = split.Panel1MinSize;
                int want = 340;
                if (want < split.Panel1MinSize) want = split.Panel1MinSize;
                if (want > max) want = max;
                split.SplitterDistance = want;
            }
            catch { }
        }

        private void BuildMenu()
        {
            menu = new MenuStrip();

            miFile = new ToolStripMenuItem(S.MenuFile);
            miOpen = new ToolStripMenuItem(S.MenuOpen, null, new EventHandler(delegate { OpenFiles(); }));
            miOpen.ShortcutKeys = Keys.Control | Keys.O;
            miFolder = new ToolStripMenuItem(S.MenuFolder, null, new EventHandler(delegate { OpenFolder(); }));
            miFolder.ShortcutKeys = Keys.Control | Keys.Shift | Keys.D;
            miPath = new ToolStripMenuItem(S.MenuPath, null, new EventHandler(delegate { FocusPathBox(); }));
            miPath.ShortcutKeys = Keys.Control | Keys.Shift | Keys.O;
            miClose = new ToolStripMenuItem(S.MenuCloseAll, null, new EventHandler(delegate { CloseAll(); }));
            miExit = new ToolStripMenuItem(S.MenuExit, null, new EventHandler(delegate { Close(); }));
            miFile.DropDownItems.Add(miOpen);
            miFile.DropDownItems.Add(miFolder);
            miFile.DropDownItems.Add(miPath);
            miFile.DropDownItems.Add(new ToolStripSeparator());
            miFile.DropDownItems.Add(miClose);
            miFile.DropDownItems.Add(miExit);

            miHelp = new ToolStripMenuItem(S.MenuHelp);
            miAbout = new ToolStripMenuItem(S.MenuAbout, null, new EventHandler(delegate { ShowAbout(); }));
            miHelp.DropDownItems.Add(miAbout);

            // 语言菜单：两个条目始终用各自的语言显示自己的名字
            miLanguage = new ToolStripMenuItem(S.MenuLanguage);
            miLangZh = new ToolStripMenuItem(S.MenuLangZh, null, new EventHandler(delegate { SwitchLanguage(UiLang.Zh); }));
            miLangEn = new ToolStripMenuItem(S.MenuLangEn, null, new EventHandler(delegate { SwitchLanguage(UiLang.En); }));
            miLanguage.DropDownItems.Add(miLangZh);
            miLanguage.DropDownItems.Add(miLangEn);
            miLangZh.Checked = S.IsZh;
            miLangEn.Checked = !S.IsZh;

            menu.Items.Add(miFile);
            menu.Items.Add(miLanguage);
            menu.Items.Add(miHelp);
            MainMenuStrip = menu;
            Controls.Add(menu);
        }

        private void SwitchLanguage(UiLang lang)
        {
            if (S.Lang == lang) return;
            S.Lang = lang;
            S.SavePreference();
            ApplyLanguage();
        }

        // 切换语言后重新设置所有静态文案，并重绘动态内容
        private void ApplyLanguage()
        {
            Text = S.AppName + " v" + AppInfo.Version;

            miFile.Text = S.MenuFile;
            miOpen.Text = S.MenuOpen;
            miFolder.Text = S.MenuFolder;
            miPath.Text = S.MenuPath;
            miClose.Text = S.MenuCloseAll;
            miExit.Text = S.MenuExit;
            miHelp.Text = S.MenuHelp;
            miAbout.Text = S.MenuAbout;
            miLanguage.Text = S.MenuLanguage;
            miLangZh.Checked = S.IsZh;
            miLangEn.Checked = !S.IsZh;

            btnOpenFile.Text = S.TbOpenFile;
            btnOpenFolder.Text = S.TbOpenFolder;
            btnGo.Text = S.TbOpenPath;
            btnGo.ToolTipText = S.TbOpenPathTip;
            lblPathCaption.Text = S.TbPathLabel;
            lblFilterCaption.Text = S.TbFilterLabel;
            txtPath.ToolTipText = S.TbPathTip;
            txtFilter.ToolTipText = S.TbFilterTip;

            lstTables.Columns[0].Text = S.ColTable;
            lstTables.Columns[1].Text = S.ColCols;
            lstTables.Columns[2].Text = S.ColPk;
            lstTables.Columns[3].Text = S.ColSource;

            lstColumns.Columns[0].Text = S.ColNo;
            lstColumns.Columns[1].Text = S.ColName;
            lstColumns.Columns[2].Text = S.ColType;
            lstColumns.Columns[3].Text = S.ColNull;
            lstColumns.Columns[4].Text = S.ColPkMark;
            lstColumns.Columns[5].Text = S.ColComment;

            miCopySelected.Text = S.CtxCopySelected;
            miCopyAll.Text = S.CtxCopyAll;

            if (allTables.Count == 0) lblTableHeader.Text = S.NoFile;
            RefreshTableList();
            UpdateStatusSummary();
        }

        private void UpdateStatusSummary()
        {
            if (allTables.Count == 0) { lblStatus.Text = S.Ready; return; }
            string msg = string.Format(S.LoadedFmt, allTables.Count, CountColumns());
            if (lastFailed > 0) msg += string.Format(S.FailedFmt, lastFailed);
            lblStatus.Text = msg + "   " + currentPath;
        }

        private void BuildToolbar()
        {
            tool = new ToolStrip();
            tool.GripStyle = ToolStripGripStyle.Hidden;
            tool.ImageScalingSize = new Size(16, 16);

            btnOpenFile = new ToolStripButton(S.TbOpenFile);
            btnOpenFile.DisplayStyle = ToolStripItemDisplayStyle.Text;
            btnOpenFile.Click += new EventHandler(delegate { OpenFiles(); });

            btnOpenFolder = new ToolStripButton(S.TbOpenFolder);
            btnOpenFolder.DisplayStyle = ToolStripItemDisplayStyle.Text;
            btnOpenFolder.Click += new EventHandler(delegate { OpenFolder(); });

            btnGo = new ToolStripButton(S.TbOpenPath);
            btnGo.DisplayStyle = ToolStripItemDisplayStyle.Text;
            btnGo.ToolTipText = S.TbOpenPathTip;
            btnGo.Click += new EventHandler(delegate { OpenPathText(txtPath.Text); });

            txtPath = new ToolStripTextBox();
            txtPath.AutoSize = false;
            txtPath.Width = 300;
            txtPath.ToolTipText = S.TbPathTip;

            txtFilter = new ToolStripTextBox();
            txtFilter.AutoSize = false;
            txtFilter.Width = 220;
            txtFilter.ToolTipText = S.TbFilterTip;
            txtFilter.TextChanged += new EventHandler(delegate { RefreshTableList(); });

            lblPathCaption = new ToolStripLabel(S.TbPathLabel);
            lblFilterCaption = new ToolStripLabel(S.TbFilterLabel);
            lblStats = new ToolStripLabel("");
            lblStats.Alignment = ToolStripItemAlignment.Right;

            tool.Items.Add(btnOpenFile);
            tool.Items.Add(btnOpenFolder);
            tool.Items.Add(new ToolStripSeparator());
            tool.Items.Add(lblPathCaption);
            tool.Items.Add(txtPath);
            tool.Items.Add(btnGo);
            tool.Items.Add(new ToolStripSeparator());
            tool.Items.Add(lblFilterCaption);
            tool.Items.Add(txtFilter);
            tool.Items.Add(lblStats);
            Controls.Add(tool);

            // ToolStripTextBox 的 Enter 会被 ToolStrip 拦截，必须挂到底层 TextBox 上
            if (txtPath.Control != null)
            {
                txtPath.Control.KeyDown += new KeyEventHandler(OnPathKeyDown);
                txtPath.Control.DoubleClick += new EventHandler(delegate { txtPath.SelectAll(); });
            }
        }

        private void BuildBody()
        {
            split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.Orientation = Orientation.Vertical;

            lstTables = new ListView();
            lstTables.Dock = DockStyle.Fill;
            lstTables.View = View.Details;
            lstTables.FullRowSelect = true;
            lstTables.HideSelection = false;
            lstTables.MultiSelect = false;
            lstTables.Columns.Add(S.ColTable, 200);
            lstTables.Columns.Add(S.ColCols, 50, HorizontalAlignment.Right);
            lstTables.Columns.Add(S.ColPk, 110);
            lstTables.Columns.Add(S.ColSource, 60);
            lstTables.SelectedIndexChanged += new EventHandler(delegate { ShowSelectedTable(); });
            split.Panel1.Controls.Add(lstTables);

            lblTableHeader = new Label();
            lblTableHeader.Dock = DockStyle.Top;
            lblTableHeader.Height = 46;
            lblTableHeader.TextAlign = ContentAlignment.MiddleLeft;
            lblTableHeader.Padding = new Padding(8, 0, 8, 0);
            lblTableHeader.BackColor = SystemColors.ControlLightLight;
            lblTableHeader.Text = S.NoFile;

            lstColumns = new ListView();
            lstColumns.Dock = DockStyle.Fill;
            lstColumns.View = View.Details;
            lstColumns.FullRowSelect = true;
            lstColumns.GridLines = true;
            lstColumns.MultiSelect = true;
            lstColumns.Columns.Add(S.ColNo, 44, HorizontalAlignment.Right);
            lstColumns.Columns.Add(S.ColName, 200);
            lstColumns.Columns.Add(S.ColType, 130);
            lstColumns.Columns.Add(S.ColNull, 50);
            lstColumns.Columns.Add(S.ColPkMark, 40);
            lstColumns.Columns.Add(S.ColComment, 520);
            lstColumns.KeyDown += new KeyEventHandler(OnColumnsKeyDown);

            ctxColumns = new ContextMenuStrip();
            miCopySelected = new ToolStripMenuItem(S.CtxCopySelected, null, new EventHandler(delegate { CopySelectedColumns(false); }));
            miCopyAll = new ToolStripMenuItem(S.CtxCopyAll, null, new EventHandler(delegate { CopySelectedColumns(true); }));
            ctxColumns.Items.Add(miCopySelected);
            ctxColumns.Items.Add(miCopyAll);
            lstColumns.ContextMenuStrip = ctxColumns;

            split.Panel2.Controls.Add(lstColumns);
            split.Panel2.Controls.Add(lblTableHeader);

            Controls.Add(split);
            split.BringToFront();
        }

        private void BuildStatus()
        {
            status = new StatusStrip();
            lblStatus = new ToolStripStatusLabel(S.Ready);
            lblStatus.Spring = true;
            lblStatus.TextAlign = ContentAlignment.MiddleLeft;
            status.Items.Add(lblStatus);
            Controls.Add(status);
        }

        private void OnDragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
        }

        private void OnDragDrop(object sender, DragEventArgs e)
        {
            string[] paths = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (paths == null || paths.Length == 0) return;
            List<string> files = new List<string>();
            for (int i = 0; i < paths.Length; i++)
            {
                if (Directory.Exists(paths[i])) files.AddRange(CollectSql(paths[i]));
                else if (IsSqlLike(paths[i])) files.Add(paths[i]);
            }
            if (files.Count == 0) { lblStatus.Text = S.NoSqlFound; return; }
            LoadFiles(files.ToArray());
        }

        private static bool IsSqlLike(string p)
        {
            string e = Path.GetExtension(p).ToLowerInvariant();
            return e == ".sql" || e == ".ddl" || e == ".txt";
        }

        private static List<string> CollectSql(string dir)
        {
            List<string> res = new List<string>();
            try
            {
                string[] fs = Directory.GetFiles(dir);
                Array.Sort(fs, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < fs.Length; i++) if (IsSqlLike(fs[i])) res.Add(fs[i]);
            }
            catch { }
            return res;
        }

        private void OpenFiles()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = S.DlgOpenTitle;
                d.Filter = S.DlgOpenFilter;
                d.Multiselect = true;
                if (d.ShowDialog(this) == DialogResult.OK) LoadFiles(d.FileNames);
            }
        }

        private void OpenFolder()
        {
            // 现代选择器：带地址栏，可直接 Ctrl+V 粘贴路径，且支持多选
            string[] picked = FolderPicker.Pick(this, S.DlgFolderTitle);
            if (picked == null)
            {
                // 仅在 COM 现代对话框不可用时才回退到旧式树形对话框
                using (FolderBrowserDialog d = new FolderBrowserDialog())
                {
                    d.Description = S.DlgFolderDesc;
                    if (d.ShowDialog(this) != DialogResult.OK) return;
                    picked = new string[] { d.SelectedPath };
                }
            }
            if (picked.Length == 0) return;   // 用户取消

            txtPath.Text = picked[0];
            List<string> files = new List<string>();
            for (int i = 0; i < picked.Length; i++) files.AddRange(CollectSql(picked[i]));
            if (files.Count == 0) { lblStatus.Text = S.FolderNoSql; return; }
            LoadFiles(files.ToArray());
        }

        private void FocusPathBox()
        {
            txtPath.Focus();
            txtPath.SelectAll();
        }

        private void OnPathKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                OpenPathText(txtPath.Text);
            }
        }

        private static string StripQuotes(string s)
        {
            s = s.Trim();
            if (s.Length >= 2)
            {
                char a = s[0], b = s[s.Length - 1];
                if ((a == '"' && b == '"') || (a == '\'' && b == '\'')) s = s.Substring(1, s.Length - 2).Trim();
            }
            return s;
        }

        // 把粘贴进来的文本拆成候选路径：优先整体当一个路径（可含空格），
        // 否则按 ; / 换行 拆分，并去掉两侧引号。
        private static List<string> SplitPaths(string text)
        {
            List<string> res = new List<string>();
            if (text == null) return res;
            string t = text.Trim();
            if (t.Length == 0) return res;

            string whole = StripQuotes(t);
            if (whole.Length > 0 && (File.Exists(whole) || Directory.Exists(whole)))
            {
                res.Add(whole);
                return res;
            }
            string[] parts = t.Split(new char[] { ';', '\r', '\n' });
            for (int i = 0; i < parts.Length; i++)
            {
                string p = StripQuotes(parts[i]);
                if (p.Length > 0) res.Add(p);
            }
            return res;
        }

        private void OpenPathText(string text)
        {
            List<string> cand = SplitPaths(text);
            if (cand.Count == 0) { lblStatus.Text = S.PasteHint; return; }

            List<string> files = new List<string>();
            List<string> missing = new List<string>();
            for (int i = 0; i < cand.Count; i++)
            {
                string p = cand[i];
                if (Directory.Exists(p)) files.AddRange(CollectSql(p));
                else if (File.Exists(p)) files.Add(p);
                else missing.Add(p);
            }
            if (files.Count == 0)
            {
                lblStatus.Text = S.PathInvalid + string.Join("; ", missing.ToArray());
                return;
            }
            LoadFiles(files.ToArray());
            if (missing.Count > 0) lblStatus.Text += "    " + string.Format(S.MissingFmt, missing.Count);
        }

        private void CloseAll()
        {
            allTables.Clear();
            currentPath = "";
            lstTables.Items.Clear();
            lstColumns.Items.Clear();
            lblTableHeader.Text = S.NoFile;
            lblStatus.Text = S.Cleared;
            RefreshStats();
        }

        private void LoadFiles(string[] files)
        {
            allTables.Clear();
            int failed = 0;
            for (int i = 0; i < files.Length; i++)
            {
                try
                {
                    string text = SqlParser.ReadAllTextAuto(files[i]);
                    List<DbTable> ts = SqlParser.Parse(text);
                    for (int k = 0; k < ts.Count; k++) allTables.Add(ts[k]);
                }
                catch { failed++; }
            }
            currentPath = files.Length == 1 ? files[0] : string.Format(S.FilesFmt, files.Length);
            lastFailed = failed;
            RefreshTableList();
            string msg = string.Format(S.LoadedFmt, allTables.Count, CountColumns());
            if (failed > 0) msg += string.Format(S.FailedFmt, failed);
            lblStatus.Text = msg + "   " + currentPath;
            if (lstTables.Items.Count > 0) lstTables.Items[0].Selected = true;
        }

        private int CountColumns()
        {
            int n = 0;
            for (int i = 0; i < allTables.Count; i++) n += allTables[i].Columns.Count;
            return n;
        }

        private void RefreshTableList()
        {
            string filter = txtFilter == null ? "" : txtFilter.Text.Trim();
            lstTables.BeginUpdate();
            lstTables.Items.Clear();
            for (int i = 0; i < allTables.Count; i++)
            {
                DbTable t = allTables[i];
                if (filter.Length > 0 && t.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                ListViewItem it = new ListViewItem(t.Name);
                it.SubItems.Add(t.Columns.Count.ToString());
                it.SubItems.Add(JoinPk(t));
                it.SubItems.Add(t.Source);
                it.Tag = t;
                lstTables.Items.Add(it);
            }
            lstTables.EndUpdate();
            RefreshStats();
            if (lstTables.Items.Count > 0) lstTables.Items[0].Selected = true;
            else { lstColumns.Items.Clear(); lblTableHeader.Text = S.NoMatch; }
        }

        private static string JoinPk(DbTable t)
        {
            List<string> pk = new List<string>();
            for (int i = 0; i < t.Columns.Count; i++) if (t.Columns[i].IsPk) pk.Add(t.Columns[i].Name);
            return string.Join(", ", pk.ToArray());
        }

        private void RefreshStats()
        {
            if (lblStats == null) return;
            lblStats.Text = string.Format(S.StatsFmt, lstTables.Items.Count, allTables.Count, CountColumns());
        }

        private void ShowSelectedTable()
        {
            if (lstTables.SelectedItems.Count == 0) { lstColumns.Items.Clear(); return; }
            DbTable t = (DbTable)lstTables.SelectedItems[0].Tag;
            string head = t.Name;
            if (t.Source.Length > 0) head += "   [" + t.Source + "]";
            head += "   " + t.Columns.Count + S.ColumnUnit;
            if (t.Description.Length > 0) head += "\r\n" + t.Description;
            lblTableHeader.Text = head;

            lstColumns.BeginUpdate();
            lstColumns.Items.Clear();
            for (int i = 0; i < t.Columns.Count; i++)
            {
                DbColumn c = t.Columns[i];
                ListViewItem it = new ListViewItem((i + 1).ToString());
                it.SubItems.Add(c.Name);
                it.SubItems.Add(c.Type);
                it.SubItems.Add(c.NotNull ? "N" : "Y");
                it.SubItems.Add(c.IsPk ? "PK" : "");
                it.SubItems.Add(c.Comment);
                lstColumns.Items.Add(it);
            }
            lstColumns.EndUpdate();
        }

        private void OnColumnsKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.C) { CopySelectedColumns(false); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.A)
            {
                lstColumns.BeginUpdate();
                for (int i = 0; i < lstColumns.Items.Count; i++) lstColumns.Items[i].Selected = true;
                lstColumns.EndUpdate();
                e.Handled = true;
            }
        }

        private void CopySelectedColumns(bool all)
        {
            StringBuilder sb = new StringBuilder();
            int count = all ? lstColumns.Items.Count : lstColumns.SelectedItems.Count;
            for (int i = 0; i < count; i++)
            {
                ListViewItem it = all ? lstColumns.Items[i] : lstColumns.SelectedItems[i];
                for (int s = 0; s < it.SubItems.Count; s++)
                {
                    if (s > 0) sb.Append('\t');
                    sb.Append(it.SubItems[s].Text);
                }
                sb.Append("\r\n");
            }
            if (sb.Length == 0) return;
            try { Clipboard.SetText(sb.ToString()); lblStatus.Text = string.Format(S.CopiedFmt, count); }
            catch { lblStatus.Text = S.ClipboardFail; }
        }

        private void ShowAbout()
        {
            MessageBox.Show(S.AboutBody, S.AboutTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            // 隐藏诊断： --dump <in.sql> <out.txt>
            if (args != null && args.Length == 3 && args[0] == "--dump")
            {
                Dump(args[1], args[2]);
                return;
            }

            // --lang zh|en 覆盖语言；未指定时读保存的偏好，没有则按系统界面语言判断
            List<string> paths = new List<string>();
            bool langForced = false;
            if (args != null)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "--lang" && i + 1 < args.Length)
                    {
                        UiLang forced;
                        if (S.TryParseLang(args[i + 1], out forced)) { S.Lang = forced; langForced = true; }
                        i++;
                        continue;
                    }
                    paths.Add(args[i]);
                }
            }
            if (!langForced) S.LoadPreference();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(paths.ToArray()));
        }

        private static void Dump(string inPath, string outPath)
        {
            List<DbTable> ts = SqlParser.Parse(SqlParser.ReadAllTextAuto(inPath));
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < ts.Count; i++)
            {
                DbTable t = ts[i];
                sb.Append("TABLE ").Append(t.Name);
                if (t.Source.Length > 0) sb.Append("  [").Append(t.Source).Append(']');
                sb.Append("  cols=").Append(t.Columns.Count).Append("  pk=");
                List<string> pk = new List<string>();
                for (int c = 0; c < t.Columns.Count; c++) if (t.Columns[c].IsPk) pk.Add(t.Columns[c].Name);
                sb.Append(string.Join("+", pk.ToArray())).Append('\n');
                if (t.Description.Length > 0) sb.Append("  desc: ").Append(t.Description).Append('\n');
                for (int c = 0; c < t.Columns.Count; c++)
                {
                    DbColumn col = t.Columns[c];
                    sb.Append("    ").Append((c + 1).ToString().PadLeft(3)).Append("  ")
                      .Append(col.Name.PadRight(26)).Append(col.Type.PadRight(16))
                      .Append(col.NotNull ? "NOT NULL " : "         ")
                      .Append(col.IsPk ? "PK " : "   ")
                      .Append(col.Comment).Append('\n');
                }
            }
            File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(true));
        }
    }
}
