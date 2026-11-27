using System.Globalization;
using System.Text.Json;

namespace SmartResourceDispatcher.Core;

internal static class UiText
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> english = new(() =>
    {
        using var stream = typeof(UiText).Assembly.GetManifestResourceStream("CoreShift.English.json")
            ?? throw new InvalidOperationException("The English interface catalog is missing.");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidOperationException("The English interface catalog is empty.");
    });

    internal static bool IsEnglish { get; set; }

    internal static string Get(string russian) =>
        IsEnglish && english.Value.TryGetValue(russian, out var translation) ? translation : russian;

    internal static string Format(FormattableString text) =>
        string.Format(CultureInfo.CurrentCulture, Get(text.Format), text.GetArguments());

    internal static IEnumerable<string> Keys => english.Value.Keys;
}
