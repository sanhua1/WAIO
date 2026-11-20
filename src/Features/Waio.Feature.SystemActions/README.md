# 系统动作(`system`)

用快捷键打开程序、文件、文件夹或网页。没有默认快捷键,在「设置 › 快捷键」里添加。

## 命令

| 命令 | 参数 | 说明 |
|---|---|---|
| `system.openApp` | `target`(必填)、`arguments`、`workingDirectory`、`runElevated` | 打开程序 / 文档 / 文件夹。程序目标支持参数;文档、文件夹不支持 |
| `system.openUrl` | `url`(必填) | 打开链接,只允许 http / https / mailto / ms-settings |

## 安全

WAIO 以管理员权限运行,但这里打开的东西默认以**普通权限**启动(通过 `ILauncher` 降权);只有 `runElevated` 明确写成 JSON 的 `true` 时才保持管理员权限。

## 所需能力

无。
