using System.Text.RegularExpressions;

namespace Dmart.Auth;

// Mirrors dmart/backend/utils/regex.py::PASSWORD. Accepts 8-64 chars, at least
// one digit (ASCII or Arabic-Indic), at least one uppercase letter (Latin or
// Arabic range), and the curated set of specials Python allows.
public static partial class PasswordRules
{
    public const string Pattern =
        "^(?=.*[0-9\u0660-\u0669])(?=.*[A-Z\u0621-\u064a])" +
        "[a-zA-Z\u0621-\u064a0-9\u0660-\u0669 _#@%*!?$^&()+={}\\[\\]~|;:,.<>/-]{8,64}$";

    [GeneratedRegex(Pattern, RegexOptions.CultureInvariant)]
    private static partial Regex PasswordRegex();

    public static bool IsValid(string? password) =>
        !string.IsNullOrEmpty(password) && PasswordRegex().IsMatch(password);
}
