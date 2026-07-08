using Npgsql;

namespace DocAI_API.Services;

public class DatabaseService
{
    private readonly string _connectionString;

    public DatabaseService(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new Exception("DefaultConnection is missing in appsettings.json");
    }

    public async Task<int> SaveDocumentAsync(
        string docType,
        string docName,
        string fileName,
        string extractedJson,
        string ocrText,
        string attachPath)
    {
        Console.WriteLine("========== SAVE DEBUG ==========");
        Console.WriteLine("Doc Type: " + docType);
        Console.WriteLine("Doc Name: " + docName);
        Console.WriteLine("File Name: " + fileName);
        Console.WriteLine("OCR Text:");
        Console.WriteLine(string.IsNullOrWhiteSpace(ocrText) ? "[EMPTY]" : ocrText);
        Console.WriteLine("Attach Path:");
        Console.WriteLine(string.IsNullOrWhiteSpace(attachPath) ? "[EMPTY]" : attachPath);
        Console.WriteLine("JSON:");
        Console.WriteLine(extractedJson);
        Console.WriteLine("===============================");

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var docTypeId = await GetDocTypeIdAsync(connection, docType);

        await using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO public.""Data""
                (doc_tp_id, ""Data"", doc_name, file_name, attach_ocr, attach_path)
            VALUES
                (@docTpId, @data::json, @docName, @fileName, @attachOcr, @attachPath)
            RETURNING data_id;
        ";

        command.Parameters.AddWithValue("@docTpId", docTypeId);
        command.Parameters.AddWithValue("@data", extractedJson);
        command.Parameters.AddWithValue("@docName", docName ?? "");
        command.Parameters.AddWithValue("@fileName", fileName ?? "");
        command.Parameters.AddWithValue("@attachOcr", ocrText ?? "");
        command.Parameters.AddWithValue("@attachPath", attachPath ?? "");

        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    public async Task<List<Dictionary<string, object?>>> GetAllDocumentsAsync()
    {
        var documents = new List<Dictionary<string, object?>>();

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT 
                d.data_id,
                d.doc_tp_id,
                dt.doc_tp_name,
                d.""Data"",
                d.doc_name,
                d.file_name,
                d.attach_ocr,
                d.attach_path
            FROM public.""Data"" d
            LEFT JOIN public.doc_types dt ON d.doc_tp_id = dt.doc_tp_id
            ORDER BY d.data_id DESC;
        ";

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>
            {
                ["data_id"] = reader["data_id"],
                ["doc_tp_id"] = reader["doc_tp_id"],
                ["doc_tp_name"] = reader["doc_tp_name"],
                ["Data"] = reader["Data"]?.ToString(),
                ["doc_name"] = reader["doc_name"],
                ["file_name"] = reader["file_name"],
                ["attach_ocr"] = reader["attach_ocr"],
                ["attach_path"] = reader["attach_path"]
            };

            documents.Add(row);
        }

        return documents;
    }

    private static async Task<int> GetDocTypeIdAsync(NpgsqlConnection connection, string docType)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT doc_tp_id
            FROM public.doc_types
            WHERE LOWER(doc_tp_name) = LOWER(@docType)
            LIMIT 1;
        ";

        command.Parameters.AddWithValue("@docType", docType);

        var result = await command.ExecuteScalarAsync();

        if (result == null)
            throw new Exception($"Document type '{docType}' was not found in doc_types table.");

        return Convert.ToInt32(result);
    }
}