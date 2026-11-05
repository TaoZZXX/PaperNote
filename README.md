# PaperNote

PaperNote 是面向科研阅读的 Windows PDF 阅读与批注工具，采用本地优先的方式管理论文、阅读进度和批注。PDF 页面由 PDFium 按需渲染，阅读器通过有限的页面缓存和后台任务处理长文档。

## 主要功能

- 导入并管理多篇 PDF 论文，按最近打开时间排序。
- 连续滚动阅读、缩放 PDF，并跳转到指定页码。
- 选择文字并跨页复制；可复制纯文本或 LaTeX 文本。
- 在 PDF 页面上创建、编辑和拖动批注。
- 右键翻译选中的论文文字，或按 `Ctrl+Alt+T`；支持跨页选区、论文术语表，并保留公式、引用标记和链接。也可在 PDF 右键菜单中单独选择“豆包详细总结所选文字”。
- 保存论文总结和页面尺寸缓存。
- 使用 `Ctrl+B` 打开论文列表和论文总结面板。
- 顶部品牌标识与 Windows 应用图标使用同一套论文便签图案，底色与 PDF 阅读区一致。

## 运行

运行 `dist` 目录中的 `PaperNote-PDFium.exe`。请将与它同目录的 `PaperNoteRuntime-PDFium` 文件夹一并保留，其中包含 PDFium 和应用所需的运行库。

论文库和批注默认保存在：

```text
%LOCALAPPDATA%\PaperNote\library.json
```

### 豆包论文翻译

首次翻译时打开“豆包翻译设置”，填写火山方舟 API Key 和控制台提供的模型 ID/接入点 ID。设置也可从 PDF 右键菜单打开。API Key 使用 Windows 当前用户加密后保存在 `%LOCALAPPDATA%\PaperNote\doubao-settings.json`。只有主动触发翻译时，当前选中的文字会发送到火山方舟；不会自动上传整篇 PDF。译文缓存在 `%LOCALAPPDATA%\PaperNote\translation-cache.json`，相同文本和术语表可直接复用。

## 构建

需要 Windows、Visual Studio 2022（含 MSBuild）和 .NET Framework 4.8 目标包。首次还原 NuGet 依赖时需要网络连接。在项目根目录运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

程序和运行库会生成到 `dist`。第三方组件及许可见 `THIRD-PARTY-NOTICES.md` 和 `LICENSES/`；当前 PDFium NuGet 包声明为 Apache-2.0，PDFium 上游还包含 BSD 风格许可条款。上游信息见 [pdfium-binaries](https://github.com/bblanchon/pdfium-binaries) 与 [PDFium](https://pdfium.googlesource.com/pdfium/)。

## 发布

PaperNote 自身使用 MIT 许可，完整文本见项目根目录的 `LICENSE`。运行以下命令生成发布包：

```powershell
powershell -ExecutionPolicy Bypass -File .\release.ps1
```

脚本会构建程序，并生成 `release\PaperNote.zip`，其中包含程序、PDFium/VC++运行库、README 和许可证文件，不包含本机论文库数据。

## 目录结构

```text
PaperNote/
├── src/                    C# 源码、项目文件、图标和 NuGet 锁定文件
├── dist/                   构建生成的 exe 和运行库
├── .build/                 MSBuild 输出与 NuGet 缓存
├── build.ps1               Windows 构建脚本
├── .clang-format            C# 代码格式规则
└── README.md               项目说明
```
