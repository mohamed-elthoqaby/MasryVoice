using System.Globalization;
using System.Text.RegularExpressions;

namespace MasryVoice.Api.Common;

/// <summary>
/// Domain parser and normalizer for colloquial Egyptian Arabic date, time, and phone expressions.
/// </summary>
public static class EgyptianDateTimeParser
{
    private static readonly Regex EgyptianPhoneRegex = new(@"^01[0125]\d{8}$", RegexOptions.Compiled);

    /// <summary>
    /// Converts Arabic-Indic (Hindi) numerals (٠-٩) and Persian numerals to standard ASCII digits (0-9).
    /// </summary>
    public static string NormalizeDigits(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        var chars = input.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            if (c >= '\u0660' && c <= '\u0669') // Arabic-Indic digits ٠-٩
            {
                chars[i] = (char)('0' + (c - '\u0660'));
            }
            else if (c >= '\u06F0' && c <= '\u06F9') // Eastern Arabic-Indic digits
            {
                chars[i] = (char)('0' + (c - '\u06F0'));
            }
        }
        return new string(chars);
    }

    /// <summary>
    /// Normalizes and validates an Egyptian mobile phone number.
    /// Handles international prefixes (+20, 0020, 20), spaces, dashes, and Arabic numerals.
    /// Returns the standard 11-digit format (e.g., "01012345678") or null if invalid.
    /// </summary>
    public static string? NormalizeEgyptianPhone(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        var normalized = NormalizeDigits(input);
        // Strip non-digits
        var digits = new string(normalized.Where(char.IsDigit).ToArray());

        if (digits.Length == 0) return null;

        // Strip country code prefixes
        if (digits.StartsWith("0020"))
        {
            digits = "0" + digits[4..];
        }
        else if (digits.StartsWith("20") && digits.Length >= 12 && (digits[2] == '1'))
        {
            digits = "0" + digits[2..];
        }
        else if (digits.Length == 10 && digits.StartsWith("1") && (digits[1] == '0' || digits[1] == '1' || digits[1] == '2' || digits[1] == '5'))
        {
            digits = "0" + digits;
        }

        return EgyptianPhoneRegex.IsMatch(digits) ? digits : null;
    }

    /// <summary>
    /// Parses colloquial Egyptian relative date phrases, weekdays, and standard formats into a Cairo-local DateTime.
    /// </summary>
    public static bool TryParseEgyptianDate(string? input, DateTime referenceCairoDate, out DateTime targetDate)
    {
        targetDate = referenceCairoDate.Date;
        if (string.IsNullOrWhiteSpace(input)) return false;

        var text = NormalizeDigits(input).Trim().ToLowerInvariant();

        // 1. Day after tomorrow (بعد بكرة / بعد بكره) - check before tomorrow
        if (text.Contains("بعد بكره") || text.Contains("بعد بكرة") || text.Contains("بعد غد") || text.Contains("day after tomorrow"))
        {
            targetDate = referenceCairoDate.Date.AddDays(2);
            return true;
        }

        // 2. Tomorrow (بكرة / بكره / غدا / غداً)
        if (text.Contains("بكره") || text.Contains("بكرة") || text.Contains("غدا") || text.Contains("غداً") || text.Contains("tomorrow"))
        {
            targetDate = referenceCairoDate.Date.AddDays(1);
            return true;
        }

        // 3. Today (النهاردة / النهارده / اليوم)
        if (text.Contains("النهارده") || text.Contains("النهاردة") || text.Contains("اليوم") || text.Contains("today"))
        {
            targetDate = referenceCairoDate.Date;
            return true;
        }

        // 4. Weekdays in Egyptian dialect
        DayOfWeek? targetDay = null;
        if (text.Contains("السبت") || text.Contains("saturday"))
        {
            targetDay = DayOfWeek.Saturday;
        }
        else if (text.Contains("الحد") || text.Contains("الأحد") || text.Contains("الاحد") || text.Contains("sunday"))
        {
            targetDay = DayOfWeek.Sunday;
        }
        else if (text.Contains("الاتنين") || text.Contains("الإثنين") || text.Contains("الاثنين") || text.Contains("monday"))
        {
            targetDay = DayOfWeek.Monday;
        }
        else if (text.Contains("التلات") || text.Contains("الثلاثاء") || text.Contains("الثلاثا") || text.Contains("tuesday"))
        {
            targetDay = DayOfWeek.Tuesday;
        }
        else if (text.Contains("الاربع") || text.Contains("الأربعاء") || text.Contains("الاربعاء") || text.Contains("wednesday"))
        {
            targetDay = DayOfWeek.Wednesday;
        }
        else if (text.Contains("الخميس") || text.Contains("thursday"))
        {
            targetDay = DayOfWeek.Thursday;
        }
        else if (text.Contains("الجمعة") || text.Contains("الجمعه") || text.Contains("friday"))
        {
            targetDay = DayOfWeek.Friday;
        }

        if (targetDay.HasValue)
        {
            var refDay = referenceCairoDate.DayOfWeek;
            int daysToAdd = ((int)targetDay.Value - (int)refDay + 7) % 7;
            if (daysToAdd == 0)
            {
                // If requested today's day of week, usually refers to the upcoming week's day unless specified
                daysToAdd = 7;
            }
            targetDate = referenceCairoDate.Date.AddDays(daysToAdd);
            return true;
        }

        // 5. Standard ISO or culture formats (e.g., 2026-10-15 or 15/10/2026)
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedIso))
        {
            targetDate = parsedIso.Date;
            return true;
        }

        if (DateTime.TryParse(text, CultureInfo.GetCultureInfo("ar-EG"), DateTimeStyles.None, out var parsedEg))
        {
            targetDate = parsedEg.Date;
            return true;
        }

        return false;
    }
}
