using System.Text;
using iTextSharp.text.pdf;
using iTextSharp.text.pdf.parser;

namespace DocAI_API.Services;

public class TextExtractionService
{
    public async Task<string> ExtractTextFromFile(IFormFile file)
    {
        // Use the fully qualified System.IO.Path to avoid ambiguity
        var fileExtension = System.IO.Path.GetExtension(file.FileName).ToLower();
        
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
            return "Please upload a PDF or TXT file.";
        }
    }

    private async Task<string> ExtractTextFromPdf(IFormFile file)
    {
        var sb = new StringBuilder();

        using var memoryStream = new MemoryStream();
        await file.CopyToAsync(memoryStream);
        memoryStream.Position = 0;

        try
        {
            using var reader = new PdfReader(memoryStream);
            
            for (int page = 1; page <= reader.NumberOfPages; page++)
            {
                var strategy = new SimpleTextExtractionStrategy();
                var currentText = PdfTextExtractor.GetTextFromPage(reader, page, strategy);
                sb.Append(currentText);
                sb.Append("\n");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ PDF Extraction Error: {ex.Message}");
            return $"PDF Error: {ex.Message}";
        }

        var result = sb.ToString();
        Console.WriteLine($"📄 Extracted text length: {result.Length}");
        
        if (string.IsNullOrWhiteSpace(result))
        {
            return "No text could be extracted from this PDF. It may be a scanned image.";
        }

        return result;
    }

    private async Task<string> ExtractTextFromTxt(IFormFile file)
    {
        using var reader = new StreamReader(file.OpenReadStream());
        return await reader.ReadToEndAsync();
    }
}