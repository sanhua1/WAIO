# 翻译(`translate`)

选中文字按 **Win+T** 翻译;没有选中文字时打开输入框。所有启用的翻译源并行请求,结果按设置里的顺序显示,大模型逐字输出。
功能逻辑参考 MAIO 的翻译功能;各翻译源的接入方式和读取选中文字的做法参考 pot-desktop(只借鉴思路)。

## 命令

| 命令 | 参数 | 说明 |
|---|---|---|
| `translate.show` | — | 默认 Win+T;有选中文字就翻译,否则打开输入框 |
| `translate.selection` | — | 只翻译选中的文字 |
| `translate.input` | — | 打开空的输入框 |
| `translate.text` | `text` | 契约命令(`TranslateContracts.TranslateText`):打开窗口并翻译传入的文字,截图的"识别并翻译"用它 |

窗口里:⏎ 翻译,⇧⏎ 换行,Esc 或 Ctrl+W 关闭;📌 固定后失去焦点不关闭;结果卡片可以复制、折叠、失败时重试。

## 翻译源

- 免 Key(非官方网页接口,对方改版可能失效):Google、必应、腾讯交互翻译。
- 自己填 Key:DeepL(也可填 DeepLX 地址)、百度、有道、腾讯云、阿里、火山、彩云、小牛。
- 大模型(OpenAI 兼容接口,可以添加多个):预设 DeepSeek、OpenAI、Gemini、智谱、Kimi、通义千问、硅基流动、Ollama(本机);提示词可以自定义。

读选中文字先用 UI Automation,读不到时模拟 Ctrl+C,读完恢复剪贴板(剪贴板历史不会记下这次复制)。

## 设置与数据

设置页「翻译」:目标语言和第二语言、翻译源的开关和顺序、窗口位置、字号、历史等。
API Key 用 DPAPI 按当前用户加密,存在 `%LOCALAPPDATA%\WAIO\data\translate\secrets.json`;翻译历史在同一目录的 `history.json`。

## 所需能力

`Network`。
