# Usage Guide

[中文](USAGE.md) · **English**

For `SqlStructViewer.exe` v1.0.0. The README explains *what* it is; this document explains *how to use it*.

> The UI is **bilingual (English / Chinese)** — see [§9 Switching the language](#9-switching-the-language).
> UI labels quoted below are given as **中文** *(English translation)*; in English mode the very same
> control shows the English label.

---

## Contents

1. [Starting the app](#1-starting-the-app)
2. [Five ways to open files](#2-five-ways-to-open-files)
3. [Interface reference](#3-interface-reference)
4. [Filtering tables](#4-filtering-tables)
5. [Exporting columns to Excel / Markdown](#5-exporting-columns-to-excel--markdown)
6. [Command line and diagnostic mode](#6-command-line-and-diagnostic-mode)
7. [Parsing rules in detail](#7-parsing-rules-in-detail)
8. [Troubleshooting](#8-troubleshooting)
9. [Switching the language](#9-switching-the-language)

---

## 1. Starting the app

Double-click `SqlStructViewer.exe`. The window opens empty and the status bar reads:

> 就绪。可拖放 .sql 文件到窗口，或在「路径」框粘贴路径后回车。
> *(Ready. Drop .sql files onto the window, or paste a path into the "Path" box and press Enter.)*

No administrator rights, no installation, no registry writes, no network access.

---

## 2. Five ways to open files

### 2.1 Paste a path (fastest)

The toolbar has a **路径** *(Path)* input:

```
路径: [ D:\project\ddl                                    ] [打开路径]
```

1. Copy a file or folder path in Explorer (`Ctrl+C`, e.g. from the address bar)
2. Click into the **路径** box and paste with `Ctrl+V`
3. Press **Enter**

Rules:

| What you paste | What happens |
|---|---|
| A folder path | Collects `*.sql`, `*.ddl`, `*.txt` in that folder, sorted by file name |
| A single file path | Loads it directly (any extension) |
| Multiple paths | Separate with `;` or **newlines**; each is processed |
| A path containing spaces | Just paste it (it is tried as a whole first); `"D:\my folder\a.sql"` also works |

> To jump to the box: `Ctrl+Shift+O` (menu **文件 → 从路径打开…**). It focuses and selects all.

### 2.2 Drag & drop

Drag files **or folders** from Explorer onto anywhere in the window. A folder is scanned for
`*.sql / *.ddl / *.txt` (top level only — subdirectories are not recursed).

### 2.3 Open File

`Ctrl+O`, or the **[打开文件]** toolbar button. The file dialog supports multi-select — `Ctrl+A`
picks the whole directory.

### 2.4 Open Folder

`Ctrl+Shift+D`, or the **[打开文件夹]** toolbar button.

This opens the modern Windows folder picker (the Common Item Dialog from Vista onward):

- It has a **breadcrumb address bar** — click it and `Ctrl+V` a path, then press Enter to jump there
- **Multi-select** folders with `Ctrl` or `Shift`
- The first selected folder is written back into the toolbar **路径** box

> On very old systems where this dialog is unavailable, the app automatically falls back to the
> legacy tree-style folder browser.

### 2.5 Command line / file association

```cmd
SqlStructViewer.exe "D:\project\ddl"
SqlStructViewer.exe a.sql b.sql "D:\more-ddl"
```

Arguments may be files or folders, mixed, and multiple. This also means you can associate the `.sql`
file type with this program — double-clicking a `.sql` file opens it here.

---

## 3. Interface reference

### 3.1 Left pane — table list

| Column | Meaning | Source |
|---|---|---|
| 表名 *(Table)* | Table name | `CREATE TABLE <name>` |
| 字段 *(Cols)* | Number of columns | Count of parsed columns |
| 主键 *(PK)* | Primary-key columns, comma separated; composite keys are fully listed | `PRIMARY KEY (...)` |
| 来源 *(Source)* | `Source` from the DDL header comment | `-- Source : CRM` |

Single-row selection. Selecting a table refreshes the right pane immediately.

### 3.2 Right pane — column grid

The light header area shows `表名　[来源]　N 字段` with the table description (`-- Description`
from the DDL header) on the second line.

| Column | Meaning |
|---|---|
| `#` | Ordinal, in DDL order (never reordered) |
| 字段名 *(Column)* | Column name (displayed uppercase) |
| 类型 *(Type)* | Preserved verbatim: `NUMBER(22)`, `VARCHAR2(256)`, `DATE`, `TIMESTAMP`, `CLOB` |
| 空 *(Null)* | `Y` = nullable, `N` = `NOT NULL` |
| PK | `PK` when the column is part of the primary key |
| 注释 *(Comment)* | Text of the `--` comment lines above the column (joined into one line) |

### 3.3 Status bar

```
已载入 110 张表 / 4040 个字段   D:\project\ddl
```

If some files could not be read, `（N 个文件读取失败）` *(N files failed to load)* is appended.

---

## 4. Filtering tables

Type a substring into the **过滤** *(Filter)* box. The left table list filters **live**
(case-insensitive) and the toolbar shows `表 <matched> / <total>　字段 <total>`.

Clear the box to restore everything. Filtering only affects the left list; it does not change the
loaded data.

---

## 5. Exporting columns to Excel / Markdown

1. Select the target table on the left
2. Click into the column grid, press `Ctrl+A` then `Ctrl+C`

You get **TSV** (tab-separated) in the column order `# / Column / Type / Null / PK / Comment`.

- Paste into Excel / WPS: it splits into columns automatically
- Only some rows? Hold `Ctrl` while clicking, then `Ctrl+C`
- The right-click menu also offers **复制选中行 / 复制全部行** *(copy selected / copy all)*

---

## 6. Command line and diagnostic mode

```cmd
:: Normal launch
SqlStructViewer.exe <path> [<path> ...]

:: Force the UI language for this run (zh | en)
SqlStructViewer.exe --lang en <path> [<path> ...]

:: Diagnostic mode: no window, write the parse result to a text file
SqlStructViewer.exe --dump <input.sql> <output.txt>
```

`--dump` is useful for:

- Diagnosing "why isn't this table showing up?"
- Batch comparison — e.g. verifying that a set of DDL files parses correctly

Output format:

```
TABLE INF_SUBSCRIBER  [CRM]  cols=73  pk=SUBS_ID
  desc: Subscriber basic information.
      1  SUBS_ID                   NUMBER(22)      NOT NULL PK 
      2  SUBS_NAME                 VARCHAR2(256)               
      3  PROD_INST_ID              NUMBER(22)                  
```

Batch example:

```cmd
mkdir out
for %f in (ddl\*.sql) do SqlStructViewer.exe --dump "%f" "out\%~nf.txt"
```

---

## 7. Parsing rules in detail

### 7.1 How tables are split

The file is split on each `CREATE TABLE`, with the statement end found by balancing parentheses.
Multiple tables in one file are all recognised — the left list shows one row per table.

### 7.2 How comments are attributed

`--` comment lines directly above a column belong to **the next column**:

```sql
CREATE TABLE T (
  -- 用户 ID。
  -- [PK]
  ID     NUMBER(22) NOT NULL,
  -- 用户名称。
  NAME   VARCHAR2(256)
);
```

Marker lines such as `-- [PK]` are recognised as a primary-key flag and are not mixed into the
comment text. Decorative separator lines (`-- ====`, `-- ----`) are ignored.

### 7.3 Where the primary key comes from

Three sources, merged:

1. Table-level constraint: `CONSTRAINT PK_T PRIMARY KEY (A, B)`
2. Inline on the column: `A NUMBER(22) PRIMARY KEY`
3. Comment marker: `-- [PK]`

Composite keys are listed in full, e.g. `pk=GROUP_ID+BE_ID+HIS_DATE`.

> If a source specification document disagrees with the markers inside the DDL, **the DDL wins**.

### 7.4 How types are handled

Types are preserved verbatim and uppercased. No mapping, no validation. A type such as
`NUMBER(20,6)` — with a comma in the precision — is not mistaken for two columns.

### 7.5 Encoding

UTF-8 is decoded strictly first; on an invalid byte sequence the reader falls back to GBK (cp936);
if that also fails, the system default encoding is used. BOMs are stripped.

### 7.6 What is ignored

- Non-DDL statements: `INSERT` / `SELECT` / `COMMENT ON TABLE`, etc.
- Table-level `UNIQUE` / `FOREIGN KEY` / `CHECK` / `INDEX` / `KEY` constraint rows
- Standalone `CREATE VIEW` / `CREATE INDEX`
- Any line that cannot be resolved into "name + type" is skipped silently

---

## 8. Troubleshooting

| Symptom | Cause / fix |
|---|---|
| Left pane empty, status bar says 0 tables | No recognisable `CREATE TABLE` in the file. Run `--dump` to see the parse result; make sure it isn't a `CREATE TABLE ... AS SELECT` form |
| Status bar says "N files failed to load" | File locked, no permission, or unusual encoding. Close whatever holds the file and retry |
| "路径不存在或其中没有可读文件" *(path does not exist or contains no readable files)* | The pasted path is wrong, or the folder has no `.sql/.ddl/.txt` files |
| Pasting a path and pressing Enter does nothing | Make sure the caret is inside the **路径** box before pressing Enter, or click **[打开路径]** |
| SmartScreen blocks the first run | "More info" → "Run anyway". All unsigned self-built binaries do this |
| Some columns are missing | The DDL uses a dialect construct not covered here — see §7.6 |

---

## 9. Switching the language

The interface is available in **English and Chinese**; switching takes effect immediately, with no restart.

**First run.** The language follows your Windows display language: a Chinese Windows starts in Chinese,
anything else starts in English.

**Switching.** Use the menu **Language → 中文 / English**. The whole window retitles at once, and the
choice is remembered for next time.

**Where it is remembered.** One line in

```
%APPDATA%\SqlStructViewer\lang.txt      (contents: zh or en)
```

Delete that file to return to automatic detection.

**Per-launch override.** Pass `--lang`:

```cmd
SqlStructViewer.exe --lang en D:\ddl
SqlStructViewer.exe --lang zh D:\ddl
```

`--lang` overrides the saved preference **for that run only** — it does not overwrite the file.

**If you end up in the wrong language**, look for the top menu bar: the third item is the language
menu (`Language` / `语言`), and its two entries are always written in their own script —
`中文` and `English` — so you can always find your way back.

---

## Appendix — all shortcuts

| Shortcut | Action |
|---|---|
| `Ctrl+O` | Open file |
| `Ctrl+Shift+D` | Open folder (modern dialog) |
| `Ctrl+Shift+O` | Focus and select the "Path" box |
| `Enter` | Inside the "Path" box: open that path |
| `Ctrl+C` / `Ctrl+A` | In the column grid or table list: copy selected rows / select all |

---

[中文](USAGE.md) · **English**
