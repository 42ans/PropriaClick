# 构建说明

PropriaClick 只依赖 .NET Framework 自带的 C# 编译器，不需要 Visual Studio、.NET SDK 或任何第三方组件。

## 环境要求

| 项目 | 要求 |
| --- | --- |
| 系统 | Windows 10/11 x64 |
| 运行时 | .NET Framework 4.x（系统自带） |
| 编译器 | `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`（随 .NET Framework 提供） |
| 脚本 | Windows PowerShell 5.1 或 PowerShell 7 |

## 构建

```powershell
.\build.ps1
```

生成仓库根目录的 `PropriaClick.exe`：单文件、无外部依赖，可复制到任意位置运行。
构建前请先退出正在运行的 PropriaClick，否则编译器无法覆盖仍被占用的 exe（`CS0016`）。

## build.ps1 做了什么

```text
csc /nologo /target:winexe /out:PropriaClick.exe /codepage:65001
    /win32icon:PropriaClick.ico /win32manifest:app.manifest
    /resource:About.md /resource:LICENSE
    /r:System.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll
    /r:WPF\UIAutomationClient.dll /r:WPF\UIAutomationTypes.dll /r:WPF\WindowsBase.dll
    Program.cs
```

| 参数 | 作用 |
| --- | --- |
| `/target:winexe` | 生成图形界面程序，运行时不弹出控制台窗口 |
| `/codepage:65001` | 以 UTF-8 读取源码，`Program.cs` 中的中文提示才能正确编译 |
| `/win32icon` | 把 `PropriaClick.ico` 作为 exe 的图标 |
| `/win32manifest` | 嵌入 `app.manifest`：声明 DPI 感知、受支持的 Windows 版本，并以当前用户权限运行（不请求管理员） |
| `/resource:About.md`、`/resource:LICENSE` | 把文档嵌入 exe，供托盘菜单「说明」「许可证」写出 |
| `/r:WPF\…` | 引用 .NET Framework 自带的 UI 自动化程序集，无需额外安装 |

`Program.cs` 是唯一的源码文件，不产生中间文件；编译结果不依赖任何同目录文件。

`About.md` 只用于内嵌（托盘「说明」写出 `PropriaClick-About.md`），`README.md` 只用于仓库首页，不参与编译——两者内容相近但面向的读者不同，增删内容时不必同步。

## 图标

`PropriaClick.ico` 已随仓库提供，正常构建无需重新生成。需要修改图标时：

1. 用新的 PNG 替换 `icon/artwork.png`（透明背景，四周留白会被自动裁掉）；
2. 运行 `.\icon\make-icon.ps1` 重新生成根目录的 `PropriaClick.ico`；
3. 重新执行 `.\build.ps1`。

该脚本先把原图裁剪到可见内容，把被线条围住的透明区域补成白色（纯线条图标在深色任务栏上几乎不可见），再对 16/20/24/32/48/64/128/256 每个尺寸分别按 4 倍超采样后做盒式平均，并为小尺寸补偿笔画对比度。所有条目都以 32 位 BMP（DIB）存放：Windows 资源管理器和 .NET 只接受位图格式的图标条目，PNG 压缩的条目无法读取。

## 验证

```powershell
.\PropriaClick.exe
Get-Content .\PropriaClick.log
```

构建成功后日志中只有一行 `started`，没有错误记录；若 exe 所在目录不可写，日志会改写到 `%TEMP%`。
托盘菜单的「说明」「许可证」会在 exe 同目录写出 `PropriaClick-About.md` 与 `PropriaClick-License.txt`。

## 常见问题

- **CS0016: 无法写入输出文件**：PropriaClick 仍在运行，退出后重试。
- **中文显示为乱码**：漏掉了 `/codepage:65001`，或源码未以 UTF-8 保存。
- **图标模糊**：`PropriaClick.ico` 缺少小尺寸条目时，系统会把 256 像素的图标强行缩小。请用 `icon/make-icon.ps1` 生成完整的尺寸序列。
