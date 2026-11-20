# 剪贴板(`clipboard`)

记录复制过的文字、富文本(RTF / HTML)、图片、文件路径,按 **Win+V** 在文字光标处弹出面板:搜索、固定、预览、粘贴回原窗口。
交互和去重规则参考 MAIO 的剪贴板功能;文字光标定位的顺序(MSAA → GetGUIThreadInfo → UI Automation)参考 Ditto。

## 命令

| 命令 | 默认快捷键 | 说明 |
|---|---|---|
| `clipboard.show` | Win+V | 弹出面板;已打开时再按一次关闭 |
| `clipboard.clearHistory` | — | 清空历史,固定的保留 |

面板里:⏎ 粘贴回原窗口,⇧⏎ 粘贴纯文本,Ctrl+1–0 粘贴前 10 条,空格预览,右键菜单(粘贴 / 纯文本 / 仅复制 / 预览 / 固定 / 删除)。

## 设置

设置页「剪贴板」:存储上限(默认 2 GB)、弹出位置(文字光标处,找不到时屏幕中央)、搜索方式、粘贴后是否移到最前、记录哪些格式等。
文件:`%APPDATA%\WAIO\config\features\clipboard.json`。

## 数据

`%LOCALAPPDATA%\WAIO\data\clipboard\`:`history.sqlite` + 按 SHA-256 存放的图片等大块数据。单次复制超过 200 MB 不记录。

## 所需能力

无特殊能力(剪贴板监听由宿主提供,粘贴用 `IInputService` 发 Ctrl+V)。
