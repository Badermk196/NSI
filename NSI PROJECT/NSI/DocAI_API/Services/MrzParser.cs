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

    // Common OCR confusions: letters that look like digits, and vice versa.
    // These get applied selectively depending on whether a position is expected
    // to be numeric (dates, check digits) or alphabetic (nationality, sex).
    private static readonly Dictionary<char, char> DigitLooksLikeLetter = new()
    {
        ['0'] = 'O',
        ['1'] = 'I',
        ['5'] = 'S',
        ['8'] = 'B',
        ['2'] = 'Z'
    };

    private static readonly Dictionary<char, char> LetterLooksLikeDigit = new()
    {
        ['O'] = '0',
        ['I'] = '1',
        ['L'] = '1',
        ['S'] = '5',
        ['B'] = '8',
        ['Z'] = '2'
    };

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
        // Strict pattern first (clean OCR), then a relaxed pattern that tolerates
        // digit/letter confusion in the nationality slot.
        var strictPattern = @"^[A-Z0-9<]{9}\d[A-Z]{3}\d{6}\d[MFX<]\d{6}\d";
        var relaxedPattern = @"^[A-Z0-9<]{9}[A-Z0-9][A-Z0-9]{3}[A-Z0-9]{6}[A-Z0-9][MFX<][A-Z0-9]{6}[A-Z0-9]";

        for (int i = 0; i < lines.Count - 1; i++)
        {
            var l1 = NormalizeMrzLine(lines[i]);
            var l2 = NormalizeMrzLine(lines[i + 1]);

            if (!l1.StartsWith("P<") && !l1.StartsWith("P<<") && !(l1.StartsWith("P") && l1.Length > 5 && l1[1] == '<'))
                continue;

            bool isStrict = Regex.IsMatch(l2, strictPattern);
            bool isRelaxed = !isStrict && l2.Length >= 28 && Regex.IsMatch(l2, relaxedPattern);

            if (!isStrict && !isRelaxed)
                continue;

            var docNum = l2.Substring(0, 9).Replace("<", "");
            var nationality = FixAlpha(l2.Substring(10, 3)).Replace("<", "");
            var dob = ParseMrzDate(FixDigits(l2.Substring(13, 6)));
            var expiry = ParseMrzDate(FixDigits(l2.Substring(21, 6)));
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
        // Strict: digits where digits are expected, [MFX<] for sex, letters for nationality.
        var strictPattern = new Regex(@"^\d{6}\d[MFX<]\d{6}\d[A-Z<]{3}");
        // Relaxed: tolerate any alphanumeric in digit AND nationality slots, since OCR
        // commonly swaps 0/O, 1/I/L, 5/S, 8/B, 2/Z in both directions.
        var relaxedPattern = new Regex(@"^[A-Z0-9]{6}[A-Z0-9][MFX<][A-Z0-9]{6}[A-Z0-9][A-Z0-9<]{3}");

        for (int i = 0; i < lines.Count; i++)
        {
            var candidate = NormalizeMrzLine(lines[i]);
            if (candidate.Length < 18) continue;

            bool isStrict = strictPattern.IsMatch(candidate);
            bool isRelaxed = !isStrict && relaxedPattern.IsMatch(candidate);

            if (!isStrict && !isRelaxed) continue;

            // TD1 line 2 layout: DOB(6) + checkdigit(1) + sex(1) + expiry(6) + checkdigit(1) + nationality(3) + ...
            // i.e. positions 0-5 DOB, 6 checkdigit, 7 sex, 8-13 expiry, 14 checkdigit, 15-17 nationality.
            var dob = ParseMrzDate(FixDigits(candidate.Substring(0, 6)));
            var expiry = candidate.Length >= 14
                ? ParseMrzDate(FixDigits(candidate.Substring(8, 6)))
                : "";
            var nationality = candidate.Length >= 18
                ? FixAlpha(candidate.Substring(15, 3)).Replace("<", "")
                : "";

            // Name line must contain "<<" AND consist only of letters/< — this avoids
            // false-matching the DOB/expiry/nationality line, which can contain "<<"
            // as incidental filler padding (e.g. "...J0R<<LKLK...").
            var nameLine = lines
                .Select(NormalizeMrzLine)
                .FirstOrDefault(l => l.Contains("<<") && Regex.IsMatch(l, @"^[A-Z<]+$"));
            var fullName = nameLine != null ? ParseNameField(nameLine) : "";

            var docNumLine = lines.FirstOrDefault(l => Regex.IsMatch(l.Trim(), @"^\d{7,10}$"));
            var docNumber = docNumLine?.Trim() ?? "";

            if (string.IsNullOrEmpty(fullName) && string.IsNullOrEmpty(docNumber)
                && string.IsNullOrEmpty(dob) && string.IsNullOrEmpty(nationality))
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

    // Converts digit-confused characters back to digits, for fields that should be numeric
    // (dates, check digits). E.g. "O" -> "0", "I"/"L" -> "1", "S" -> "5", "B" -> "8", "Z" -> "2".
    private static string FixDigits(string s)
    {
        var chars = s.Select(c => LetterLooksLikeDigit.TryGetValue(c, out var d) ? d : c);
        return new string(chars.ToArray());
    }

    // Converts letter-confused characters back to letters, for fields that should be alphabetic
    // (nationality code). E.g. "0" -> "O", "1" -> "I", "5" -> "S", "8" -> "B", "2" -> "Z".
    private static string FixAlpha(string s)
    {
        var chars = s.Select(c => DigitLooksLikeLetter.TryGetValue(c, out var l) ? l : c);
        return new string(chars.ToArray());
    }

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