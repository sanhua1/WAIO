# 截图(`screenshot`)

按 **Win+C** 先冻结整个屏幕,再在静止的画面上框选,打开的菜单、悬浮提示也能截进去。自动识别鼠标下的窗口(按住 Ctrl 识别控件),可以取色。
截图自动复制,并写入截图历史。功能和交互参考 MAIO 的截图功能。

## 命令

| 命令 | 默认快捷键 | 说明 |
|---|---|---|
| `screenshot.capture` | Win+C | 区域截图 |
| `screenshot.fullScreen` | — | 截取鼠标所在的屏幕 |
| `screenshot.window` | — | 截取最前面的窗口(不带阴影,被挡住的部分也能截到) |
| `screenshot.ocr` | — | 框选后识别文字 |
| `screenshot.translate` | — | 框选后识别文字并翻译 |
| `screenshot.scrolling` | — | 长截图 |
| `screenshot.record` | Alt+F3 | 区域录屏;录制中再按一次 = 完成 |
| `screenshot.recordFullScreen` | Ctrl+Alt+F2 | 全屏录屏(鼠标所在屏幕),声音分轨录制;再按一次结束并选择保留哪些音轨 |
| `screenshot.stopRecording` | — | 结束正在进行的录屏 |
| `screenshot.pinClipboard` | Alt+F2 | 把剪贴板里的图片(文字会渲染成图片)贴到屏幕上 |
| `screenshot.pinLast` | — | 重新贴最近一张截图 |
| `screenshot.history` | — | 打开截图历史 |
| `screenshot.closeAllPins` | — | 关闭屏幕上所有贴图 |

框选时:Ctrl+C 复制、Ctrl+S 保存、Ctrl+P 贴图、Ctrl+O 识别文字、Ctrl+T 翻译、Ctrl+L 长截图、Ctrl+R 录屏。

## 主要部分

- 标注:矩形、椭圆、箭头、直线、画笔、荧光笔、文字、马赛克、模糊、序号(Win2D 绘制),撤销 / 重做,可在历史里重新编辑。
- 贴图:缩放、旋转、透明度;贴图后在后台识别文字,鼠标可以直接在文字上拖动选择。
- 识别文字:PP-OCRv5 mobile 模型(ONNX Runtime,CPU,离线)。模型由 `scripts/fetch-ocr-models.ps1` 在构建时下载并校验,不在仓库里;缺模型时退回 Windows 自带 OCR。
- 长截图:用户自己滚动,按灰度帧估计位移后拼接,最长 30000 像素。
- 录屏:MP4(Media Foundation H.264 + AAC,先硬件编码、失败退回软件编码,可录系统声音和麦克风)或 GIF。

## 设置与数据

设置页「截图」:放大镜、颜色格式、保存位置(默认「图片\WAIO」)、贴图、录屏(帧率、格式、指针、声音)、历史容量(默认 2 GB)等。
历史:`%LOCALAPPDATA%\WAIO\data\screenshot\`。录屏保存到「视频\WAIO」。

## 所需能力

`ScreenCapture`。
