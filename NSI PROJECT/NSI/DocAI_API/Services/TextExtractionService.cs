using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using PDFtoImage;
using SkiaSharp;
using Tesseract;

namespace DocAI_API.Services;

public class TextExtractionService
{
    // Minimum characters we require from PdfPig before we trust it's a "real" text PDF.
    // Scanned PDFs sometimes contain a handful of stray characters (e.g. from a watermark)
    // even with no real text layer, so we don't just check for == 0.
    private const int MinTextLengthThreshold = 20;

    // Path to the folder containing tessdata language files (eng.traineddata, etc).
    // This folder must be copied to the output/bin directory at build time.
    private static readonly string TessDataPath =
        Path.Combine(AppContext.BaseDirectory, "tessdata");

    public async Task<string> ExtractTextFromFile(IFormFile file)
    {
        var fileExtension = Path.GetExtension(file.FileName).ToLower();

        if (fileExtension == ".pdf")
        {
            return await ExtractTextFromPdf(file);
        }
        else if (fileExtension == ".txt")
        {
            return await ExtractTextFromTxt(file);
        }
        else
        {
            return "Unsupported file type. Please upload PDF or TXT files.";
        }
    }

    // ─── PDF EXTRACTION ───
    private async Task<string> ExtractTextFromPdf(IFormFile file)
    {
        var sb = new StringBuilder();
        byte[] pdfBytes;

        using (var memoryStream = new MemoryStream())
        {
            await file.CopyToAsync(memoryStream);
            pdfBytes = memoryStream.ToArray();
        }

        // ─── METHOD 1: Try normal text-layer extraction with PdfPig ───
        try
        {
            using var ms = new MemoryStream(pdfBytes);
            using var pdf = UglyToad.PdfPig.PdfDocument.Open(ms);
            Console.WriteLine($"📄 Number of pages: {pdf.GetPages().Count()}");

            foreach (var page in pdf.GetPages())
            {
                var text = page.Text;
                Console.WriteLine($"📄 Page {page.Number} text length: {text.Length}");
                if (text.Length > 0)
                {
                    Console.WriteLine($"📄 Page {page.Number} preview: {text.Substring(0, Math.Min(100, text.Length))}");
                }
                sb.Append(text);
            }

            var result = sb.ToString();
            if (!string.IsNullOrWhiteSpace(result) && result.Trim().Length >= MinTextLengthThreshold)
            {
                Console.WriteLine($"✅ PdfPig extracted {result.Length} characters (text-based PDF)");
                return result;
            }

            Console.WriteLine($"⚠️ PdfPig text layer too short ({result.Trim().Length} chars) — likely a scanned/image PDF. Falling back to OCR.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ PdfPig failed: {ex.Message}");
        }

        // ─── METHOD 2: OCR fallback for scanned/image-based PDFs ───
        try
        {
            var ocrResult = await ExtractTextWithOcr(pdfBytes);
            if (!string.IsNullOrWhiteSpace(ocrResult))
            {
                Console.WriteLine($"✅ OCR extracted {ocrResult.Length} characters (image-based PDF)");
                return ocrResult;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ OCR fallback failed: {ex.Message}");
        }

        return "No text could be extracted from this PDF. Please upload a clearer scan or a text-based PDF.";
    }

    // ─── OCR FALLBACK: Render each PDF page to an image, then run Tesseract OCR ───
    private async Task<string> ExtractTextWithOcr(byte[] pdfBytes)
    {
        return await Task.Run(() =>
        {
            var sb = new StringBuilder();

            if (!Directory.Exists(TessDataPath))
            {
                Console.WriteLine($"❌ tessdata folder not found at: {TessDataPath}");
                Console.WriteLine("❌ Download eng.traineddata from https://github.com/tesseract-ocr/tessdata "
                    + "and place it in a 'tessdata' folder next to your built .exe/.dll, "
                    + "and ensure it's set to 'Copy to Output Directory' in your .csproj.");
                return string.Empty;
            }

            using var engine = new TesseractEngine(TessDataPath, "eng", EngineMode.Default);

            // PDFtoImage renders each page of the PDF to a bitmap at a given DPI.
            // Higher DPI = better OCR accuracy but slower processing.
            var pageCount = Conversion.GetPageCount(pdfBytes);
            Console.WriteLine($"🖼️ Rendering {pageCount} page(s) to images for OCR...");

            for (int i = 0; i < pageCount; i++)
            {
                using SKBitmap bitmap = Conversion.ToImage(pdfBytes, page: i, options: new RenderOptions(Dpi: 300));
                using SKData encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
                using var pix = Pix.LoadFromMemory(encoded.ToArray());
                using var ocrPage = engine.Process(pix);

                var pageText = ocrPage.GetText();
                var confidence = ocrPage.GetMeanConfidence();

                Console.WriteLine($"🖼️ Page {i + 1} OCR confidence: {confidence:P0}, extracted {pageText.Length} characters");
                sb.Append(pageText);
                sb.Append("\n");
            }

            return sb.ToString();
        });
    }

    // ─── TXT EXTRACTION ───
    private async Task<string> ExtractTextFromTxt(IFormFile file)
    {
        using var reader = new StreamReader(file.OpenReadStream());
        return await reader.ReadToEndAsync();
    }
}