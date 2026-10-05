# 更新日志 / Changelog

本项目遵循 [语义化版本](https://semver.org/lang/zh-CN/)。
格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)。

## [1.0.0] - 2026-10-06

首个发布版本。

### 新增

- **表结构浏览**：打开 SQL DDL（`CREATE TABLE`）文件，双栏展示表清单与字段明细
  - 左侧表清单：表名、字段数、主键、来源
  - 右侧字段网格：序号、字段名、类型、可空（Y/N）、PK、注释
- **五种打开方式**：粘贴路径 / 拖放 / 打开文件（多选）/ 打开文件夹 / 命令行参数
- **路径框**：工具栏内直接粘贴文件或文件夹路径，回车打开；支持一次粘贴多个（`;` 或换行分隔）
- **现代文件夹选择器**：改用 `IFileOpenDialog` + `FOS_PICKFOLDERS`，带地址栏可在其中粘贴路径，支持多选文件夹；COM 不可用时回退旧式对话框
- **表名过滤**：即时筛选，不区分大小写（`Ctrl+Shift+F` 定位）
- **字段搜索/过滤**：按字段名做不区分大小写的**部分匹配**，一次遍历**全部已载入的表**（171 张表 5644 个字段）
  - 表清单收窄到命中表，`字段` 列显示 `命中 / 总数`
  - 字段网格只显示当前表中命中的字段，表头显示 `匹配 X / N`
  - 全部表都无命中时提示「没有任何表包含该字段」
  - **分隔符不敏感**：空格、下划线、连字符、点号一律忽略，`acct id` / `acct_id` / `ACCT-ID` / `acctid` 四种写法等价
  - 表名过滤同样忽略分隔符（`bc acct` → `BC_ACCT`、`BC_ACCT_BILL_CYCLE`、`BC_ACCT_PROP`）
  - `Ctrl+F` 定位到该框；输入 `ID`、`acct id`、`DATE` 等片段即可，不需要完整字段名
- **复制为 TSV**：字段网格 `Ctrl+A` / `Ctrl+C`，可直接粘贴进 Excel 或 Markdown 表格
- **`--dump` 诊断模式**：把解析结果输出为文本，便于脚本比对与 CI 校验
- 快捷键：`Ctrl+O`、`Ctrl+Shift+D`、`Ctrl+Shift+O`

### 中英双语界面

- 界面提供中文与 English 两种语言，所有文案集中在 `S` 字符串表
- 首次运行跟随 Windows 显示语言（中文系统→中文，其他→英文）
- 菜单「语言 → 中文 / English」运行时切换，窗口即时改名，无需重启
- 选择保存在 `%APPDATA%\SqlStructViewer\lang.txt`（一行 `zh`/`en`），删除即恢复自动判断
- `--lang zh|en` 可对单次运行强制语言，不覆盖已保存的偏好
- 语言菜单两项始终用各自的文字显示（`中文` / `English`），进错语言也能找回
- 文件与文件夹对话框的标题、过滤器、按钮提示全部随语言切换

### 解析能力

- 同一文件内多张表；`IF NOT EXISTS` / `TEMPORARY`
- 引号标识符 `"x"` / `` `x` `` / `[x]`，`schema.table` 前缀
- 复合主键（表级 `CONSTRAINT ... PRIMARY KEY (...)` 与列内联）
- 字段上方 `--` 注释归属该字段，多行自动拼接；忽略 `-- ====` 装饰行
- `COMMENT ON COLUMN tbl.col IS '...'`
- 列修饰：`DEFAULT` / `CHECK` / `REFERENCES` / `COLLATE` / `GENERATED` / `AUTO_INCREMENT` / `IDENTITY`
- 表级约束行（`UNIQUE` / `FOREIGN` / `CHECK` / `KEY` / `INDEX`）自动跳过
- 编码：UTF-8（含/不含 BOM），失败回退 GBK
- Oracle 类型原样保留：`NUMBER(p,s)` / `VARCHAR2(n)` / `DATE` / `TIMESTAMP` / `CLOB`

### 构建

- 单文件 C# 源码，`csc` 编译，产物 29 KB，无需 .NET SDK
- 依赖 Windows 自带的 .NET Framework 4.x

### 文档

- 中英双语：`README.md`（英文，仓库默认）/ `README.zh-CN.md`（中文），`docs/USAGE.md` / `docs/USAGE.en.md`
- 每篇顶部与底部均有语言切换链接
- 截图两套：`docs/screenshot.png`（英文界面）/ `docs/screenshot.zh-CN.png`（中文界面）

### 验证

- 在 Ooredoo Maldives BSS DWH 数据字典上验证：110 张表 / 4040 个字段
- 解析结果与源 Excel 规格逐表比对，字段数与主键集合 110/110 完全一致
- GUI 侧实测：拖入文件夹后表清单 110 行、字段网格 6 列；粘贴路径 + 回车可正确载入；`Ctrl+Shift+D` 调起的对话框子窗口含 `DUIViewWndClassName`（确认是现代 Common Item Dialog）
- 双语实测：`--lang en` → 标题 `SQL Structure Viewer v1.0.0`、列头 `Table | Cols | PK | Source`；`--lang zh` → `SQL 表结构查看器 v1.0.0`、`表名 | 字段 | 主键 | 来源`
- 运行时切换实测：菜单「语言」→ 选中 English 后标题即时由中文变为英文，`lang.txt` 写入 `en`；重启无参数启动时按该文件恢复英文
- 仓库文档校验：相对链接 18 条 0 断链，锚点 16 条 0 失效

### 已知限制

- 只读，不修改也不生成 SQL
- 不连数据库，不做类型/外键的语义校验
- 大文件一次性读入内存，未做流式优化
- 不支持 `CREATE TABLE ... AS SELECT`、`CREATE VIEW`、`CREATE INDEX`
- 界面只提供中英两种语言
