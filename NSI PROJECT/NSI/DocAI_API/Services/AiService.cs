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

        // ─── MRZ FALLBACK/OVERRIDE for ID-like documents ───
        // The AI model struggles with MRZ-encoded fields (dateOfBirth, nationality,
        // expiryDate) because they're positional codes, not labeled text. The MRZ
        // parser handles these deterministically and more accurately when an MRZ
        // block is present in the OCR'd text, so we prefer it for those fields.
        if (docType == "NationalID" || docType == "Passport")
        {
            var mrz = MrzParser.TryParse(fileText);
            if (mrz != null)
            {
                Console.WriteLine("🪪 MRZ block detected — merging MRZ-parsed fields into result.");
                Console.WriteLine($"🪪 MRZ: dob={mrz.DateOfBirth}, expiry={mrz.ExpiryDate}, nationality={mrz.Nationality}, name={mrz.FullName}");

                if (!string.IsNullOrWhiteSpace(mrz.DateOfBirth) && result.ContainsKey("dateOfBirth"))
                    result["dateOfBirth"] = mrz.DateOfBirth;

                if (!string.IsNullOrWhiteSpace(mrz.ExpiryDate) && result.ContainsKey("expiryDate"))
                    result["expiryDate"] = mrz.ExpiryDate;

                if (!string.IsNullOrWhiteSpace(mrz.Nationality) && result.ContainsKey("nationality"))
                    result["nationality"] = mrz.Nationality;

                // Prefer MRZ's cleanly-parsed name over the AI's name only if the AI's
                // value is empty or looks like a raw, unparsed MRZ fragment (contains "<").
                if (!string.IsNullOrWhiteSpace(mrz.FullName) && result.ContainsKey("fullName"))
                {
                    var aiName = result["fullName"]?.ToString() ?? "";
                    if (string.IsNullOrWhiteSpace(aiName) || aiName.Contains('<'))
                    {
                        result["fullName"] = mrz.FullName;
                    }
                }
            }
            else
            {
                Console.WriteLine("🪪 No MRZ block detected in this document's text.");
            }
        }

        return JsonSerializer.Serialize(result);
    }

    private async Task<Dictionary<string, object>> CallOllama(string fileText, string docType)
    {
        Console.WriteLine("========================================");
        Console.WriteLine("🤖 SENDING TO OLLAMA:");
        Console.WriteLine($"📄 Document Type: {docType}");
        Console.WriteLine($"📄 Text length: {fileText.Length} characters");
        Console.WriteLine("========================================");
        Console.WriteLine("📄 FULL TEXT SENT TO OLLAMA:");
        Console.WriteLine("========================================");
        Console.WriteLine(fileText);
        Console.WriteLine("========================================");

        string prompt = BuildPrompt(docType, fileText);

        // Log the EXACT prompt being sent so we can visually verify the JSON template
        // inside it is not corrupted before it ever reaches Ollama.
        Console.WriteLine("📝 FULL PROMPT SENT TO OLLAMA:");
        Console.WriteLine("========================================");
        Console.WriteLine(prompt);
        Console.WriteLine("========================================");

        var requestBody = new
        {
            model = _modelName,
            prompt = prompt,
            stream = false,
            format = "json", // Forces Ollama's grammar-constrained decoding to emit valid JSON
            options = new { temperature = 0.1, top_p = 0.9 }
        };

        var content = new StringContent(
            JsonSerializer.Serialize(requestBody),
            Encoding.UTF8,
            "application/json"
        );

        HttpResponseMessage response;
        string responseString;

        try
        {
            response = await _httpClient.PostAsync("http://localhost:11434/api/generate", content);
            responseString = await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Failed to reach Ollama: {ex.Message}");
            return new();
        }

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"❌ Ollama returned HTTP {(int)response.StatusCode}: {responseString}");
            return new();
        }

        string extractedData;
        try
        {
            var jsonResponse = JsonSerializer.Deserialize<JsonElement>(responseString);
            extractedData = jsonResponse.GetProperty("response").GetString() ?? "{}";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Failed to parse Ollama envelope: {ex.Message}");
            Console.WriteLine($"❌ Raw response string: {responseString}");
            return new();
        }

        Console.WriteLine("📥 RAW OLLAMA RESPONSE:");
        Console.WriteLine(extractedData);
        Console.WriteLine("========================================");

        extractedData = CleanJsonResponse(extractedData, docType);

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object>>(extractedData) ?? new();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ JSON parse failed for {docType}: {ex.Message}");
            Console.WriteLine($"⚠️ Raw cleaned response: {extractedData}");
            return new();
        }
    }

    private string CleanJsonResponse(string response, string docType)
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
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ JSON parse failed for {docType}: {ex.Message}");
            Console.WriteLine($"⚠️ Raw cleaned response: {response}");
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

        Console.WriteLine("🧩 JSON TEMPLATE FOR PROMPT:");
        Console.WriteLine(jsonFields);
        Console.WriteLine("========================================");

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
            "Invoice" => @"1. INVOICE NUMBER: Look for labels like 'Invoice #', 'Invoice Number', 'INV-', 'Invoice No'.
2. DATE: Look for labels like 'Date', 'Invoice Date', 'Issue Date'.
3. DUE DATE: Look for labels like 'Due Date', 'Payment Due', 'Due On'.
4. VENDOR NAME: Look for labels like 'Vendor', 'Supplier', 'From', 'Company', 'Seller'.
5. EMAIL: Look for email address pattern (xxx@xxx.xxx).
6. AMOUNT: Look for labels like 'Total', 'Grand Total', 'Amount Due', 'Balance Due'.",

            "NationalID" => @"1. ID NUMBER: Look for labels like 'ID Number', 'ID No', 'National ID', 'رقم الهوية', 'ID:'.
2. FULL NAME: Look for labels like 'Name', 'Full Name', 'Holder Name', 'الاسم', 'ENGLISH NAME'.
3. DATE OF BIRTH: Look for labels like 'DOB', 'Date of Birth', 'تاريخ الميلاد', 'Birth Date'.
4. NATIONALITY: Look for labels like 'Nationality', 'الجنسية', 'Country'.
5. EXPIRY DATE: Look for labels like 'Expiry', 'Expiry Date', 'تاريخ الانتهاء', 'Valid Until'.",

            "Passport" => @"1. PASSPORT NUMBER: Look for labels like 'Passport No', 'Passport Number', 'رقم جواز السفر'.
2. FULL NAME: Look for labels like 'Name', 'Full Name', 'Holder Name', 'الاسم'.
3. DATE OF BIRTH: Look for labels like 'DOB', 'Date of Birth', 'تاريخ الميلاد'.
4. NATIONALITY: Look for labels like 'Nationality', 'الجنسية'.
5. EXPIRY DATE: Look for labels like 'Expiry Date', 'Date of Expiry', 'تاريخ الانتهاء'.",

            "EmployeeRecord" => @"1. EMPLOYEE ID: Look for labels like 'Employee ID', 'EMP', 'Staff ID', 'رقم الموظف'.
2. FULL NAME: Look for labels like 'Name', 'Employee Name', 'الاسم'.
3. DEPARTMENT: Look for labels like 'Department', 'Dept', 'Division', 'القسم'.
4. POSITION: Look for labels like 'Position', 'Job Title', 'Role', 'المسمى الوظيفي'.
5. EMAIL: Look for email address pattern.
6. PHONE: Look for phone number pattern.",

            "Receipt" => @"1. RECEIPT NUMBER: Look for labels like 'Receipt #', 'Receipt Number', 'رقم الإيصال', 'Transaction ID'.
2. DATE: Look for labels like 'Receipt Date', 'Date', 'تاريخ الإيصال', 'Transaction Date'.
3. VENDOR NAME: Look for the organization/entity name at the top of the receipt.
4. AMOUNT: Look for labels like 'Total', 'Total Amount', 'Grand Total', 'المجموع الإجمالي'.
5. PAYMENT METHOD: Look for labels like 'Payment Method', 'طريقة الدفع', 'Paid by', 'Cash', 'Card'.",

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

            "Passport" => @"Example:
Text:
""PASSPORT NO: P1234567
NAME: JOHN MICHAEL SMITH
DATE OF BIRTH: 12/05/1990
NATIONALITY: BRITISH
DATE OF EXPIRY: 11/05/2030""

Expected JSON:
{
  ""passportNumber"": ""P1234567"",
  ""fullName"": ""JOHN MICHAEL SMITH"",
  ""dateOfBirth"": ""12/05/1990"",
  ""nationality"": ""BRITISH"",
  ""expiryDate"": ""11/05/2030""
}",

            "EmployeeRecord" => @"Example:
Text:
""EMPLOYEE ID: EMP-00231
NAME: SARA AHMED KHALIL
DEPARTMENT: FINANCE
POSITION: SENIOR ACCOUNTANT
EMAIL: sara.khalil@company.com
PHONE: +971-50-1234567""

Expected JSON:
{
  ""employeeId"": ""EMP-00231"",
  ""fullName"": ""SARA AHMED KHALIL"",
  ""department"": ""FINANCE"",
  ""position"": ""SENIOR ACCOUNTANT"",
  ""email"": ""sara.khalil@company.com"",
  ""phone"": ""+971-50-1234567""
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