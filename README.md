# PaperNote

PaperNote is a local-first Windows PDF reader and paper annotation app. It uses PDFium for page rendering and text geometry. Pages are loaded on demand and every zoom level gets the resolution it needs: each page is re-rendered as the zoom changes, and once a whole-page bitmap would grow past its memory budget only the visible window is re-rasterized at the current screen resolution, so detail keeps improving instead of the page being stretched. The page cache and bounded background workers keep long PDFs from being loaded into memory all at once.

Run `PaperNote-PDFium.exe` from this folder and keep the adjacent `PaperNoteRuntime-PDFium` directory with it. Import a PDF or select an existing paper, scroll continuously between pages, and use Ctrl+wheel to zoom. Drag over selectable text to highlight and annotate; continue the same drag onto another page to select across pages. The reader auto-scrolls when the pointer reaches the top or bottom edge. Ctrl+C copies selected text as plain text, including across pages; Ctrl+Shift+C copies the extracted selection as LaTeX text. Text selection never adds a rendered page image to the clipboard. Hold Ctrl and drag with the left mouse button to immediately copy a clean screenshot of the framed PDF region; it excludes the blue selection overlay. The context menu also offers direct extraction of an embedded PDF image object. The paper list and annotations are stored in `%LOCALAPPDATA%\PaperNote\library.json`.

## Build

On Windows, install Visual Studio 2022 MSBuild and use a network connection for the first NuGet restore, then run:

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

The project includes the .NET Framework 4.8 reference assemblies as a build dependency. The script restores the x64 PDFium binary package, builds the WinForms app, and copies `pdfium.dll` plus managed dependencies into `PaperNoteRuntime-PDFium`. PDFium APIs are serialized through one engine lock, as the upstream API is not thread-safe. PDFium is distributed under BSD-style licensing; the binary package and source notices are available from [pdfium-binaries](https://github.com/bblanchon/pdfium-binaries) and [PDFium](https://pdfium.googlesource.com/pdfium/).
