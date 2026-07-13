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
    private readonly DatabaseService _databaseService;

    public FileUploadController(DatabaseService databaseService)
    {
        _aiService = new AiService();
        _textExtractionService = new TextExtractionService();
        _databaseService = databaseService;
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

    [HttpGet("document-types")]
    public async Task<IActionResult> GetDocumentTypes()
    {
        var documentTypes = await _databaseService.GetAllDocumentTypesAsync();

        return Ok(new
        {
            success = true,
            count = documentTypes.Count,
            data = documentTypes
        });
    }

    [HttpGet("fields/{docType}")]
    public async Task<IActionResult> GetFieldsByDocType(string docType)
    {
        var fields = await _databaseService.GetFieldsByDocTypeAsync(docType);

        return Ok(new
        {
            success = true,
            docType,
            count = fields.Count,
            fields
        });
    }

    [HttpPost("upload")]
    public async Task<IActionResult> UploadFile(IFormFile file, [FromForm] string docType)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "No file uploaded." });

        if (string.IsNullOrWhiteSpace(docType))
            return BadRequest(new { error = "Document type is required." });

        string fileText;

        try
        {
            fileText = await _textExtractionService.ExtractTextFromFile(file);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = $"Failed to read file: {ex.Message}" });
        }

        if (string.IsNullOrWhiteSpace(fileText))
            fileText = "No text could be extracted.";

        var uploadsFolder = Path.Combine(Directory.GetCurrentDirectory(), "Uploads");

        if (!Directory.Exists(uploadsFolder))
            Directory.CreateDirectory(uploadsFolder);

        var uniqueFileName = $"{Guid.NewGuid()}_{file.FileName}";
        var filePath = Path.Combine(uploadsFolder, uniqueFileName);

        await using (var stream = new FileStream(filePath, FileMode.Create))
        {
            await file.CopyToAsync(stream);
        }

        var fields = await _databaseService.GetFieldsByDocTypeAsync(docType);

        if (fields.Count == 0)
        {
            return BadRequest(new
            {
                error = $"No fields found in doc_type_fields table for document type '{docType}'."
            });
        }

        var extractedJson = await _aiService.ExtractData(fileText, docType, fields);

        return Ok(new
        {
            fileName = file.FileName,
            savedFileName = uniqueFileName,
            savedFilePath = filePath,
            fileSize = file.Length,
            fileType = file.ContentType,
            extractedData = extractedJson,
            extractedText = fileText,
            docType = docType,
            message = "✅ AI processing complete!"
        });
    }

    [HttpPost("save")]
    public async Task<IActionResult> SaveData([FromBody] JsonElement data)
    {
        try
        {
            string docType = GetStringValue(data, "docType");
            string fileName = GetStringValue(data, "fileName");
            string savedFileName = GetStringValue(data, "savedFileName");
            string extractedText = GetStringValue(data, "extractedText");

            if (string.IsNullOrWhiteSpace(docType))
                return BadRequest(new { success = false, error = "docType is missing." });

            if (string.IsNullOrWhiteSpace(fileName))
                fileName = "Unknown File";

            if (string.IsNullOrWhiteSpace(savedFileName))
                savedFileName = fileName;

            var saveUploadsFolder = Path.Combine(Directory.GetCurrentDirectory(), "Uploads");
            var fullFilePath = Path.Combine(saveUploadsFolder, savedFileName);

            var extractedDataJson = ExtractDataJson(data);

            var newId = await _databaseService.SaveDocumentAsync(
                docType: docType,
                docName: fileName,
                fileName: savedFileName,
                extractedJson: extractedDataJson,
                ocrText: extractedText,
                attachPath: fullFilePath
            );

            return Ok(new
            {
                success = true,
                id = newId,
                message = "✅ Data saved to PostgreSQL successfully!"
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

    [HttpPut("update/{id}")]
    public async Task<IActionResult> UpdateData(int id, [FromBody] JsonElement data)
    {
        try
        {
            string docType = GetStringValue(data, "docType");
            string fileName = GetStringValue(data, "fileName");
            string savedFileName = GetStringValue(data, "savedFileName");
            string extractedText = GetStringValue(data, "extractedText");

            if (string.IsNullOrWhiteSpace(docType))
                return BadRequest(new { success = false, error = "docType is missing." });

            if (string.IsNullOrWhiteSpace(fileName))
                fileName = "Unknown File";

            if (string.IsNullOrWhiteSpace(savedFileName))
                savedFileName = fileName;

            var saveUploadsFolder = Path.Combine(Directory.GetCurrentDirectory(), "Uploads");
            var fullFilePath = Path.Combine(saveUploadsFolder, savedFileName);

            var extractedDataJson = ExtractDataJson(data);

            var updated = await _databaseService.UpdateDocumentAsync(
                id: id,
                docType: docType,
                docName: fileName,
                fileName: savedFileName,
                extractedJson: extractedDataJson,
                ocrText: extractedText,
                attachPath: fullFilePath
            );

            if (!updated)
            {
                return NotFound(new
                {
                    success = false,
                    message = "Document not found. It may have been deleted."
                });
            }

            return Ok(new
            {
                success = true,
                id = id,
                message = "✅ Data updated in PostgreSQL successfully!"
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

    [HttpGet("all")]
    public async Task<IActionResult> GetAllDocuments()
    {
        var documents = await _databaseService.GetAllDocumentsAsync();

        return Ok(new
        {
            success = true,
            count = documents.Count,
            data = documents
        });
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteDocument(int id)
    {
        var deleted = await _databaseService.DeleteDocumentAsync(id);

        if (!deleted)
        {
            return NotFound(new
            {
                success = false,
                message = "Document not found."
            });
        }

        return Ok(new
        {
            success = true,
            message = "Document deleted from database successfully."
        });
    }

    [HttpGet("view/{fileName}")]
    public IActionResult ViewDocument(string fileName)
    {
        var uploadsFolder = Path.Combine(Directory.GetCurrentDirectory(), "Uploads");
        var filePath = Path.Combine(uploadsFolder, fileName);

        if (!System.IO.File.Exists(filePath))
            return NotFound(new { error = "File not found." });

        var fileBytes = System.IO.File.ReadAllBytes(filePath);
        var contentType = GetContentType(fileName);

        return File(fileBytes, contentType);
    }

    private static string GetStringValue(JsonElement data, string propertyName)
    {
        if (data.TryGetProperty(propertyName, out var value))
        {
            if (value.ValueKind == JsonValueKind.String)
                return value.GetString() ?? "";

            return value.ToString();
        }

        return "";
    }

    private static string ExtractDataJson(JsonElement data)
    {
        if (data.TryGetProperty("extractedData", out var extractedData))
        {
            if (extractedData.ValueKind == JsonValueKind.String)
                return extractedData.GetString() ?? "{}";

            return extractedData.GetRawText();
        }

        var dictionary = new Dictionary<string, object?>();

        foreach (var property in data.EnumerateObject())
        {
            if (property.Name is "docType" or "fileName" or "savedFileName" or "savedFilePath" or "extractedText" or "attachPath")
                continue;

            dictionary[property.Name] = property.Value.ToString();
        }

        return JsonSerializer.Serialize(dictionary);
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
