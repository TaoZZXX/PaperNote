# PaperNote

PaperNote 是 Windows PDF 论文阅读与批注工具。

## 运行

双击 `PaperNote.exe`。请将 `PaperNoteRuntime-PDFium` 文件夹与程序放在同一目录。运行环境需要 Windows 和 .NET Framework 4.8。

## 豆包论文翻译

在 PDF 中选中文字后右键选择“豆包翻译所选文字”，也可按 `Ctrl+Alt+T`。首次使用时填写火山方舟 API Key 和控制台中的模型 ID/接入点 ID。API Key 会使用 Windows 当前用户加密保存在 `%LOCALAPPDATA%\PaperNote`。仅主动选中的文字会发送给火山方舟，不会自动上传整篇 PDF。

在 PDF 上选中文字后右键点“豆包详细总结所选文字”，会直接生成当前选区的结构化科研总结。翻译窗口也保留“详细总结”页签；两项功能共用同一豆包模型配置。

## 许可

PaperNote 自身按随包的 `LICENSE` 提供。PDFium、Microsoft Visual C++ Runtime 等第三方组件按 `THIRD-PARTY-NOTICES.md` 和 `LICENSES/` 中的说明提供。
