using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DocAI_API.Services;

public class AiService
{
    private readonly HttpClient _httpClient;
    private readonly string _modelName;

    public AiService()
    {
        _httpClient = new HttpClient();
        _modelName = "qwen2.5:7b";
    }

    public async Task<string> ExtractData(string fileText, string docType, List<string> fields)
    {
        var cleanFields = fields
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f.Trim())
            .Distinct()
            .ToList();

        if (cleanFields.Count == 0)
            return "{}";

        var aiData = await CallOllama(fileText, docType, cleanFields);

        var result = new Dictionary<string, object?>();

        foreach (var field in cleanFields)
        {
            if (aiData.TryGetValue(field, out var value) && value != null)
                result[field] = value;
            else
                result[field] = "";
        }

        ApplyRegexFallbacks(fileText, result);
        ApplyMrzFallback(fileText, docType, result);

        return JsonSerializer.Serialize(result);
    }

    private async Task<Dictionary<string, object>> CallOllama(string fileText, string docType, List<string> fields)
    {
        string prompt = BuildPrompt(docType, fileText, fields);

        Console.WriteLine("========== AI PROMPT ==========");
        Console.WriteLine(prompt);
        Console.WriteLine("===============================");

        var requestBody = new
        {
            model = _modelName,
            prompt,
            stream = false,
            format = "json",
            options = new
            {
                temperature = 0.0,
                top_p = 0.9
            }
        };

        var content = new StringContent(
            JsonSerializer.Serialize(requestBody),
            Encoding.UTF8,
            "application/json"
        );

        try
        {
            var response = await _httpClient.PostAsync("http://localhost:11434/api/generate", content);
            var responseString = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Ollama error: {responseString}");
                return new Dictionary<string, object>();
            }

            var jsonResponse = JsonSerializer.Deserialize<JsonElement>(responseString);
            var extractedData = jsonResponse.GetProperty("response").GetString() ?? "{}";

            Console.WriteLine("========== RAW AI RESPONSE ==========");
            Console.WriteLine(extractedData);
            Console.WriteLine("=====================================");

            extractedData = CleanJsonResponse(extractedData);

            return JsonSerializer.Deserialize<Dictionary<string, object>>(extractedData)
                   ?? new Dictionary<string, object>();
        }
        catch (Exception ex)
        {
            Console.WriteLine("AI extraction error: " + ex.Message);
            return new Dictionary<string, object>();
        }
    }

    private string BuildPrompt(string docType, string fileText, List<string> fields)
    {
        var jsonTemplate = string.Join(",\n", fields.Select(field => $"    \"{field}\": \"\""));
        var fieldList = string.Join("\n", fields.Select(field => $"- {field}"));

        return $@"
You are a strict OCR document data extraction system.

Document Type:
{docType}

Extract ONLY these fields:
{fieldList}

Important rules:
- Return ONLY valid JSON.
- Do not write explanation.
- Do not use markdown.
- Do not invent values.
- If a field is missing, return an empty string.
- Use the exact field names provided.
- Do not add extra fields.
- Extract from English or Arabic text.
- Preserve important numbers exactly as written.
- For amount fields, return only the numeric amount without currency symbols if possible.
- For date fields, return the date exactly as found.
- For passports and ID cards, use visible labels and MRZ lines if available.
- For receipts, vendorName is usually the store/company name near the top.
- For employee records, look for employee ID, name, department, position, email, and phone.

Document OCR text:
---
{fileText}
---

Return this exact JSON object:
{{
{jsonTemplate}
}}
";
    }

    private string CleanJsonResponse(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return "{}";

        response = response.Replace("```json", "").Replace("```", "").Trim();

        int start = response.IndexOf('{');
        int end = response.LastIndexOf('}');

        if (start >= 0 && end > start)
            response = response.Substring(start, end - start + 1);

        try
        {
            var parsed = JsonSerializer.Deserialize<object>(response);
            return JsonSerializer.Serialize(parsed);
        }
        catch
        {
            return "{}";
        }
    }

    private void ApplyRegexFallbacks(string fileText, Dictionary<string, object?> result)
    {
        if (string.IsNullOrWhiteSpace(fileText))
            return;

        if (result.ContainsKey("email") && IsEmpty(result["email"]))
        {
            var emailMatch = Regex.Match(fileText, @"[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}", RegexOptions.IgnoreCase);
            if (emailMatch.Success)
                result["email"] = emailMatch.Value;
        }

        if (result.ContainsKey("phone") && IsEmpty(result["phone"]))
        {
            var phoneMatch = Regex.Match(fileText, @"(\+?\d[\d\s\-()]{7,}\d)");
            if (phoneMatch.Success)
                result["phone"] = phoneMatch.Value.Trim();
        }

        if (result.ContainsKey("invoiceNumber") && IsEmpty(result["invoiceNumber"]))
        {
            var invoiceMatch = Regex.Match(fileText, @"\bINV[-\s]?\d+\b", RegexOptions.IgnoreCase);
            if (invoiceMatch.Success)
                result["invoiceNumber"] = invoiceMatch.Value;
        }

        if (result.ContainsKey("idNumber") && IsEmpty(result["idNumber"]))
        {
            var idMatch = Regex.Match(fileText, @"\b\d{3}-\d{4}-\d{7}-\d\b");
            if (idMatch.Success)
                result["idNumber"] = idMatch.Value;
        }

        if (result.ContainsKey("passportNumber") && IsEmpty(result["passportNumber"]))
        {
            var passportMatch = Regex.Match(fileText, @"\b[A-Z][0-9]{6,9}\b", RegexOptions.IgnoreCase);
            if (passportMatch.Success)
                result["passportNumber"] = passportMatch.Value;
        }
    }

    private void ApplyMrzFallback(string fileText, string docType, Dictionary<string, object?> result)
    {
        if (!docType.Equals("NationalID", StringComparison.OrdinalIgnoreCase) &&
            !docType.Equals("Passport", StringComparison.OrdinalIgnoreCase))
            return;

        var mrz = MrzParser.TryParse(fileText);

        if (mrz == null)
            return;

        if (result.ContainsKey("dateOfBirth") && !string.IsNullOrWhiteSpace(mrz.DateOfBirth))
            result["dateOfBirth"] = mrz.DateOfBirth;

        if (result.ContainsKey("expiryDate") && !string.IsNullOrWhiteSpace(mrz.ExpiryDate))
            result["expiryDate"] = mrz.ExpiryDate;

        if (result.ContainsKey("nationality") && !string.IsNullOrWhiteSpace(mrz.Nationality))
            result["nationality"] = mrz.Nationality;

        if (result.ContainsKey("fullName") && !string.IsNullOrWhiteSpace(mrz.FullName))
        {
            var currentName = result["fullName"]?.ToString() ?? "";

            if (string.IsNullOrWhiteSpace(currentName) || currentName.Contains("<"))
                result["fullName"] = mrz.FullName;
        }
    }

    private bool IsEmpty(object? value)
    {
        return value == null || string.IsNullOrWhiteSpace(value.ToString());
    }
}