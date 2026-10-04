using System.Globalization;

namespace Source2.Compiler;

/// <summary>
/// tier0's string-to-number conversions, as the entity lump writer calls them.
///
/// <para>They are not interchangeable and the lump shows the difference. V_atoi
/// reads the longest prefix it can, so "0.600000" is 0 and "12abc" is 12. The
/// V_StringTo* family is strict: resourcecompiler passes a default of 0 and no
/// error flags, so anything that does not parse to the end is that 0. The float
/// readers use from_chars on a double and narrow it, after skipping blanks and
/// one leading '+'.</para>
/// </summary>
internal static class CNumbers
{
    /// <summary>V_atoi: spaces and tabs, a sign, then "0x" hex, a quoted
    /// character, or decimal digits, stopping at the first that is not one.</summary>
    public static long Atoi(string text)
    {
        var i = 0;
        while (i < text.Length && text[i] is ' ' or '\t')
            i++;
        long sign = 1;
        if (i < text.Length && text[i] is '-' or '+')
            sign = text[i++] == '-' ? -1 : 1;

        long value = 0;
        if (i + 1 < text.Length && text[i] == '0' && text[i + 1] is 'x' or 'X')
        {
            for (i += 2; i < text.Length && Uri.IsHexDigit(text[i]); i++)
                value = value * 16 + Convert.ToInt32(text[i].ToString(), 16);
            return value * sign;
        }
        if (i + 1 < text.Length && text[i] == '\'')
            return text[i + 1] * sign;
        for (; i < text.Length && text[i] is >= '0' and <= '9'; i++)
            value = value * 10 + (text[i] - '0');
        return value * sign;
    }

    /// <summary>V_atofloat32: atof, the longest prefix that reads as a number
    /// after blanks, and 0 when none does.</summary>
    public static float Atof(string text)
    {
        var at = SkipBlanks(text, 0);
        if (at < text.Length && text[at] == '+')
            at++;
        return FromChars(text, at, out _) is { } value ? (float)value : 0f;
    }

    /// <summary>V_StringToInt32: strtoll to the end of the string, 0 otherwise or
    /// when it leaves 32 bits.</summary>
    public static int ToInt32(string text)
        => StrToL(text, out var value) && value is >= int.MinValue and <= int.MaxValue ? (int)value : 0;

    /// <summary>V_StringToUint32: strtoull to the end of the string, 0 otherwise or
    /// when it leaves 32 bits. A minus sign wraps, as strtoull's does, and so
    /// leaves them.</summary>
    public static uint ToUInt32(string text)
        => StrToL(text, out var value) && value is >= 0 and <= uint.MaxValue ? (uint)value : 0;

    /// <summary>V_StringToFloat32: blanks, one '+', then a double that has to run
    /// to the end of the string, narrowed to float. 0 for anything else.</summary>
    public static float ToFloat32(string text)
    {
        var at = SkipBlanks(text, 0);
        if (at < text.Length && text[at] == '+')
            at++;
        return FromChars(text, at, out var end) is { } value && end == text.Length ? (float)value : 0f;
    }

    /// <summary>V_StringToFloatArray: up to <paramref name="count"/> doubles, each
    /// after blanks and an optional '+', narrowed to float; the first that does not
    /// parse ends the array and the rest are zero.</summary>
    public static float[] FloatArray(string text, int count)
    {
        var values = new float[count];
        var at = 0;
        for (var i = 0; i < count; i++)
        {
            at = SkipBlanks(text, at);
            if (at >= text.Length)
                break;
            if (text[at] == '+')
                at++;
            if (FromChars(text, at, out var end) is not { } value)
                break;
            values[i] = (float)value;
            at = end;
        }
        return values;
    }

    /// <summary>sscanf "%f %f ...": floats read straight to float, as many as match,
    /// the rest left at the zero the caller initialised.</summary>
    public static float[] Scan(string text, int count)
    {
        var values = new float[count];
        var at = 0;
        for (var i = 0; i < count; i++)
        {
            at = SkipBlanks(text, at);
            var start = at;
            if (at < text.Length && text[at] is '+' or '-')
                at++;
            if (FromChars(text, at, out var end) is null)
                break;
            values[i] = float.Parse(text.AsSpan(start, end - start), NumberStyles.Float, CultureInfo.InvariantCulture);
            at = end;
        }
        return values;
    }

    /// <summary>
    /// V_StringToColor: six or eight hex digits with no blanks, or three or four
    /// decimal channels each 0 to 255 and nothing after them. Anything else is
    /// black with a full alpha; so is a trailing blank, which the channel loop
    /// reads as a fifth value that is not a number.
    /// </summary>
    public static (byte R, byte G, byte B, byte A) ToColor(string text)
    {
        (byte, byte, byte, byte) black = (0, 0, 0, 255);
        if (text.Length is 6 or 8 && !text.Any(c => c is '\t' or '\n' or '\v' or '\f' or '\r' or ' '))
        {
            if (!uint.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex))
                return black;
            return text.Length == 6
                ? ((byte)(hex >> 16), (byte)(hex >> 8), (byte)hex, (byte)255)
                : ((byte)(hex >> 24), (byte)(hex >> 16), (byte)(hex >> 8), (byte)hex);
        }

        var channels = new List<long>();
        var at = 0;
        while (at < text.Length && channels.Count < 4)
        {
            if (!StrToLPrefix(text, at, out var value, out var end) || value is < int.MinValue or > int.MaxValue)
                return black;
            channels.Add(value);
            at = end;
        }
        if (channels.Count < 3 || at < text.Length || channels.Any(c => (uint)c > 255))
            return black;
        return ((byte)channels[0], (byte)channels[1], (byte)channels[2], channels.Count == 4 ? (byte)channels[3] : (byte)255);
    }

    private static bool StrToL(string text, out long value)
    {
        value = 0;
        return text.Length > 0 && StrToLPrefix(text, 0, out value, out var end) && end == text.Length;
    }

    /// <summary>strtoll in base 10 from <paramref name="at"/>: blanks, a sign,
    /// digits. False when no digit was read.</summary>
    private static bool StrToLPrefix(string text, int at, out long value, out int end)
    {
        value = 0;
        var i = SkipBlanks(text, at);
        var negative = false;
        if (i < text.Length && text[i] is '-' or '+')
            negative = text[i++] == '-';
        var digits = i;
        for (; i < text.Length && text[i] is >= '0' and <= '9'; i++)
            value = unchecked(value * 10 + (text[i] - '0'));
        end = i == digits ? at : i;
        if (negative)
            value = -value;
        return i > digits;
    }

    private static int SkipBlanks(string text, int at)
    {
        while (at < text.Length && text[at] is '\t' or '\n' or '\v' or '\f' or '\r' or ' ')
            at++;
        return at;
    }

    /// <summary>
    /// from_chars on a double in general format: a sign, digits with an optional
    /// point, an optional exponent, or inf and nan. Null when nothing parses.
    /// </summary>
    private static double? FromChars(string text, int at, out int end)
    {
        end = at;
        var i = at;
        if (i < text.Length && text[i] == '-')
            i++;
        foreach (var word in new[] { "infinity", "inf", "nan" })
            if (string.Compare(text, i, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) == 0)
            {
                end = i + word.Length;
                var negative = text[at] == '-';
                return word == "nan" ? double.NaN : negative ? double.NegativeInfinity : double.PositiveInfinity;
            }

        var mantissa = i;
        while (i < text.Length && char.IsAsciiDigit(text[i]))
            i++;
        if (i < text.Length && text[i] == '.')
            for (i++; i < text.Length && char.IsAsciiDigit(text[i]); i++) { }
        if (i == mantissa || (i == mantissa + 1 && text[mantissa] == '.'))
            return null;
        if (i < text.Length && text[i] is 'e' or 'E')
        {
            var e = i + 1;
            if (e < text.Length && text[e] is '+' or '-')
                e++;
            if (e < text.Length && char.IsAsciiDigit(text[e]))
            {
                for (i = e; i < text.Length && char.IsAsciiDigit(text[i]); i++) { }
            }
        }
        end = i;
        return double.Parse(text.AsSpan(at, i - at), NumberStyles.Float, CultureInfo.InvariantCulture);
    }
    /// <summary>
    /// MSVC's printf "%g" (precision 6): the value rounded to six significant
    /// digits, in exponent form ("1.23457e+07") when the decimal exponent is
    /// under -4 or at least 6, else fixed; trailing zeros and a bare point
    /// dropped. inf and nan as the UCRT writes them.
    /// </summary>
    public static string FormatG(double value)
    {
        if (double.IsNaN(value))
            return double.IsNegative(value) ? "-nan(ind)" : "nan";
        if (double.IsInfinity(value))
            return value < 0 ? "-inf" : "inf";
        if (value == 0)
            return double.IsNegative(value) ? "-0" : "0";
        // E5 rounds to six significant digits; its exponent decides the form.
        var e = value.ToString("E5", CultureInfo.InvariantCulture);
        var exponent = int.Parse(e[(e.IndexOf('E') + 1)..], CultureInfo.InvariantCulture);
        if (exponent < -4 || exponent >= 6)
        {
            var mantissa = e[..e.IndexOf('E')];
            if (mantissa.Contains('.'))
                mantissa = mantissa.TrimEnd('0').TrimEnd('.');
            return $"{mantissa}e{(exponent < 0 ? '-' : '+')}{Math.Abs(exponent):00}";
        }
        var text = value.ToString("F" + (5 - exponent).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        return text.Contains('.') ? text.TrimEnd('0').TrimEnd('.') : text;
    }
}
