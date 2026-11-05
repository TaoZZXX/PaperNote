using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PaperNote
{
    // All PDFium calls are serialized. PDFium's public API is not thread-safe.
    internal static class PdfiumEngine
    {
        const int MaxRenderDimension = 8192;
        const int MaxRegionDimension = 18000;
        const long MaxRenderPixels = 24000000L;
        const long MaxRegionPixels = 150000000L;
        internal static readonly Color PaperBackgroundColor = Color.FromArgb(255, 248, 238);
        static readonly object Gate = new object();
        static bool initialized;
        static IntPtr document;
        static string documentPath;

        public static int GetPageCount(string path)
        {
            lock (Gate)
            {
                IntPtr doc = GetDocument(path);
                int count = Native.FPDF_GetPageCount(doc);
                if (count < 1)
                    throw new InvalidDataException("PDFium 未能读取 PDF 页数。");
                return count;
            }
        }

        public static List<SizeF> GetPageSizes(string path)
        {
            lock (Gate)
            {
                IntPtr doc = GetDocument(path);
                int count = Native.FPDF_GetPageCount(doc);
                if (count < 1)
                    throw new InvalidDataException("PDFium 未能读取 PDF 页数。");
                var sizes = new List<SizeF>(count);
                for (int i = 0; i < count; i++)
                {
                    IntPtr page = Native.FPDF_LoadPage(doc, i);
                    if (page != IntPtr.Zero)
                    {
                        try
                        {
                            double width = Native.FPDF_GetPageWidth(page), height = Native.FPDF_GetPageHeight(page);
                            if (width > 0 && height > 0 && width < Single.MaxValue && height < Single.MaxValue)
                            {
                                sizes.Add(new SizeF((float)width, (float)height));
                                continue;
                            }
                        }
                        finally
                        {
                            Native.FPDF_ClosePage(page);
                        }
                    }

                    double fallbackWidth, fallbackHeight;
                    if (Native.FPDF_GetPageSizeByIndex(doc, i, out fallbackWidth, out fallbackHeight) == 0 ||
                        fallbackWidth <= 0 || fallbackHeight <= 0)
                        throw PdfError("PDFium 无法读取第 " + (i + 1) + " 页的尺寸");
                    sizes.Add(new SizeF((float)fallbackWidth, (float)fallbackHeight));
                }
                return sizes;
            }
        }

        public static RenderedPage RenderPage(string path, int pageNumber, int dpi)
        {
            return RenderPage(path, pageNumber, dpi, PaperBackgroundColor);
        }

        public static RenderedPage RenderPage(string path, int pageNumber, int dpi, Color backgroundColor)
        {
            lock (Gate)
            {
                IntPtr doc = GetDocument(path);
                int count = Native.FPDF_GetPageCount(doc);
                if (count < 1)
                    throw new InvalidDataException("PDFium 未能读取 PDF 页数。");
                int pageIndex = Math.Max(0, Math.Min(count - 1, pageNumber - 1));
                IntPtr page = Native.FPDF_LoadPage(doc, pageIndex);
                if (page == IntPtr.Zero)
                    throw PdfError("PDFium 无法打开第 " + (pageIndex + 1) + " 页");
                try
                {
                    double pageWidth = Native.FPDF_GetPageWidth(page);
                    double pageHeight = Native.FPDF_GetPageHeight(page);
                    int width = Math.Max(1, checked((int)Math.Round(pageWidth * dpi / 72.0)));
                    int height = Math.Max(1, checked((int)Math.Round(pageHeight * dpi / 72.0)));
                    if (width > MaxRenderDimension || height > MaxRenderDimension ||
                        (long)width * height > MaxRenderPixels)
                        throw new InvalidDataException("页面渲染尺寸超过单页资源上限（" + width + "×" + height +
                                                       "），将降低 DPI 重试。");
                    Bitmap rendered = RenderToBitmap(page, width, height, 0, 0, width, height, backgroundColor);
                    return new RenderedPage { PageCount = count, Bitmap = rendered };
                }
                finally
                {
                    Native.FPDF_ClosePage(page);
                }
            }
        }

        // Renders only the given window of the page, at the same scale the whole page would
        // have at this dpi. The page is laid out at full size and shifted so the requested
        // rectangle lands inside a small bitmap, which keeps memory per tile tiny while
        // every pixel is produced by PDFium at the requested resolution.
        public static RenderedPage RenderPageRegion(string path, int pageNumber, int dpi, int left, int top, int width,
                                                    int height)
        {
            if (width < 1 || height < 1 || width > MaxRegionDimension || height > MaxRegionDimension ||
                (long)width * height > MaxRegionPixels)
                throw new InvalidDataException("页面局部渲染尺寸超出上限（" + width + "×" + height + "）。");
            lock (Gate)
            {
                IntPtr doc = GetDocument(path);
                int count = Native.FPDF_GetPageCount(doc);
                if (count < 1)
                    throw new InvalidDataException("PDFium 未能读取 PDF 页数。");
                int pageIndex = Math.Max(0, Math.Min(count - 1, pageNumber - 1));
                IntPtr page = Native.FPDF_LoadPage(doc, pageIndex);
                if (page == IntPtr.Zero)
                    throw PdfError("PDFium 无法打开第 " + (pageIndex + 1) + " 页");
                try
                {
                    double pageWidth = Native.FPDF_GetPageWidth(page);
                    double pageHeight = Native.FPDF_GetPageHeight(page);
                    int deviceWidth = Math.Max(1, checked((int)Math.Round(pageWidth * dpi / 72.0)));
                    int deviceHeight = Math.Max(1, checked((int)Math.Round(pageHeight * dpi / 72.0)));
                    Bitmap rendered = RenderToBitmap(page, deviceWidth, deviceHeight, left, top, width, height,
                                                     PaperBackgroundColor);
                    return new RenderedPage { PageCount = count, Bitmap = rendered };
                }
                finally
                {
                    Native.FPDF_ClosePage(page);
                }
            }
        }

        static Bitmap RenderToBitmap(IntPtr page, int deviceWidth, int deviceHeight, int left, int top, int width,
                                     int height, Color backgroundColor)
        {
            IntPtr bitmap = IntPtr.Zero;
            Bitmap output = null;
            BitmapData outputData = null;
            try
            {
                output = new Bitmap(width, height, PixelFormat.Format24bppRgb);
                outputData = output.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly,
                                             PixelFormat.Format24bppRgb);
                if (outputData.Stride <= 0 || outputData.Stride < width * 3)
                    throw new InvalidDataException("无法为 PDFium 创建页面像素缓冲区。");
                bitmap = Native.FPDFBitmap_CreateEx(width, height, 2 /* FPDFBitmap_BGR */, outputData.Scan0,
                                                    outputData.Stride);
                if (bitmap == IntPtr.Zero)
                    throw new OutOfMemoryException("PDFium 无法分配页面位图（" + width + "×" + height + "）。");
                Native.FPDFBitmap_FillRect(bitmap, 0, 0, width, height,
                                           unchecked((uint)backgroundColor.ToArgb()));
                Native.FPDF_RenderPageBitmap(bitmap, page, -left, -top, deviceWidth, deviceHeight, 0,
                                             1 /* FPDF_ANNOT */);
                Native.FPDFBitmap_Destroy(bitmap);
                bitmap = IntPtr.Zero;
                output.UnlockBits(outputData);
                outputData = null;
                Bitmap rendered = output;
                output = null;
                return rendered;
            }
            finally
            {
                if (bitmap != IntPtr.Zero)
                    Native.FPDFBitmap_Destroy(bitmap);
                if (outputData != null && output != null)
                    output.UnlockBits(outputData);
                if (output != null)
                    output.Dispose();
            }
        }

        public static Bitmap ExtractImageObject(string path, int pageNumber, RectangleF normalizedRegion,
                                                int deviceWidth, int deviceHeight)
        {
            lock (Gate)
            {
                IntPtr doc = GetDocument(path);
                int count = Native.FPDF_GetPageCount(doc);
                if (pageNumber < 1 || pageNumber > count)
                    throw new ArgumentOutOfRangeException("pageNumber");
                IntPtr page = Native.FPDF_LoadPage(doc, pageNumber - 1);
                if (page == IntPtr.Zero)
                    throw PdfError("PDFium 无法读取第 " + pageNumber + " 页的图片对象");
                IntPtr pdfBitmap = IntPtr.Zero;
                try
                {
                    if (deviceWidth < 1 || deviceHeight < 1)
                        return null;
                    int left = Math.Max(0, Math.Min(deviceWidth, (int)Math.Floor(normalizedRegion.Left * deviceWidth)));
                    int right =
                        Math.Max(0, Math.Min(deviceWidth, (int)Math.Ceiling(normalizedRegion.Right * deviceWidth)));
                    int top = Math.Max(0, Math.Min(deviceHeight, (int)Math.Floor(normalizedRegion.Top * deviceHeight)));
                    int bottom =
                        Math.Max(0, Math.Min(deviceHeight, (int)Math.Ceiling(normalizedRegion.Bottom * deviceHeight)));
                    if (right <= left || bottom <= top)
                        return null;

                    double x1, y1, x2, y2, x3, y3, x4, y4;
                    if (Native.FPDF_DeviceToPage(page, 0, 0, deviceWidth, deviceHeight, 0, left, top, out x1, out y1) ==
                            0 ||
                        Native.FPDF_DeviceToPage(page, 0, 0, deviceWidth, deviceHeight, 0, right, top, out x2,
                                                 out y2) == 0 ||
                        Native.FPDF_DeviceToPage(page, 0, 0, deviceWidth, deviceHeight, 0, left, bottom, out x3,
                                                 out y3) == 0 ||
                        Native.FPDF_DeviceToPage(page, 0, 0, deviceWidth, deviceHeight, 0, right, bottom, out x4,
                                                 out y4) == 0)
                        return null;
                    double regionLeft = Math.Min(Math.Min(x1, x2), Math.Min(x3, x4));
                    double regionRight = Math.Max(Math.Max(x1, x2), Math.Max(x3, x4));
                    double regionBottom = Math.Min(Math.Min(y1, y2), Math.Min(y3, y4));
                    double regionTop = Math.Max(Math.Max(y1, y2), Math.Max(y3, y4));

                    int objectCount = Native.FPDFPage_CountObjects(page);
                    IntPtr bestObject = IntPtr.Zero;
                    double bestCoverage = 0, bestIntersection = 0;
                    for (int i = 0; i < objectCount; i++)
                    {
                        IntPtr pageObject = Native.FPDFPage_GetObject(page, i);
                        if (pageObject == IntPtr.Zero ||
                            Native.FPDFPageObj_GetType(pageObject) != 3 /* FPDF_PAGEOBJ_IMAGE */)
                            continue;
                        float objectLeft, objectBottom, objectRight, objectTop;
                        if (Native.FPDFPageObj_GetBounds(pageObject, out objectLeft, out objectBottom, out objectRight,
                                                         out objectTop) == 0)
                            continue;
                        double w = Math.Max(0, Math.Min(regionRight, objectRight) - Math.Max(regionLeft, objectLeft));
                        double h = Math.Max(0, Math.Min(regionTop, objectTop) - Math.Max(regionBottom, objectBottom));
                        double intersection = w * h;
                        double objectArea = Math.Max(0.001, (objectRight - objectLeft) * (objectTop - objectBottom));
                        double coverage = intersection / objectArea;
                        if (intersection > 0 &&
                            (coverage > bestCoverage ||
                             (Math.Abs(coverage - bestCoverage) < 0.000001 && intersection > bestIntersection)))
                        {
                            bestObject = pageObject;
                            bestCoverage = coverage;
                            bestIntersection = intersection;
                        }
                    }
                    if (bestObject == IntPtr.Zero)
                        return null;

                    try
                    {
                        pdfBitmap = Native.FPDFImageObj_GetRenderedBitmap(doc, page, bestObject);
                    }
                    catch (EntryPointNotFoundException)
                    {
                        pdfBitmap = Native.FPDFImageObj_GetBitmap(bestObject);
                    }
                    if (pdfBitmap == IntPtr.Zero)
                        return null;
                    return CopyPdfBitmap(pdfBitmap);
                }
                finally
                {
                    if (pdfBitmap != IntPtr.Zero)
                        Native.FPDFBitmap_Destroy(pdfBitmap);
                    Native.FPDF_ClosePage(page);
                }
            }
        }

        public static List<RectangleF> GetGraphicObjectRegions(string path, int pageNumber,
                                                               RectangleF normalizedSelection, int deviceWidth,
                                                               int deviceHeight)
        {
            var regions = new List<RectangleF>();
            lock (Gate)
            {
                IntPtr doc = GetDocument(path);
                int count = Native.FPDF_GetPageCount(doc);
                if (pageNumber < 1 || pageNumber > count)
                    throw new ArgumentOutOfRangeException("pageNumber");
                IntPtr page = Native.FPDF_LoadPage(doc, pageNumber - 1);
                if (page == IntPtr.Zero)
                    throw PdfError("PDFium 无法读取第 " + pageNumber + " 页的图形对象");
                try
                {
                    if (deviceWidth < 1 || deviceHeight < 1)
                        return regions;
                    double selectionArea = Math.Max(0.000001, normalizedSelection.Width * normalizedSelection.Height);
                    int objectCount = Native.FPDFPage_CountObjects(page);
                    var candidates = new List<RectangleF>();
                    for (int i = 0; i < objectCount; i++)
                    {
                        IntPtr pageObject = Native.FPDFPage_GetObject(page, i);
                        if (pageObject == IntPtr.Zero)
                            continue;
                        int objectType = Native.FPDFPageObj_GetType(pageObject);
                        if (objectType < 2 || objectType > 5)
                            continue; // path, image, shading, or form
                        float left, bottom, right, top;
                        if (Native.FPDFPageObj_GetBounds(pageObject, out left, out bottom, out right, out top) == 0)
                            continue;
                        int x1, y1, x2, y2, x3, y3, x4, y4;
                        if (Native.FPDF_PageToDevice(page, 0, 0, deviceWidth, deviceHeight, 0, left, bottom, out x1,
                                                     out y1) == 0 ||
                            Native.FPDF_PageToDevice(page, 0, 0, deviceWidth, deviceHeight, 0, right, bottom, out x2,
                                                     out y2) == 0 ||
                            Native.FPDF_PageToDevice(page, 0, 0, deviceWidth, deviceHeight, 0, left, top, out x3,
                                                     out y3) == 0 ||
                            Native.FPDF_PageToDevice(page, 0, 0, deviceWidth, deviceHeight, 0, right, top, out x4,
                                                     out y4) == 0)
                            continue;
                        float regionLeft = (float)Math.Max(
                            0, Math.Min(1, Math.Min(Math.Min(x1, x2), Math.Min(x3, x4)) / (double)deviceWidth));
                        float regionRight = (float)Math.Max(
                            0, Math.Min(1, Math.Max(Math.Max(x1, x2), Math.Max(x3, x4)) / (double)deviceWidth));
                        float regionTop = (float)Math.Max(
                            0, Math.Min(1, Math.Min(Math.Min(y1, y2), Math.Min(y3, y4)) / (double)deviceHeight));
                        float regionBottom = (float)Math.Max(
                            0, Math.Min(1, Math.Max(Math.Max(y1, y2), Math.Max(y3, y4)) / (double)deviceHeight));
                        double objectArea = Math.Max(0, (regionRight - regionLeft) * (regionBottom - regionTop));
                        if (objectArea <= 0 || objectArea > selectionArea * 2.0)
                            continue;
                        double intersectionWidth = Math.Max(0, Math.Min(normalizedSelection.Right, regionRight) -
                                                                   Math.Max(normalizedSelection.Left, regionLeft));
                        double intersectionHeight = Math.Max(0, Math.Min(normalizedSelection.Bottom, regionBottom) -
                                                                    Math.Max(normalizedSelection.Top, regionTop));
                        double intersectionArea = intersectionWidth * intersectionHeight;
                        if (intersectionArea / Math.Min(objectArea, selectionArea) < 0.12)
                            continue;
                        candidates.Add(RectangleF.FromLTRB(regionLeft, regionTop, regionRight, regionBottom));
                    }
                    const float groupGap = 0.008f;
                    foreach (RectangleF candidate in candidates)
                    {
                        RectangleF merged = candidate;
                        bool changed = true;
                        while (changed)
                        {
                            changed = false;
                            for (int i = regions.Count - 1; i >= 0; i--)
                            {
                                RectangleF existing = regions[i];
                                bool near = merged.Left <= existing.Right + groupGap &&
                                            merged.Right >= existing.Left - groupGap &&
                                            merged.Top <= existing.Bottom + groupGap &&
                                            merged.Bottom >= existing.Top - groupGap;
                                if (near)
                                {
                                    merged = RectangleF.Union(merged, existing);
                                    regions.RemoveAt(i);
                                    changed = true;
                                }
                            }
                        }
                        regions.Add(merged);
                    }
                    return regions;
                }
                finally
                {
                    Native.FPDF_ClosePage(page);
                }
            }
        }

        static Bitmap CopyPdfBitmap(IntPtr pdfBitmap)
        {
            int width = Native.FPDFBitmap_GetWidth(pdfBitmap), height = Native.FPDFBitmap_GetHeight(pdfBitmap);
            int stride = Native.FPDFBitmap_GetStride(pdfBitmap), format = Native.FPDFBitmap_GetFormat(pdfBitmap);
            int bytesPerPixel = format == 1 ? 1 : (format == 2 ? 3 : ((format == 3 || format == 4) ? 4 : 0));
            if (width < 1 || height < 1 || width > 30000 || height > 30000 || bytesPerPixel == 0 ||
                stride < width * bytesPerPixel || (long)width * height > 120000000)
                return null;
            IntPtr source = Native.FPDFBitmap_GetBuffer(pdfBitmap);
            if (source == IntPtr.Zero)
                return null;
            Bitmap output = new Bitmap(width, height, PixelFormat.Format32bppRgb);
            BitmapData data = null;
            try
            {
                data = output.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly,
                                       PixelFormat.Format32bppRgb);
                byte[] inputRow = new byte[stride];
                byte[] outputRow = new byte[width * 4];
                for (int y = 0; y < height; y++)
                {
                    Marshal.Copy(IntPtr.Add(source, y * stride), inputRow, 0, stride);
                    for (int x = 0; x < width; x++)
                    {
                        int si = x * bytesPerPixel, di = x * 4;
                        if (format == 1)
                        {
                            outputRow[di] = outputRow[di + 1] = outputRow[di + 2] = inputRow[si];
                        }
                        else
                        {
                            byte alpha = format == 4 ? inputRow[si + 3] : (byte)255;
                            outputRow[di] = (byte)((inputRow[si] * alpha + 255 * (255 - alpha)) / 255);
                            outputRow[di + 1] = (byte)((inputRow[si + 1] * alpha + 255 * (255 - alpha)) / 255);
                            outputRow[di + 2] = (byte)((inputRow[si + 2] * alpha + 255 * (255 - alpha)) / 255);
                        }
                        outputRow[di + 3] = 255;
                    }
                    Marshal.Copy(outputRow, 0, IntPtr.Add(data.Scan0, y * data.Stride), outputRow.Length);
                }
                output.UnlockBits(data);
                data = null;
                return output;
            }
            catch
            {
                if (data != null)
                    output.UnlockBits(data);
                output.Dispose();
                throw;
            }
        }

        public static List<MainForm.WordBox> ExtractWords(string path, int pageNumber)
        {
            lock (Gate)
            {
                IntPtr doc = GetDocument(path);
                int count = Native.FPDF_GetPageCount(doc);
                if (pageNumber < 1 || pageNumber > count)
                    throw new ArgumentOutOfRangeException("pageNumber");
                IntPtr page = Native.FPDF_LoadPage(doc, pageNumber - 1);
                if (page == IntPtr.Zero)
                    throw PdfError("PDFium 无法读取第 " + pageNumber + " 页的文字");
                IntPtr textPage = IntPtr.Zero;
                try
                {
                    double pageWidth = Math.Max(1.0, Native.FPDF_GetPageWidth(page));
                    double pageHeight = Math.Max(1.0, Native.FPDF_GetPageHeight(page));
                    // Use the same page-to-device transform as PDFium's renderer. Character boxes
                    // are in PDF user space and can be offset by a non-zero CropBox origin; simply
                    // dividing by page width/height shifts selections on those pages.
                    const int coordinateDpi = 450;
                    int deviceWidth = Math.Max(1, checked((int)Math.Round(pageWidth * coordinateDpi / 72.0)));
                    int deviceHeight = Math.Max(1, checked((int)Math.Round(pageHeight * coordinateDpi / 72.0)));
                    textPage = Native.FPDFText_LoadPage(page);
                    if (textPage == IntPtr.Zero)
                        return new List<MainForm.WordBox>();
                    int chars = Math.Max(0, Native.FPDFText_CountChars(textPage));
                    var result = new List<MainForm.WordBox>();
                    Dictionary<int, int> structureOrder = GetPageStructureOrder(page);
                    bool structureApiAvailable = structureOrder.Count > 0;
                    IntPtr lastTextObject = IntPtr.Zero;
                    int lastObjectMcid = -1;
                    var word = new StringBuilder();
                    double left = 0, right = 0, bottom = 0, top = 0, previousRight = 0;
                    double previousCenterY = 0, previousHeight = 0;
                    int wordTaggedOrder = -1, wordTaggedCharIndex = -1;
                    bool hasBox = false;

                    Action flush = delegate
                    {
                        if (word.Length > 0 && hasBox)
                        {
                            float x1 = (float)Math.Max(0, Math.Min(1, left / deviceWidth));
                            float x2 = (float)Math.Max(0, Math.Min(1, right / deviceWidth));
                            float y1 = (float)Math.Max(0, Math.Min(1, top / deviceHeight));
                            float y2 = (float)Math.Max(0, Math.Min(1, bottom / deviceHeight));
                            if (x2 > x1 && y2 > y1)
                                result.Add(new MainForm.WordBox { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                                                                  Text = word.ToString(), TaggedOrder = wordTaggedOrder,
                                                                  TaggedCharIndex = wordTaggedCharIndex });
                        }
                        word.Length = 0;
                        hasBox = false;
                        wordTaggedOrder = -1;
                        wordTaggedCharIndex = -1;
                    };

                    for (int i = 0; i < chars; i++)
                    {
                        int unicode = unchecked((int)Native.FPDFText_GetUnicode(textPage, i));
                        if (unicode <= 0 || !IsValidScalar(unicode))
                        {
                            flush();
                            continue;
                        }
                        string value = Char.ConvertFromUtf32(unicode);
                        if (Char.IsWhiteSpace(value, 0))
                        {
                            flush();
                            continue;
                        }

                        double charLeft, charRight, charBottom, charTop;
                        if (Native.FPDFText_GetCharBox(textPage, i, out charLeft, out charRight, out charBottom,
                                                       out charTop) == 0)
                        {
                            flush();
                            continue;
                        }
                        int deviceX1, deviceY1, deviceX2, deviceY2;
                        if (Native.FPDF_PageToDevice(page, 0, 0, deviceWidth, deviceHeight, 0, charLeft, charBottom,
                                                     out deviceX1, out deviceY1) == 0 ||
                            Native.FPDF_PageToDevice(page, 0, 0, deviceWidth, deviceHeight, 0, charRight, charTop,
                                                     out deviceX2, out deviceY2) == 0)
                        {
                            flush();
                            continue;
                        }
                        double boxLeft = Math.Min(deviceX1, deviceX2);
                        double boxRight = Math.Max(deviceX1, deviceX2);
                        double boxTop = Math.Min(deviceY1, deviceY2);
                        double boxBottom = Math.Max(deviceY1, deviceY2);
                        if (boxRight <= 0 || boxLeft >= deviceWidth || boxBottom <= 0 || boxTop >= deviceHeight)
                        {
                            flush();
                            continue;
                        }
                        int charTaggedOrder = -1;
                        if (structureApiAvailable)
                        {
                            try
                            {
                                IntPtr textObject = Native.FPDFText_GetTextObject(textPage, i);
                                if (textObject != lastTextObject)
                                {
                                    lastTextObject = textObject;
                                    lastObjectMcid = textObject == IntPtr.Zero
                                                         ? -1
                                                         : Native.FPDFPageObj_GetMarkedContentID(textObject);
                                }
                                int mappedOrder;
                                if (lastObjectMcid >= 0 && structureOrder.TryGetValue(lastObjectMcid, out mappedOrder))
                                    charTaggedOrder = mappedOrder;
                            }
                            catch (EntryPointNotFoundException)
                            {
                                structureApiAvailable = false;
                            }
                        }
                        double charHeight = Math.Max(0.1, boxBottom - boxTop);
                        double centerY = (boxTop + boxBottom) * 0.5;
                        bool newWord =
                            hasBox &&
                            (Math.Abs(centerY - previousCenterY) > Math.Max(previousHeight, charHeight) * 0.7 ||
                             boxLeft - previousRight > Math.Max(previousHeight, charHeight) * 0.65);
                        if (newWord)
                            flush();
                        if (!hasBox)
                        {
                            left = boxLeft;
                            right = boxRight;
                            top = boxTop;
                            bottom = boxBottom;
                            hasBox = true;
                            wordTaggedOrder = charTaggedOrder;
                            wordTaggedCharIndex = charTaggedOrder >= 0 ? i : -1;
                        }
                        else
                        {
                            left = Math.Min(left, boxLeft);
                            right = Math.Max(right, boxRight);
                            top = Math.Min(top, boxTop);
                            bottom = Math.Max(bottom, boxBottom);
                            if (wordTaggedOrder < 0 && charTaggedOrder >= 0)
                            {
                                wordTaggedOrder = charTaggedOrder;
                                wordTaggedCharIndex = i;
                            }
                        }
                        word.Append(value);
                        previousRight = boxRight;
                        previousCenterY = centerY;
                        previousHeight = charHeight;
                    }
                    flush();
                    if (!structureApiAvailable)
                        foreach (MainForm.WordBox extracted in result)
                        {
                            extracted.TaggedOrder = -1;
                            extracted.TaggedCharIndex = -1;
                        }
                    return result;
                }
                finally
                {
                    if (textPage != IntPtr.Zero)
                        Native.FPDFText_ClosePage(textPage);
                    Native.FPDF_ClosePage(page);
                }
            }
        }

        static Dictionary<int, int> GetPageStructureOrder(IntPtr page)
        {
            var order = new Dictionary<int, int>();
            IntPtr tree = IntPtr.Zero;
            try
            {
                tree = Native.FPDF_StructTree_GetForPage(page);
                if (tree == IntPtr.Zero)
                    return order;
                int roots = Native.FPDF_StructTree_CountChildren(tree), next = 0;
                for (int i = 0; i < roots; i++)
                    AppendStructureElementOrder(Native.FPDF_StructTree_GetChildAtIndex(tree, i), order, ref next, 0);
            }
            catch (EntryPointNotFoundException)
            {
                order.Clear();
            }
            finally
            {
                if (tree != IntPtr.Zero)
                {
                    try
                    {
                        Native.FPDF_StructTree_Close(tree);
                    }
                    catch (EntryPointNotFoundException)
                    {
                        order.Clear();
                    }
                }
            }
            return order;
        }
        static void AppendStructureElementOrder(IntPtr element, Dictionary<int, int> order, ref int next, int depth)
        {
            if (element == IntPtr.Zero || depth > 96)
                return;
            int children = Native.FPDF_StructElement_CountChildren(element);
            if (children < 0)
                return;
            for (int i = 0; i < children; i++)
            {
                // Child order is significant: PDFium exposes MCID children and nested
                // structure elements in the logical sequence authored in the PDF.
                int mcid = Native.FPDF_StructElement_GetChildMarkedContentID(element, i);
                if (mcid >= 0 && !order.ContainsKey(mcid))
                    order.Add(mcid, next++);
                IntPtr child = Native.FPDF_StructElement_GetChildAtIndex(element, i);
                if (child != IntPtr.Zero)
                    AppendStructureElementOrder(child, order, ref next, depth + 1);
            }
        }

        static bool IsValidScalar(int value)
        {
            return value <= 0x10FFFF && (value < 0xD800 || value > 0xDFFF);
        }

        static IntPtr GetDocument(string path)
        {
            if (!initialized)
            {
                Native.FPDF_InitLibrary();
                initialized = true;
            }
            string fullPath = Path.GetFullPath(path);
            if (document != IntPtr.Zero && String.Equals(documentPath, fullPath, StringComparison.OrdinalIgnoreCase))
                return document;
            if (document != IntPtr.Zero)
            {
                Native.FPDF_CloseDocument(document);
                document = IntPtr.Zero;
            }
            byte[] utf8Path = Encoding.UTF8.GetBytes(fullPath + "\0");
            GCHandle pinned = GCHandle.Alloc(utf8Path, GCHandleType.Pinned);
            try
            {
                document = Native.FPDF_LoadDocument(pinned.AddrOfPinnedObject(), IntPtr.Zero);
            }
            finally
            {
                pinned.Free();
            }
            if (document == IntPtr.Zero)
                throw PdfError("PDFium 无法打开文件：" + fullPath);
            documentPath = fullPath;
            return document;
        }

        static Exception PdfError(string message)
        {
            uint code = Native.FPDF_GetLastError();
            string detail;
            switch (code)
            {
            case 1:
                detail = "未知错误";
                break;
            case 2:
                detail = "文件不存在或无法读取";
                break;
            case 3:
                detail = "文件不是有效 PDF";
                break;
            case 4:
                detail = "PDF 受密码保护";
                break;
            case 5:
                detail = "PDF 文件损坏或格式不受支持";
                break;
            default:
                detail = "错误码 " + code;
                break;
            }
            return new InvalidDataException(message + "（" + detail + "）");
        }

        public sealed class RenderedPage
        {
            public int PageCount;
            public Bitmap Bitmap;
        }

        static class Native
        {
            const string Library = "pdfium";
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
            internal static extern void FPDF_InitLibrary();
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
            internal static extern IntPtr FPDF_LoadDocument(IntPtr filePath, IntPtr password);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
            internal static extern void FPDF_CloseDocument(IntPtr document);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
            internal static extern uint FPDF_GetLastError();
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
            internal static extern int FPDF_GetPageCount(IntPtr document);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
            internal static extern int FPDF_GetPageSizeByIndex(IntPtr document, int pageIndex, out double width,
                                                               out double height);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
            internal static extern IntPtr FPDF_LoadPage(IntPtr document, int pageIndex);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
            internal static extern void FPDF_ClosePage(IntPtr page);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
            internal static extern double FPDF_GetPageWidth(IntPtr page);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
            internal static extern double FPDF_GetPageHeight(IntPtr page);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
            internal static extern int FPDF_PageToDevice(IntPtr page, int startX, int startY, int sizeX, int sizeY,
                                                         int rotate, double pageX, double pageY, out int deviceX,
                                                         out int deviceY);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
            internal static extern int FPDF_DeviceToPage(IntPtr page, int startX, int startY, int sizeX, int sizeY,
                                                         int rotate, int deviceX, int deviceY, out double pageX,
                                                         out double pageY);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
            internal static extern int FPDFPage_CountObjects(IntPtr page);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
            internal static extern IntPtr FPDFPage_GetObject(IntPtr page, int index);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
            internal static extern int FPDFPageObj_GetType(IntPtr pageObject);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
            internal static extern int FPDFPageObj_GetBounds(IntPtr pageObject, out float left, out float bottom,
                                                             out float right, out float top);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
            internal static extern IntPtr FPDFImageObj_GetRenderedBitmap(IntPtr document, IntPtr page,
                                                                         IntPtr pageObject);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
            internal static extern IntPtr FPDFImageObj_GetBitmap(IntPtr pageObject);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
            internal static extern int FPDFBitmap_GetWidth(IntPtr bitmap);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
            internal static extern int FPDFBitmap_GetHeight(IntPtr bitmap);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
            internal static extern int FPDFBitmap_GetStride(IntPtr bitmap);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
            internal static extern int FPDFBitmap_GetFormat(IntPtr bitmap);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
            internal static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
            internal static extern IntPtr FPDFBitmap_Create(int width, int height, int alpha);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
            internal static extern IntPtr FPDFBitmap_CreateEx(int width, int height, int format, IntPtr firstScan,
                                                              int stride);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
            internal static extern void FPDFBitmap_Destroy(IntPtr bitmap);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
            internal static extern void FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height,
                                                            uint color);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
            internal static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int startX, int startY,
                                                              int sizeX, int sizeY, int rotate, int flags);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
            internal static extern IntPtr FPDFText_LoadPage(IntPtr page);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
            internal static extern void FPDFText_ClosePage(IntPtr textPage);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
            internal static extern int FPDFText_CountChars(IntPtr textPage);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
            internal static extern uint FPDFText_GetUnicode(IntPtr textPage, int index);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
            internal static extern int FPDFText_GetCharBox(IntPtr textPage, int index, out double left,
                                                           out double right, out double bottom, out double top);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
            internal static extern IntPtr FPDFText_GetTextObject(IntPtr textPage, int index);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
            internal static extern int FPDFPageObj_GetMarkedContentID(IntPtr pageObject);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
            internal static extern IntPtr FPDF_StructTree_GetForPage(IntPtr page);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
            internal static extern void FPDF_StructTree_Close(IntPtr tree);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
            internal static extern int FPDF_StructTree_CountChildren(IntPtr tree);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
            internal static extern IntPtr FPDF_StructTree_GetChildAtIndex(IntPtr tree, int index);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
            internal static extern int FPDF_StructElement_CountChildren(IntPtr element);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
            internal static extern IntPtr FPDF_StructElement_GetChildAtIndex(IntPtr element, int index);
            [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
            internal static extern int FPDF_StructElement_GetChildMarkedContentID(IntPtr element, int index);
        }
    }
}
