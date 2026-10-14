using System.Text.RegularExpressions;

namespace Sendway.Core;

// A phone number in E.164 form: "+", a country calling code, then the national number — 15 digits
// at most. The country code is split off by the ITU numbering plan, whose codes are prefix-free:
// 1 and 7 are one digit, the two-digit codes are listed below, every other code is three digits.
public sealed partial record PhoneNumber
{
    private static readonly HashSet<string> TwoDigitCountryCodes =
    [
        "20", "27", "30", "31", "32", "33", "34", "36", "39", "40", "41", "43", "44", "45", "46", "47",
        "48", "49", "51", "52", "53", "54", "55", "56", "57", "58", "60", "61", "62", "63", "64", "65",
        "66", "81", "82", "84", "86", "90", "91", "92", "93", "94", "95", "98"
    ];

    private PhoneNumber(string countryCode, string nationalNumber)
    {
        CountryCode = countryCode;
        NationalNumber = nationalNumber;
    }

    public string CountryCode { get; }

    // The number after the country code, without a trunk prefix (Korea's leading 0 is not part of it).
    public string NationalNumber { get; }

    public static PhoneNumber Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !E164().IsMatch(value))
        {
            throw new InvalidRecipientException(
                $"'{value}' is not a phone number in E.164 form (a '+', the country code and the number, digits only).");
        }

        var digits = value[1..];
        var length = digits[0] is '1' or '7' ? 1 : TwoDigitCountryCodes.Contains(digits[..2]) ? 2 : 3;
        return new PhoneNumber(digits[..length], digits[length..]);
    }

    public override string ToString() => $"+{CountryCode}{NationalNumber}";

    [GeneratedRegex(@"^\+[1-9][0-9]{6,14}$")]
    private static partial Regex E164();
}
