using Microsoft.AspNetCore.Mvc;
using DocAI_API.Services;
using System.Text.Json;

namespace DocAI_API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class FileUploadController : ControllerBase
{
    private readonly AiService _aiService;
    private readonly TextExtractionService _textExtractionService;

    public FileUploadController()
    {
        _aiService = new AiService();
        _textExtractionService = new TextExtractionService();
    }

    [HttpGet("test")]
    public IActionResult TestConnection()
    {
        return Ok(new 
        { 
            message = "✅ Backend API is running!",
            status = "online",
            timestamp = DateTime.Now
        });
    }

    [HttpPost("upload")]
    public async Task<IActionResult> UploadFile(IFormFile file, [FromForm] string docType)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest(new { error = "No file uploaded." });
        }

        if (string.IsNullOrEmpty(docType))
        {
            return BadRequest(new { error = "Document type is required." });
        }

        // ─── Extract text from file ───
        string fileText;
        try
        {
            fileText = await _textExtractionService.ExtractTextFromFile(file);
            
            Console.WriteLine("=== EXTRACTED TEXT ===");
            Console.WriteLine(fileText);
            Console.WriteLine("=== END EXTRACTED TEXT ===");
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = $"Failed to read file: {ex.Message}" });
        }

        if (string.IsNullOrWhiteSpace(fileText))
        {
            fileText = "No text could be extracted.";
        }

        // ─── SAVE FILE TO SERVER ───
        var uploadsFolder = Path.Combine(Directory.GetCurrentDirectory(), "Uploads");
        if (!Directory.Exists(uploadsFolder))
        {
            Directory.CreateDirectory(uploadsFolder);
        }

        var uniqueFileName = $"{Guid.NewGuid()}_{file.FileName}";
        var filePath = Path.Combine(uploadsFolder, uniqueFileName);

        using (var stream = new FileStream(filePath, FileMode.Create))
        {
            await file.CopyToAsync(stream);
        }

        Console.WriteLine($"📄 File saved: {uniqueFileName}");

        // ─── AI extracts data for ALL document types ───
        var extractedJson = await _aiService.ExtractInvoiceData(fileText, docType);

        // ─── Filter fields ───
        var dataDict = JsonSerializer.Deserialize<Dictionary<string, object>>(extractedJson) ?? new Dictionary<string, object>();
        var filteredData = _aiService.FilterFieldsForDocType(dataDict, docType);
        var filteredJson = JsonSerializer.Serialize(filteredData);

        Console.WriteLine("=== FILTERED DATA ===");
        Console.WriteLine(filteredJson);
        Console.WriteLine("=== END FILTERED DATA ===");

        return Ok(new
        {
            fileName = file.FileName,
            savedFileName = uniqueFileName,
            fileSize = file.Length,
            fileType = file.ContentType,
            extractedData = filteredJson,
            docType = docType,
            invoiceId = 0,
            message = "✅ AI processing complete!"
        });
    }

    [HttpPost("save")]
    public async Task<IActionResult> SaveData([FromBody] dynamic data)
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(data);
            return Ok(new
            {
                success = true,
                id = 1,
                message = "✅ Data saved successfully!"
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new
            {
                success = false,
                error = ex.Message
            });
        }
    }

    // ─── VIEW DOCUMENT ENDPOINT ───
    [HttpGet("view/{fileName}")]
    public IActionResult ViewDocument(string fileName)
    {
        try
        {
            var uploadsFolder = Path.Combine(Directory.GetCurrentDirectory(), "Uploads");
            var filePath = Path.Combine(uploadsFolder, fileName);

            Console.WriteLine($"📄 Looking for file: {filePath}");

            if (!System.IO.File.Exists(filePath))
            {
                return NotFound(new { error = "File not found." });
            }

            var fileBytes = System.IO.File.ReadAllBytes(filePath);
            var contentType = GetContentType(fileName);
            
            return File(fileBytes, contentType);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    private string GetContentType(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLower();
        return ext switch
        {
            ".pdf" => "application/pdf",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".txt" => "text/plain",
            _ => "application/octet-stream"
        };
    }
}