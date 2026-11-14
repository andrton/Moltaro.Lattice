using System.Globalization;

namespace Moltaro.Lattice.Prototypes.StringEncodingComparison;

internal static class TextCorpus
{
    public static string[] Create(string family, int count)
    {
        var prefix = family switch
        {
            "ascii" => "configuration:feature:enabled:",
            "cyrillic" => "Настройка:параметр:включён:",
            "cjk" => "設定項目分類識別名稱狀態資訊:",
            _ => throw new ArgumentException("Corpus must be ascii, cyrillic or cjk.", nameof(family))
        };

        var values = new string[count];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = prefix + index.ToString("D5", CultureInfo.InvariantCulture);
        }
        return values;
    }
}
