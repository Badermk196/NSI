using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DocAI_API.Services;

public class AiService
{
    private readonly HttpClient _httpClient;
    private readonly string _modelName;

    private static readonly Dictionary<string, string[]> DocTypeFields = new()
    {
        ["Invoice"] = new[] { "invoiceNumber", "date", "dueDate", "vendorName", "email", "amount" },
        ["NationalID"] = new[] { "idNumber", "fullName", "dateOfBirth", "nationality", "expiryDate" },
        ["Passport"] = new[] { "passportNumber", "fullName", "dateOfBirth", "nationality", "expiryDate" },
        ["EmployeeRecord"] = new[] { "employeeId", "fullName", "department", "position", "email", "phone" },
        ["Receipt"] = new[] { "receiptNumber", "date", "vendorName", "amount", "paymentMethod" }
    };

    public AiService()
    {
        _httpClient = new HttpClient();
        _modelName = "qwen2.5:7b";
    }

    public async Task<string> ExtractInvoiceData(string fileText, string docType = "Invoice")
    {
        // ─── OLLAMA READS THE CONTENT DIRECTLY ───
        var aiData = await CallOllama(fileText, docType);

        var fields = DocTypeFields.TryGetValue(docType, out var f) ? f : Array.Empty<string>();
        var result = new Dictionary<string, object>();

        foreach (var field in fields)
        {
            if (aiData.TryGetValue(field, out var value) && !string.IsNullOrWhiteSpace(value?.ToString()))
            {
                result[field] = value!;
            }
            else
            {
                result[field] = "";
            }
        }

        return JsonSerializer.Serialize(result);
    }

    private async Task<Dictionary<string, object>> CallOllama(string fileText, string docType)
    {
        string prompt = BuildPrompt(docType, fileText);

        var requestBody = new
        {
            model = _modelName,
            prompt = prompt,
            stream = false,
            options = new { temperature = 0.1, top_p = 0.9 }
        };

        var content = new StringContent(
            JsonSerializer.Serialize(requestBody),
            Encoding.UTF8,
            "application/json"
        );

        var response = await _httpClient.PostAsync("http://localhost:11434/api/generate", content);
        var responseString = await response.Content.ReadAsStringAsync();

        var jsonResponse = JsonSerializer.Deserialize<JsonElement>(responseString);
        var extractedData = jsonResponse.GetProperty("response").GetString() ?? "{}";

        extractedData = CleanJsonResponse(extractedData);

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object>>(extractedData) ?? new();
        }
        catch
        {
            return new();
        }
    }

    private string CleanJsonResponse(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return "{}";

        response = response.Replace("```json", "").Replace("```", "");
        response = Regex.Replace(response, @"^[^{]*", "");
        response = Regex.Replace(response, @"[^}]*$", "");

        int start = response.IndexOf('{');
        int end = response.LastIndexOf('}');

        if (start >= 0 && end > start)
        {
            response = response.Substring(start, end - start + 1);
        }

        response = response.Trim();

        try
        {
            var parsed = JsonSerializer.Deserialize<object>(response);
            return JsonSerializer.Serialize(parsed);
        }
        catch
        {
            var match = Regex.Match(response, @"\{[^{}]*\}");
            if (match.Success)
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<object>(match.Value);
                    return JsonSerializer.Serialize(parsed);
                }
                catch
                {
                    return "{}";
                }
            }
            return "{}";
        }
    }

    public Dictionary<string, object> FilterFieldsForDocType(Dictionary<string, object> data, string docType)
    {
        var allowedFields = DocTypeFields.TryGetValue(docType, out var fields)
            ? new HashSet<string>(fields)
            : new HashSet<string>(data.Keys);

        var filteredData = new Dictionary<string, object>();
        foreach (var key in data.Keys)
        {
            if (allowedFields.Contains(key))
            {
                filteredData[key] = data[key];
            }
        }
        return filteredData;
    }

    private string BuildPrompt(string docType, string fileText)
    {
        string instructions = GetFieldsForDocType(docType);
        string jsonFields = GetJsonFieldsForDocType(docType);
        string fewShot = GetFewShotExample(docType);

        return $@"You are a strict document data extractor. You ONLY output valid JSON, nothing else.

Document type: {docType}

Field instructions:
{instructions}

Rules:
- If a field is not present in the text, return an empty string for it. Never invent data.
- Look for both English and Arabic labels.
- Return ONLY the JSON object below, filled in. No markdown, no code fences, no explanation, no comments.

{fewShot}

Now extract from this document text:
---
{fileText}
---

Output JSON (and ONLY this JSON, filled in with real values from the text above):
{{
{jsonFields}
}}";
    }

    private string GetFieldsForDocType(string docType)
    {
        return docType switch
        {
            "Invoice" => @"1. INVOICE NUMBER: Look for 'INVOICE #', 'Invoice Number', 'INV-'.
2. DATE: Look for 'Date:', 'Invoice Date:'.
3. DUE DATE: Look for 'Due Date:', 'Payment Due:'.
4. VENDOR NAME: Look for 'Vendor:', 'Supplier:', 'Company:', 'From:'.
5. EMAIL: Look for email address (xxx@xxx.xxx).
6. AMOUNT: Look for 'Total:', 'Grand Total:', 'Amount Due:'.",

            "NationalID" => @"1. ID NUMBER: Look for 'ID NUMBER:', 'ID No', 'National ID', or a long number.
2. FULL NAME: Look for 'ENGLISH NAME:', 'NAME:', 'Full Name:', or names.
3. DATE OF BIRTH: Look for 'DATE OF BIRTH:', 'DOB:', 'BIRTH DATE:', or 'Issue Date'.
4. NATIONALITY: Look for 'NATIONALITY:', 'Country:'.
5. EXPIRY DATE: Look for 'EXPIRE DATE:', 'EXPIRY DATE:', 'Expiry:.",

            "Passport" => @"1. PASSPORT NUMBER: Look for 'Passport No', 'Passport Number'.
2. FULL NAME: Look for 'Name:', 'Full Name:'.
3. DATE OF BIRTH: Look for 'DOB:', 'Date of Birth:'.
4. NATIONALITY: Look for 'Nationality:'.
5. EXPIRY DATE: Look for 'Expiry Date:', 'Date of Expiry:.",

            "EmployeeRecord" => @"1. EMPLOYEE ID: Look for 'Employee ID', 'EMP', 'Staff ID'.
2. FULL NAME: Look for 'Name:', 'Employee Name:'.
3. DEPARTMENT: Look for 'Department:', 'Dept:', 'Division:'.
4. POSITION: Look for 'Position:', 'Job Title:', 'Role:'.
5. EMAIL: Look for email address.
6. PHONE: Look for phone number.",

            "Receipt" => @"1. RECEIPT NUMBER: Look for 'Receipt Number', 'رقم الإيصال', 'Transaction ID'.
2. DATE: Look for 'Receipt Date', 'تاريخ الإيصال', 'Date:'.
3. VENDOR NAME: The organization/entity name, usually at the top of the receipt.
4. AMOUNT: Look for 'Total Amount', 'المجموع الإجمالي', 'Grand Total'.
5. PAYMENT METHOD: Look for 'Payment Method', 'طريقة الدفع', 'Paid by'.",

            _ => @"1. DOCUMENT NUMBER
2. DATE
3. NAME
4. DESCRIPTION"
        };
    }

    private string GetFewShotExample(string docType)
    {
        return docType switch
        {
            "Receipt" => @"Example:
Text:
""UNITED ARAB EMIRATES
Receipt No: 88213456
Date: 14/03/2025
Total Amount: 250.00
Payment Method: Credit Card""

Expected JSON:
{
  ""receiptNumber"": ""88213456"",
  ""date"": ""14/03/2025"",
  ""vendorName"": ""UNITED ARAB EMIRATES"",
  ""amount"": 250.00,
  ""paymentMethod"": ""Credit Card""
}",

            "NationalID" => @"Example:
Text:
""ID NUMBER: 784-2003-8405 903-4
ENGLISH NAME: BADER MAHMOUD SAYYED ALMAKAWI
EXPIRE DATE: 3/29/2027
NATIONALITY: JORDAN""

Expected JSON:
{
  ""idNumber"": ""784-2003-8405 903-4"",
  ""fullName"": ""BADER MAHMOUD SAYYED ALMAKAWI"",
  ""dateOfBirth"": """",
  ""nationality"": ""JORDAN"",
  ""expiryDate"": ""3/29/2027""
}",

            _ => ""
        };
    }

    private string GetJsonFieldsForDocType(string docType)
    {
        return docType switch
        {
            "Invoice" => @"
    ""invoiceNumber"": """",
    ""date"": """",
    ""dueDate"": """",
    ""vendorName"": """",
    ""email"": """",
    ""amount"": 0",

            "NationalID" => @"
    ""idNumber"": """",
    ""fullName"": """",
    ""dateOfBirth"": """",
    ""nationality"": """",
    ""expiryDate"": """"",

            "Passport" => @"
    ""passportNumber"": """",
    ""fullName"": """",
    ""dateOfBirth"": """",
    ""nationality"": """",
    ""expiryDate"": """"",

            "EmployeeRecord" => @"
    ""employeeId"": """",
    ""fullName"": """",
    ""department"": """",
    ""position"": """",
    ""email"": """",
    ""phone"": """"",

            "Receipt" => @"
    ""receiptNumber"": """",
    ""date"": """",
    ""vendorName"": """",
    ""amount"": 0,
    ""paymentMethod"": """"",

            _ => @"
    ""documentNumber"": """",
    ""date"": """",
    ""name"": """",
    ""description"": """""
        };
    }
}