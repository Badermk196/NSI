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

    public async Task<bool> UpdateDocumentAsync(
        int id,
        string docType,
        string docName,
        string fileName,
        string extractedJson,
        string ocrText,
        string attachPath)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var docTypeId = await GetDocTypeIdAsync(connection, docType);

        await using var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE public.""Data""
            SET
                doc_tp_id = @docTpId,
                ""Data"" = @data::json,
                doc_name = @docName,
                file_name = @fileName,
                attach_ocr = @attachOcr,
                attach_path = @attachPath
            WHERE data_id = @id;
        ";

        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@docTpId", docTypeId);
        command.Parameters.AddWithValue("@data", extractedJson);
        command.Parameters.AddWithValue("@docName", docName ?? "");
        command.Parameters.AddWithValue("@fileName", fileName ?? "");
        command.Parameters.AddWithValue("@attachOcr", ocrText ?? "");
        command.Parameters.AddWithValue("@attachPath", attachPath ?? "");

        var rowsAffected = await command.ExecuteNonQueryAsync();
        return rowsAffected > 0;
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
            documents.Add(new Dictionary<string, object?>
            {
                ["data_id"] = reader["data_id"],
                ["doc_tp_id"] = reader["doc_tp_id"],
                ["doc_tp_name"] = reader["doc_tp_name"],
                ["Data"] = reader["Data"]?.ToString(),
                ["doc_name"] = reader["doc_name"],
                ["file_name"] = reader["file_name"],
                ["attach_ocr"] = reader["attach_ocr"],
                ["attach_path"] = reader["attach_path"]
            });
        }

        return documents;
    }

    public async Task<List<Dictionary<string, object?>>> GetAllDocumentTypesAsync()
    {
        var documentTypes = new List<Dictionary<string, object?>>();

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT 
                doc_tp_id,
                doc_tp_name
            FROM public.doc_types
            ORDER BY doc_tp_id;
        ";

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            documentTypes.Add(new Dictionary<string, object?>
            {
                ["doc_tp_id"] = reader["doc_tp_id"],
                ["doc_tp_name"] = reader["doc_tp_name"]
            });
        }

        return documentTypes;
    }

    public async Task<List<string>> GetFieldsByDocTypeAsync(string docType)
    {
        var fields = new List<string>();

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT
                df.field_name
            FROM public.doc_type_fields df
            INNER JOIN public.doc_types dt
                ON df.doc_tp_id = dt.doc_tp_id
            WHERE LOWER(dt.doc_tp_name) = LOWER(@docType)
            ORDER BY df.field_id;
        ";

        command.Parameters.AddWithValue("@docType", docType);

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            fields.Add(reader["field_name"]?.ToString() ?? "");
        }

        return fields.Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
    }

    public async Task<bool> DeleteDocumentAsync(int id)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = @"
            DELETE FROM public.""Data""
            WHERE data_id = @id;
        ";

        command.Parameters.AddWithValue("@id", id);

        var rowsAffected = await command.ExecuteNonQueryAsync();
        return rowsAffected > 0;
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
