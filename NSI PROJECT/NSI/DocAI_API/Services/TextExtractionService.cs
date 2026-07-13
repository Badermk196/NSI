using System.Text;
using UglyToad.PdfPig;
using PDFtoImage;
using SkiaSharp;
using Tesseract;

namespace DocAI_API.Services;

public class TextExtractionService
{
    private const int MinTextLengthThreshold = 20;

    private static readonly string TessDataPath =
        Path.Combine(AppContext.BaseDirectory, "tessdata");

    public async Task<string> ExtractTextFromFile(IFormFile file)
    {
        var fileExtension = Path.GetExtension(file.FileName).ToLower();

        if (fileExtension == ".pdf")
            return await ExtractTextFromPdf(file);

        if (fileExtension == ".txt")
            return await ExtractTextFromTxt(file);

        if (fileExtension == ".jpg" || fileExtension == ".jpeg" || fileExtension == ".png")
            return await ExtractTextFromImage(file);

        return "Unsupported file type. Please upload PDF, TXT, JPG, JPEG, or PNG files.";
    }

    private async Task<string> ExtractTextFromPdf(IFormFile file)
    {
        var sb = new StringBuilder();
        byte[] pdfBytes;

        using (var memoryStream = new MemoryStream())
        {
            await file.CopyToAsync(memoryStream);
            pdfBytes = memoryStream.ToArray();
        }

        try
        {
            using var ms = new MemoryStream(pdfBytes);
            using var pdf = PdfDocument.Open(ms);

            Console.WriteLine($"📄 Number of pages: {pdf.GetPages().Count()}");

            foreach (var page in pdf.GetPages())
            {
                var text = page.Text;

                Console.WriteLine($"📄 Page {page.Number} text length: {text.Length}");

                if (text.Length > 0)
                    Console.WriteLine($"📄 Page {page.Number} preview: {text.Substring(0, Math.Min(100, text.Length))}");

                sb.Append(text);
                sb.Append("\n");
            }

            var result = sb.ToString();

            if (!string.IsNullOrWhiteSpace(result) && result.Trim().Length >= MinTextLengthThreshold)
            {
                Console.WriteLine($"✅ PdfPig extracted {result.Length} characters");
                return result;
            }

            Console.WriteLine("⚠️ PdfPig text too short. Falling back to OCR.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ PdfPig failed: {ex.Message}");
        }

        try
        {
            var ocrResult = await ExtractTextFromPdfWithOcr(pdfBytes);

            if (!string.IsNullOrWhiteSpace(ocrResult))
            {
                Console.WriteLine($"✅ OCR extracted {ocrResult.Length} characters from PDF");
                return ocrResult;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ PDF OCR fallback failed: {ex.Message}");
        }

        return "No text could be extracted from this PDF.";
    }

    private async Task<string> ExtractTextFromPdfWithOcr(byte[] pdfBytes)
    {
        return await Task.Run(() =>
        {
            var sb = new StringBuilder();

            if (!Directory.Exists(TessDataPath))
            {
                Console.WriteLine($"❌ tessdata folder not found at: {TessDataPath}");
                return string.Empty;
            }

            using var engine = new TesseractEngine(TessDataPath, "eng", EngineMode.Default);

            var pageCount = Conversion.GetPageCount(pdfBytes);
            Console.WriteLine($"🖼️ Rendering {pageCount} PDF page(s) to image for OCR...");

            for (int i = 0; i < pageCount; i++)
            {
                using SKBitmap bitmap = Conversion.ToImage(
                    pdfBytes,
                    page: i,
                    options: new RenderOptions(Dpi: 300)
                );

                using SKData encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
                using var pix = Pix.LoadFromMemory(encoded.ToArray());
                using var ocrPage = engine.Process(pix);

                var pageText = ocrPage.GetText();
                var confidence = ocrPage.GetMeanConfidence();

                Console.WriteLine($"🖼️ PDF page {i + 1} OCR confidence: {confidence:P0}, extracted {pageText.Length} characters");

                sb.Append(pageText);
                sb.Append("\n");
            }

            return sb.ToString();
        });
    }

    private async Task<string> ExtractTextFromImage(IFormFile file)
    {
        return await Task.Run(async () =>
        {
            if (!Directory.Exists(TessDataPath))
            {
                Console.WriteLine($"❌ tessdata folder not found at: {TessDataPath}");
                return string.Empty;
            }

            byte[] imageBytes;

            using (var memoryStream = new MemoryStream())
            {
                await file.CopyToAsync(memoryStream);
                imageBytes = memoryStream.ToArray();
            }

            using var engine = new TesseractEngine(TessDataPath, "eng", EngineMode.Default);
            using var pix = Pix.LoadFromMemory(imageBytes);
            using var page = engine.Process(pix);

            var text = page.GetText();
            var confidence = page.GetMeanConfidence();

            Console.WriteLine($"🖼️ Image OCR confidence: {confidence:P0}");
            Console.WriteLine($"🖼️ Image OCR extracted {text.Length} characters");

            if (!string.IsNullOrWhiteSpace(text))
                Console.WriteLine($"🖼️ Image OCR preview: {text.Substring(0, Math.Min(200, text.Length))}");

            return text;
        });
    }

    private async Task<string> ExtractTextFromTxt(IFormFile file)
    {
        using var reader = new StreamReader(file.OpenReadStream());
        return await reader.ReadToEndAsync();
    }
}