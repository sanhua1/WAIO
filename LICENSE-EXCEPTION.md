# GPL 例外条款:允许链接微软组件

WAIO 以 [GNU 通用公共许可证第 3 版(GPL-3.0)](LICENSE) 发布,并按 GPL-3.0 第 7 节附加以下许可。

WAIO 是 WinUI 3 程序,运行时需要并随安装包分发几个微软组件,它们使用的是 Microsoft 软件许可条款,不是 GPL 兼容的开源许可证。
这条例外允许 WAIO(以及基于它修改的版本)和这些组件一起链接、打包、分发,而不要求这些组件本身以 GPL 发布或提供源码。
例外只针对下面列出的组件;WAIO 自己的代码和其他依赖仍然完全按 GPL-3.0。

## Additional permission under GNU GPL version 3 section 7

Copyright (C) 2026 sanhua1

If you modify this Program, or any covered work, by linking or combining it with any of the following
(or a modified version of them), containing parts covered by the terms of the Microsoft Software License Terms
or other license terms under which Microsoft distributes them, the licensors of this Program grant you additional
permission to convey the resulting work:

- Microsoft Windows App SDK (including WinUI 3 and the Windows App Runtime, NuGet package `Microsoft.WindowsAppSDK`)
- Win2D (NuGet package `Microsoft.Graphics.Win2D`)
- The Microsoft Visual C++ runtime libraries and other Microsoft runtime components redistributed together with the above

Corresponding Source for a non-source form of such a combination is not required to include the source code
for the parts of the components listed above.

You may remove this additional permission from your copy, or from any part of it, as described in section 7 of the GPL.
