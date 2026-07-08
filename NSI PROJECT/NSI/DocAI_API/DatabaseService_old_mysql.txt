using MySql.Data.MySqlClient;
using System.Text.Json;

namespace DocAI_API.Services;

public class DatabaseService
{
    private readonly string _connectionString;

    public DatabaseService()
    {
        // ─── UPDATE THIS WITH YOUR MYSQL CONNECTION STRING ───
        _connectionString = "Server=localhost;Database=docai_db;User Id=root;Password=yourpassword;";
        
        CreateTable();
    }

    private void CreateTable()
    {
        using var connection = new MySqlConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS Invoices (
                Id INT AUTO_INCREMENT PRIMARY KEY,
                InvoiceNumber VARCHAR(100),
                InvoiceDate VARCHAR(50),
                DueDate VARCHAR(50),
                VendorName VARCHAR(200),
                Email VARCHAR(200),
                Amount DECIMAL(18,2),
                FileName VARCHAR(255),
                RawJson TEXT,
                CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP
            )
        ";
        command.ExecuteNonQuery();
    }

    // ─── SAVE INVOICE ───
    public async Task<int> SaveInvoice(string extractedData, string fileName)
    {
        using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();

        using var doc = JsonDocument.Parse(extractedData);
        var root = doc.RootElement;

        var invoiceNumber = root.GetProperty("invoiceNumber").GetString() ?? "";
        var invoiceDate = root.GetProperty("invoiceDate").GetString() ?? "";
        var dueDate = root.GetProperty("dueDate").GetString() ?? "";
        var vendorName = root.GetProperty("vendorName").GetString() ?? "";
        var email = root.TryGetProperty("email", out var emailProp) ? emailProp.GetString() ?? "" : "";
        var amount = root.GetProperty("amount").GetDouble();

        var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO Invoices (InvoiceNumber, InvoiceDate, DueDate, VendorName, Email, Amount, FileName, RawJson)
            VALUES (@Number, @Date, @Due, @Vendor, @Email, @Amount, @FileName, @RawJson);
            SELECT LAST_INSERT_ID();
        ";

        command.Parameters.AddWithValue("@Number", invoiceNumber);
        command.Parameters.AddWithValue("@Date", invoiceDate);
        command.Parameters.AddWithValue("@Due", dueDate);
        command.Parameters.AddWithValue("@Vendor", vendorName);
        command.Parameters.AddWithValue("@Email", email);
        command.Parameters.AddWithValue("@Amount", amount);
        command.Parameters.AddWithValue("@FileName", fileName);
        command.Parameters.AddWithValue("@RawJson", extractedData);

        var newId = Convert.ToInt32(await command.ExecuteScalarAsync());
        return newId;
    }

    // ─── GET ALL INVOICES ───
    public async Task<List<Dictionary<string, object>>> GetAllInvoices()
    {
        var invoices = new List<Dictionary<string, object>>();

        using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT Id, InvoiceNumber, InvoiceDate, DueDate, VendorName, Email, Amount, FileName, CreatedAt
            FROM Invoices
            ORDER BY Id DESC
        ";

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var invoice = new Dictionary<string, object>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                invoice[reader.GetName(i)] = reader.GetValue(i);
            }
            invoices.Add(invoice);
        }

        return invoices;
    }

    // ─── GET SINGLE INVOICE (FIXED NULL WARNING) ───
    public async Task<Dictionary<string, object>?> GetInvoice(int id)
    {
        using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT Id, InvoiceNumber, InvoiceDate, DueDate, VendorName, Email, Amount, FileName, RawJson, CreatedAt
            FROM Invoices
            WHERE Id = @Id
        ";
        command.Parameters.AddWithValue("@Id", id);

        using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var invoice = new Dictionary<string, object>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                invoice[reader.GetName(i)] = reader.GetValue(i);
            }
            return invoice;
        }

        return null;
    }

    // ─── DELETE INVOICE ───
    public async Task<bool> DeleteInvoice(int id)
    {
        using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Invoices WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id);

        var rowsAffected = await command.ExecuteNonQueryAsync();
        return rowsAffected > 0;
    }

    // ─── UPDATE INVOICE ───
    public async Task<bool> UpdateInvoice(int id, string jsonData)
    {
        using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();

        using var doc = JsonDocument.Parse(jsonData);
        var root = doc.RootElement;

        var invoiceNumber = root.GetProperty("invoiceNumber").GetString() ?? "";
        var invoiceDate = root.GetProperty("date").GetString() ?? "";
        var dueDate = root.GetProperty("dueDate").GetString() ?? "";
        var vendorName = root.GetProperty("vendorName").GetString() ?? "";
        var email = root.TryGetProperty("email", out var emailProp) ? emailProp.GetString() ?? "" : "";
        var amount = root.GetProperty("amount").GetDouble();

        var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE Invoices
            SET InvoiceNumber = @Number,
                InvoiceDate = @Date,
                DueDate = @Due,
                VendorName = @Vendor,
                Email = @Email,
                Amount = @Amount,
                RawJson = @RawJson
            WHERE Id = @Id
        ";

        command.Parameters.AddWithValue("@Id", id);
        command.Parameters.AddWithValue("@Number", invoiceNumber);
        command.Parameters.AddWithValue("@Date", invoiceDate);
        command.Parameters.AddWithValue("@Due", dueDate);
        command.Parameters.AddWithValue("@Vendor", vendorName);
        command.Parameters.AddWithValue("@Email", email);
        command.Parameters.AddWithValue("@Amount", amount);
        command.Parameters.AddWithValue("@RawJson", jsonData);

        var rowsAffected = await command.ExecuteNonQueryAsync();
        return rowsAffected > 0;
    }
}