<p align="center">
  <img src="docs/images/icon.png" width="128" alt="WAIO 图标">
</p>

<h1 align="center">WAIO</h1>

<p align="center"><b>Windows All In One</b>:一个常驻托盘的 Windows 小工具合集</p>

WAIO 把日常离不开的几个小工具装进同一个程序:剪贴板历史、翻译、截图(标注、贴图、识别文字、长截图、录屏)、启动台(应用 + 全盘文件搜索),再加上按键映射和一套能绑定任意组合键的全局快捷键。

每个功能都是独立模块,可以单独开关,互不依赖。

> 这是个人项目,主要在作者自己的电脑上日常使用。WAIO 以管理员权限常驻运行(原因见下文「权限」),安装包没有代码签名。

## 功能

### 剪贴板历史
- 记录文字、富文本(RTF / HTML)、图片、文件路径,存在本地 SQLite。
- Win+V 在文字光标处弹出列表(找不到光标时弹在屏幕中央),⏎ 粘贴回原窗口,⇧⏎ 粘贴纯文本,Ctrl+1–0 快速粘贴前 10 条。
- 搜索、固定条目(固定的不会被自动清理)、空格预览文字 / 图片 / 文件列表;按存储上限自动清理。
- 去重:同一段内容在不同程序里复制、终端"选中即复制"产生的多个版本,只保留最新一条。
- 输入法组字时按键全部交给输入法,不会误触。

### 翻译
- 选中文字按 Win+T 翻译;没有选中文字时打开输入框。截图里识别出的文字也能直接翻译。
- 多个翻译源同时请求:Google、必应、腾讯交互翻译等免费源,DeepL(也支持 DeepLX)、百度、有道、腾讯云、阿里、火山、彩云、小牛等需要自己填 Key 的源,以及大模型(OpenAI 兼容接口:DeepSeek、OpenAI、Gemini、智谱、Kimi、通义千问、硅基流动、本机 Ollama 等,逐字输出)。
- 自动识别语种,原文已经是目标语言时自动改译成第二语言;自动去掉 PDF 复制带来的断行;翻译历史。
- API Key 用 Windows DPAPI 按当前用户加密保存。

### 截图
- 按 Win+C 先冻结整个屏幕,再在静止的画面上框选,打开的菜单、悬浮提示也能截进去。
- 自动识别鼠标下的窗口;按住 Ctrl 可以识别按钮、输入框等控件。框选时有放大镜,可以取色。
- 标注:矩形、椭圆、箭头、直线、画笔、荧光笔、文字、马赛克、模糊、序号,可以撤销 / 重做,也能在历史里重新编辑。
- 贴图(Alt+F2 贴剪贴板里的图):把截图钉在屏幕上,可以缩放、旋转、调透明度;贴图上识别出的文字可以直接拖动选择、复制。
- 识别文字(OCR)和截图翻译:PP-OCRv5 模型,本地离线运行。
- 长截图:手动往下滚,自动拼接,右侧显示长条预览。
- 录屏(Alt+F3 区域录屏,Ctrl+Alt+F2 全屏录屏):MP4(H.264 + AAC,可录系统声音和麦克风)或 GIF;全屏录屏时声音分轨录制,结束时选择保留哪些音轨。
- 截图历史:按日期分组,可以按日期、来源程序、识别出的文字搜索。

### 启动台
- Ctrl+空格弹出搜索框,搜应用支持拼音和首字母(`wx` 找到微信),常用的排前面。传统程序和商店应用都能搜到。
- 全盘搜文件和文件夹,做法和 Everything 一样:直接读 NTFS 的主文件表建索引(C 盘几百万个文件约 10 秒),之后启动立即可用,通过 USN 日志实时更新。
- 搜索词里带 `\` 时按完整路径匹配:`documents\ report` 只找 documents 文件夹下的 report,`d:\downloads\` 列出整个下载文件夹;双引号括起来的部分算一个词。
- 前缀 `a ` 只搜应用、`f ` 只搜文件、`d ` 只搜文件夹;`g`、`bd`、`bi`、`gh` + 空格直接网页搜索(可以自定义)。
- 右侧预览文本和图片;可以把文件直接拖到别的程序里;Ctrl+⏎ 打开所在位置,Ctrl+C 复制路径。

### 按键映射
- Super Caps(默认开启):CapsLock 本身不再切换大小写;按住 CapsLock 时整个键盘换一层,默认 WASD 方向键、QE 翻页、XC 行首 / 行尾,还能叠加 Shift 等修饰键(Caps+Shift+D 向右选中)。
- 改键:一个键换成另一个键。
- 双功能键:单独轻点是一个键,按住和别的键一起按是另一个键。

### 全局快捷键与系统动作
- 任意组合键绑定到任意命令:打开程序、文件、文件夹或网址,以及上面各个功能的命令。在设置里点键帽直接录制,F13–F24 也能用。
- 自定义修饰键:可以把 F1–F24、CapsLock 等不用来打字的键当修饰键用(按住 F24 再按 J)。
- 每个快捷键可以设置生效范围(只在某些程序里 / 除某些程序外)。
- 内置约 120 条 Windows 系统快捷键清单:WAIO 的快捷键和系统冲突时会提示;用到某个系统快捷键时,WAIO 运行期间由它接管,不用了或退出 WAIO 后系统功能自动恢复,不改任何系统设置。

### 外观
- 设置窗口和各个面板使用磨砂玻璃效果,模糊、鲜艳度、色调、强调色都可以调;深浅色跟随系统。

### 计划中
- 启动台支持 Everything 搜索语法:`|` 或、`!` 排除、`*.pdf` 通配符、`ext:`、`size:`、`dm:`、`regex:` 等。
- 启动台搜 WAIO 自己的命令和系统操作(锁屏、睡眠、关机……)。
- 安装包代码签名。

## 安装

1. 到 [Releases](https://github.com/sanhua1/WAIO/releases) 下载最新的 `WAIO-Setup-<版本>.exe`。
2. 双击安装。安装包没有代码签名,Windows 可能提示"Windows 已保护你的电脑",点「更多信息」→「仍要运行」。安装需要管理员权限,固定装到 `C:\Program Files\WAIO`。
3. 安装时可以选「开机自动启动」(默认勾选):登录 Windows 后 WAIO 自动以管理员权限在托盘运行,不会每次弹 UAC。
4. WAIO 常驻在托盘(任务栏右下角)。左键点托盘图标打开设置,右键可以检查更新或退出。

默认快捷键:

| 快捷键 | 功能 |
|---|---|
| Win+V | 剪贴板历史 |
| Win+T | 翻译 |
| Win+C | 截图 |
| Alt+F2 | 把剪贴板里的图贴到屏幕上 |
| Alt+F3 | 区域录屏(再按一次结束) |
| Ctrl+Alt+F2 | 全屏录屏(再按一次结束) |
| Ctrl+空格 | 启动台 |

都可以在「设置 › 快捷键」里修改或删除。注意:Win+V、Win+C、Win+T 本来是 Windows 的快捷键,中文 Windows 上 Ctrl+空格是"切换中文输入法",被 WAIO 占用期间这些系统功能暂停;不想要时在「设置 › 系统快捷键」里一键恢复。

### 升级

WAIO 每天检查一次 GitHub 上的新版本,有新版本时托盘弹出通知。在「设置 › 通用 › 更新」里点「下载并安装」:WAIO 下载安装包、校验 SHA-256 后静默安装,装完自动重新启动。设置和数据都会保留。也可以在托盘菜单点「检查更新」,或者在设置里关掉自动检查。

### 卸载

在「设置 › 应用 › 已安装的应用」里卸载 WAIO,开机自启任务会一起删掉。设置和数据不会自动删除,需要的话手动删掉:

- `%APPDATA%\WAIO`:设置、快捷键
- `%LOCALAPPDATA%\WAIO`:剪贴板历史、截图历史、文件索引、日志、加密保存的 API Key
- 「图片\WAIO」「视频\WAIO」:保存的截图和录屏

## 系统要求

- Windows 10 2004(19041)或更高版本,Windows 11;x64
- 文件搜索只支持 NTFS 磁盘
- 从源码构建:[.NET 10 SDK](https://dotnet.microsoft.com/download);打包安装包还需要 [Inno Setup 6](https://jrsoftware.org/isinfo.php)

## 从源码构建

```powershell
pwsh scripts/check.ps1            # 下载 OCR 模型 + 构建 + 全部测试(含架构测试)
pwsh scripts/publish.ps1          # 发布 Release 版(自包含)到 artifacts/publish
pwsh scripts/build-installer.ps1  # 打包安装包:artifacts/installer/WAIO-Setup-<版本>.exe
```

调试时可以直接运行 `src/Waio.App/bin/Debug/.../Waio.exe`。常用参数:`--no-elevate` 不提权、`--no-hook` 不装键盘钩子(只跑托盘和界面)。从开发目录运行时不能开启开机自启(见「权限」)。

装好安装版以后,`pwsh scripts/deploy-local.ps1` 会把当前代码发布并同步到 `C:\Program Files\WAIO`,然后重启 WAIO。

版本号写在 `Directory.Build.props` 的 `<Version>`。推送 `v<版本>` 标签后,GitHub Actions 会构建安装包、计算 SHA-256 并创建 Release。自己 fork 发布的话,把 `src/Waio.Core/Update/ReleaseFeed.cs` 里的 `DefaultRepository` 改成自己的仓库。

## 权限

WAIO 以管理员权限常驻运行:

| 需要管理员的地方 | 原因 |
|---|---|
| 全局快捷键、按键映射 | 普通权限的键盘钩子收不到管理员窗口(任务管理器、管理员终端等)里的按键 |
| 文件搜索 | 直接读 NTFS 主文件表和 USN 日志需要管理员 |
| 开机自启 | 用"登录时以最高权限运行"的计划任务,免去每次开机的 UAC 弹窗 |

为了不让管理员权限扩散:

- 从 WAIO 打开的程序、文件、网址默认以普通权限启动(只有在快捷键里明确设置"以管理员身份运行"的除外)。
- 开机自启任务只允许指向 Program Files 下的程序(普通程序改不了这里的文件)。
- 自动更新的安装包下载到只有管理员能写的目录,校验 SHA-256 后才运行。

## 联网与隐私

WAIO 不上传任何数据。联网的只有:

- 翻译:把要翻译的文字发给你启用的翻译源。
- 网页搜索:用浏览器打开搜索引擎。
- 检查更新:访问 GitHub 的公开发布接口(不带任何个人信息,可以在设置里关掉)。

识别文字(OCR)完全在本地运行。

## 配置文件

所有设置都能在设置窗口里改,一般不用碰配置文件。想手动编辑或备份时:

| 文件 | 内容 |
|---|---|
| `%APPDATA%\WAIO\config\app.json` | 功能开关、外观、自定义修饰键、检查更新 |
| `%APPDATA%\WAIO\config\bindings.json` | 全部快捷键 |
| `%APPDATA%\WAIO\config\features\<功能>.json` | 各功能的设置(如 `clipboard.json`、`translate.json`) |

`bindings.json` 的格式(手动改完后在「设置 › 快捷键」里点「重新加载快捷键配置」;写错的条目会被跳过,原文件自动备份):

```json
{
  "schemaVersion": 1,
  "bindings": [
    {
      "id": "6f1c2a3e-0b7d-4c55-9a51-3d2f8e4b7c10",
      "trigger": { "hotkey": "ctrl+alt+shift+n" },
      "command": "system.openApp",
      "arguments": { "target": "notepad.exe" },
      "enabled": true
    },
    {
      "id": "0d9e4b21-5f3a-4e8c-b6a2-7c1d9f0e2a34",
      "trigger": { "hotkey": "ctrl+alt+g" },
      "command": "system.openUrl",
      "arguments": { "url": "https://github.com" },
      "scope": { "type": "process", "exclude": true, "names": ["code"] },
      "enabled": true
    }
  ]
}
```

- `id`:任意不重复的 GUID。
- `hotkey`:修饰键 `ctrl` / `alt` / `shift` / `win`(或自定义修饰键,如 `f24`)加一个主键,用 `+` 连接。
- `scope`:不写 = 全局;`"type": "process"` + `names`(进程名,带不带 `.exe` 都行)限定生效范围,`exclude: true` 表示"除这些程序外"。
- 各功能有哪些命令、参数,见各功能目录的 README(`src/Features/*/README.md`)。

命令行参数:

| 参数 | 作用 |
|---|---|
| `--autostart` | 静默启动到托盘,不打开设置窗口(开机自启用的就是它) |
| `--no-elevate` | 不请求管理员权限(调试用) |
| `--no-hook` | 不装键盘钩子,快捷键不可用,只跑托盘和界面(调试界面用) |
| `--register-autostart` / `--unregister-autostart` | 只创建 / 删除开机自启任务然后退出(安装包用) |

## 常见问题

**杀毒软件报毒 / 拦截?**
WAIO 会安装全局键盘钩子、发送模拟按键、以管理员权限运行,安装包又没有签名,这些都容易被误报。源码全部公开,也可以按下文自己构建。

**有些地方快捷键不起作用?**
UAC 弹窗、锁屏、Ctrl+Alt+Delete 界面属于系统的安全桌面,任何程序的键盘钩子都收不到;部分带反作弊或独占输入的游戏也可能收不到。如果 WAIO 没有以管理员权限运行,管理员窗口里的快捷键也不生效(设置 › 通用 会提示)。

**改键改乱了,键盘不好用了?**
从托盘退出 WAIO,所有映射立即失效。也可以提前给「暂停 / 恢复按键映射」(`keymap.toggle`)绑一个快捷键当安全阀。

**Ctrl+空格 切换不了中文输入法了?**
启动台默认用 Ctrl+空格。可以在「设置 › 快捷键」里给启动台换个键,或者在「设置 › 系统快捷键」里恢复系统功能(会停用占用它的 WAIO 快捷键)。

**文件搜索搜不到某些文件?**
只支持 NTFS 磁盘;U 盘、移动硬盘默认不索引(「设置 › 启动台 › 文件搜索」里可以打开);需要以管理员权限运行。索引异常时可以在设置里重建索引。

**Google 翻译用不了?**
国内访问 Google 需要代理;Google 也会按 IP 限流(提示"Google 限流了")。换一个代理节点,或者用其他翻译源。

**从源码运行的版本不能开机自启、不能自动更新?**
这是有意的:以管理员身份自启 / 运行一个普通用户能改的文件有提权风险,所以这两项只对装在 Program Files 下的安装版开放。

## 已知限制

- 免 Key 的翻译源(Google、必应、腾讯交互翻译)用的是网页版的非官方接口,对方改版或限流时可能失效。
- 文件搜索只支持 NTFS,不支持 ReFS、FAT32、exFAT 和网络磁盘。
- 文字光标定位依赖各程序对辅助功能接口(MSAA / UI Automation)的支持,定位不到时面板弹在屏幕中央。
- 按键映射只有一层,不能按程序区分;映射出来的键不会再触发 WAIO 自己的快捷键。

## 项目结构

```
src/
  Waio.Kit/             功能可见的契约(唯一的公共依赖,平台无关)
  Waio.Core/            输入引擎(键盘钩子)、命令总线、设置、启动器、自启、更新
  Waio.UI/              共享界面组件、磨砂玻璃
  Waio.Shell/           托盘、设置窗口、启动流程
  Waio.App/             程序入口、功能清单、图标
  Features/             各个功能,只依赖 Waio.Kit 和 Waio.UI
tests/                  单元测试和架构测试
installer/              安装包脚本(Inno Setup)
scripts/                构建、发布、打包脚本
```

功能之间不直接依赖,跨功能协作通过 `Waio.Kit/Contracts` 里的契约命令;只有 `Waio.Core` 能装键盘钩子、发送按键、创建进程。这些规则由 `tests/Waio.Architecture.Tests` 自动检查。代码注释里的 `ADR-xxxx` 是设计决策记录的编号。

## 参与开发

欢迎提 Issue 和 Pull Request。提交前请确认:

- `pwsh scripts/check.ps1` 全部通过(包括架构测试;架构测试失败说明违反了上面的依赖规则,请改代码而不是改测试)。
- 单个 `.cs` 文件不超过 400 行;新增 NuGet 依赖要在 `Directory.Packages.props` 里集中管理。
- 文档和注释用中文,标识符用英文。
- 会安装真实键盘钩子、发送按键的集成测试默认跳过,需要时设置环境变量 `WAIO_INTEGRATION=1` 手动运行(运行期间不要碰键盘)。

## 致谢

开发时参考了这些项目的思路:

- [MAIO](https://github.com/sanhua1/MAIO):同一作者的 macOS 版本,各功能的交互和规则主要来自它
- [Ditto](https://github.com/sabrogden/Ditto):剪贴板、文字光标定位
- [pot-desktop](https://github.com/pot-app/pot-desktop):翻译源接入、读取选中文字
- [kanata](https://github.com/jtroo/kanata):按键映射的分层、双功能键概念
- [oil-find](https://github.com/oil-oil/oil-find):文件名匹配质量和索引布局
- [PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR)、[RapidOCR](https://github.com/RapidAI/RapidOCR):识别文字的模型和前后处理
- 文件搜索的默认策略和交互参考了 [Everything](https://www.voidtools.com/)

GPL 项目只借鉴思路,没有复制代码;借鉴了具体做法的地方在代码注释里注明了出处。

用到的第三方组件和数据:

| 组件 | 用途 | 许可证 |
|---|---|---|
| [Windows App SDK](https://github.com/microsoft/WindowsAppSDK)(WinUI 3,随程序分发运行时) | 界面 | Microsoft 软件许可条款 |
| [Win2D](https://github.com/microsoft/Win2D) | 截图标注、贴图绘制 | Microsoft 软件许可条款 |
| [ONNX Runtime](https://github.com/microsoft/onnxruntime) | 运行 OCR 模型 | MIT |
| [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore) | 剪贴板历史 | MIT |
| PP-OCRv5 mobile 模型(RapidOCR 转换的 ONNX 版本,构建时下载,不在仓库里) | 识别文字 | Apache-2.0 |
| [Unicode Unihan](https://www.unicode.org/charts/unihan.html) 读音数据 | 启动台拼音表 `pinyin.txt` | Unicode License v3 |
| [Inno Setup](https://jrsoftware.org/isinfo.php) | 安装包 | Inno Setup License |
| [Inno Setup 简体中文翻译](https://github.com/kira-96/Inno-Setup-Chinese-Simplified-Translation) | `installer/Languages/ChineseSimplified.isl` | MIT |

## 许可证

[GPL-3.0](LICENSE),附加一条[例外条款](LICENSE-EXCEPTION.md):允许和 Windows App SDK(WinUI 3)、Win2D 等随程序分发的微软组件一起链接和分发。这些组件本身不是 GPL 兼容的开源许可证,例外条款只针对它们,WAIO 自己的代码仍然完全按 GPL-3.0。
