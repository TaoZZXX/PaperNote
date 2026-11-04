# 第三方组件与许可

PaperNote 发布包包含以下第三方运行组件。每个组件仍按其各自许可条款提供；PaperNote 自身许可见发布包根目录的 `LICENSE`。

## PDFium

- NuGet 包：`bblanchon.PDFium.Win32`，版本 `158.0.8111.0`。
- NuGet 包清单声明的许可：Apache License 2.0。
- 包版权信息：Copyright © Benoît Blanchon 2017–2025。
- PDFium 上游许可包含 BSD 风格条款；随包附带 `LICENSES/PDFium-BSD.txt` 和 `LICENSES/Apache-2.0.txt`。
- PDFium 还包含其他开源组件。对应构建的完整清单应以 PDFium `chromium/8111` 源码中的 `LICENSES.chromium.html` 为准：
  <https://pdfium.googlesource.com/pdfium/+/refs/branch-heads/8111/LICENSES.chromium.html>
- 上游来源：<https://github.com/bblanchon/pdfium-binaries>

## Microsoft Visual C++ Runtime

发布包运行目录中附带了未修改的 Microsoft Visual C++ Runtime 文件。这些文件来自构建所用的 Visual Studio 2022 Redistributable 目录。Microsoft 说明此类文件的再分发受 Visual Studio 软件许可条款约束，且仅限持有相应许可的用户再分发。请参阅：

- <https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files>
- <https://learn.microsoft.com/en-us/visualstudio/releases/2022/redistribution>

PDFium 和 Microsoft 运行库由各自权利人按原样提供，不属于 PaperNote 自身许可授权范围。
