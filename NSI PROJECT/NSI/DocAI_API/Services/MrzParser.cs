using System.Text.RegularExpressions;

namespace DocAI_API.Services;

public static class MrzParser
{
    public class MrzResult
    {
        public string DocumentNumber { get; set; } = "";
        public string FullName { get; set; } = "";
        public string DateOfBirth { get; set; } = "";
        public string ExpiryDate { get; set; } = "";
        public string Nationality { get; set; } = "";
        public string DocumentType { get; set; } = "";
    }

    public static MrzResult? TryParse(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return null;

        var lines = rawText
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        return TryParseTd3(lines) ?? TryParseTd1(lines);
    }

    private static MrzResult? TryParseTd3(List<string> lines)
    {
        for (int i = 0; i < lines.Count - 1; i++)
        {
            var l1 = NormalizeMrzLine(lines[i]);
            var l2 = NormalizeMrzLine(lines[i + 1]);

            if (!l1.StartsWith("P<") && !l1.StartsWith("P<<") && !(l1.StartsWith("P") && l1.Length > 5 && l1[1] == '<'))
                continue;

            if (!Regex.IsMatch(l2, @"^[A-Z0-9<]{9}\d[A-Z]{3}\d{6}\d[MFX<]\d{6}\d"))
                continue;

            var docNum = l2.Substring(0, 9).Replace("<", "");
            var nationality = l2.Substring(10, 3).Replace("<", "");
            var dob = ParseMrzDate(l2.Substring(13, 6));
            var expiry = ParseMrzDate(l2.Substring(21, 6));
            var name = ParseNameField(l1.Length > 5 ? l1.Substring(5) : "");

            if (string.IsNullOrEmpty(name))
            {
                name = ParseNameField(l1);
            }

            return new MrzResult
            {
                DocumentType = "TD3",
                DocumentNumber = docNum,
                FullName = name,
                Nationality = nationality,
                DateOfBirth = dob,
                ExpiryDate = expiry
            };
        }
        return null;
    }

    private static MrzResult? TryParseTd1(List<string> lines)
    {
        var line2Regex = new Regex(@"^\d{6}\d[MFX<]\d{6}\d[A-Z<]{3}");

        for (int i = 0; i < lines.Count; i++)
        {
            var candidate = NormalizeMrzLine(lines[i]);
            if (!line2Regex.IsMatch(candidate)) continue;

            var dob = ParseMrzDate(candidate.Substring(0, 6));
            var expiry = ParseMrzDate(candidate.Substring(7, 6));
            var nationality = candidate.Length >= 17 ? candidate.Substring(14, 3).Replace("<", "") : "";

            var nameLine = lines.FirstOrDefault(l => l.Contains("<<"));
            var fullName = nameLine != null ? ParseNameField(NormalizeMrzLine(nameLine)) : "";

            var docNumLine = lines.FirstOrDefault(l => Regex.IsMatch(l.Trim(), @"^\d{7,10}$"));
            var docNumber = docNumLine?.Trim() ?? "";

            if (string.IsNullOrEmpty(fullName) && string.IsNullOrEmpty(docNumber))
                continue;

            return new MrzResult
            {
                DocumentType = "TD1",
                DocumentNumber = docNumber,
                FullName = fullName,
                Nationality = nationality,
                DateOfBirth = dob,
                ExpiryDate = expiry
            };
        }
        return null;
    }

    private static string NormalizeMrzLine(string line) =>
        line.Trim().Replace(" ", "").ToUpperInvariant();

    private static string ParseNameField(string nameField)
    {
        if (string.IsNullOrWhiteSpace(nameField)) return "";

        var clean = nameField.TrimEnd('<');
        var parts = clean.Split("<<", StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "";

        var surname = parts[0].Replace("<", " ").Trim();
        var given = parts.Length > 1 ? parts[1].Replace("<", " ").Trim() : "";

        return string.IsNullOrEmpty(given) ? surname : $"{given} {surname}".Trim();
    }

    private static string ParseMrzDate(string yymmdd)
    {
        if (yymmdd.Length != 6 || !Regex.IsMatch(yymmdd, @"^\d{6}$")) return "";

        int yy = int.Parse(yymmdd.Substring(0, 2));
        int mm = int.Parse(yymmdd.Substring(2, 2));
        int dd = int.Parse(yymmdd.Substring(4, 2));

        int currentYearTwoDigit = DateTime.Now.Year % 100;
        int century = (yy > currentYearTwoDigit + 10) ? 1900 : 2000;
        int fullYear = century + yy;

        try
        {
            return new DateTime(fullYear, mm, dd).ToString("dd/MM/yyyy");
        }
        catch
        {
            return "";
        }
    }
}