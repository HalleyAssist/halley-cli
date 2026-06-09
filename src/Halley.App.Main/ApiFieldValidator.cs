using PhoneNumbers;

namespace Halley.App.Main;

internal static class ApiFieldValidator
{
    private const string ExampleTimezone = "Australia/Melbourne";
    private const string ExamplePhoneNumber = "+61400000000";
    private static readonly PhoneNumberUtil SharedPhoneNumberUtil = PhoneNumberUtil.GetInstance();

    public static string? ValidateIanaTimezone(string? value)
    {
        var normalized = Normalize(value);
        if (normalized is null)
        {
            return null;
        }

        // This accepts known IANA identifiers while rejecting Windows timezone ids.
        return TimeZoneInfo.TryConvertIanaIdToWindowsId(normalized, out _)
            ? null
            : $"Expected a valid IANA timezone such as `{ExampleTimezone}`.";
    }

    public static string? ValidateInternationalPhoneNumber(string? value)
    {
        var normalized = Normalize(value);
        if (normalized is null)
        {
            return null;
        }

        if (!normalized.StartsWith('+'))
        {
            return $"Expected a valid international phone number such as `{ExamplePhoneNumber}`.";
        }

        return TryParseInternationalPhoneNumber(normalized, out _)
            ? null
            : $"Expected a valid international phone number such as `{ExamplePhoneNumber}`.";
    }

    public static string? ValidateContactPhoneNumber(string? value, string? country)
    {
        var normalized = Normalize(value);
        if (normalized is null)
        {
            return null;
        }

        if (normalized.StartsWith('+'))
        {
            return ValidateInternationalPhoneNumber(normalized);
        }

        var normalizedCountry = Normalize(country);
        if (normalizedCountry is null)
        {
            return $"Use an international phone number such as `{ExamplePhoneNumber}`, or supply `--country` when using a national-format number.";
        }

        return TryParseNationalPhoneNumber(normalized, normalizedCountry, out _)
            ? null
            : $"Expected a valid phone number. Use an international number such as `{ExamplePhoneNumber}`, or a national-format number that matches the supplied country.";
    }

    private static bool TryParseInternationalPhoneNumber(string value, out PhoneNumber number)
    {
        try
        {
            number = SharedPhoneNumberUtil.Parse(value, null);
            return SharedPhoneNumberUtil.IsValidNumber(number);
        }
        catch (NumberParseException)
        {
            number = new PhoneNumber();
            return false;
        }
    }

    private static bool TryParseNationalPhoneNumber(string value, string country, out PhoneNumber number)
    {
        try
        {
            number = SharedPhoneNumberUtil.Parse(value, country);
            return SharedPhoneNumberUtil.IsValidNumberForRegion(number, country);
        }
        catch (NumberParseException)
        {
            number = new PhoneNumber();
            return false;
        }
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
