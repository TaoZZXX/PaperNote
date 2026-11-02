# PaperNote for Windows

PaperNote is a small Windows desktop app for keeping a local paper library and reading PDF papers with page-linked annotations.

## Run

Open `PaperNote-Material.exe`. The app maximizes to the available display. Choose a paper from the top selector or import a PDF. Scroll to move between pages; Ctrl+wheel zooms. Drag over a page to select a region, then add a quote or note in the floating card. Saved region highlights reappear on that page. The paper list and annotations are stored in `%LOCALAPPDATA%\PaperNote\library.json`.

To rebuild on Windows, run `powershell -ExecutionPolicy Bypass -File .\build.ps1`. It uses the .NET Framework C# compiler included with Windows and needs no NuGet packages. The embedded reader currently uses Poppler from the local Codex runtime cache to render PDF pages.

## Current scope

This build supports in-app PDF reading, page navigation, mouse-drag region highlights, and persistent page-numbered notes, formula excerpts, questions, and other annotation types. The selected text must be typed or pasted into the quote field; OCR and text selection from scanned pages are not included.
