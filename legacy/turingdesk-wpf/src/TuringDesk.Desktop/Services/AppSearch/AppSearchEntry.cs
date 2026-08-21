using System.IO;
using PinyinNet;

namespace TuringDesk.Desktop.Services.AppSearch;

internal sealed record AppSearchEntry(
    string Name,
    string Target,
    string Category,
    string Source,
    IReadOnlyList<string> Aliases)
{
    public static AppSearchEntry Create(DiscoveredProgram program)
    {
        var aliases = new HashSet<string>(StringComparer.Ordinal);

        AddAlias(aliases, program.Name);
        AddAlias(aliases, BuildWordInitials(program.Name));

        if (!string.IsNullOrWhiteSpace(program.Target) && !program.Target.StartsWith("aumid:", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var fileName = Path.GetFileNameWithoutExtension(program.Target);
                AddAlias(aliases, fileName);
            }
            catch { }
        }

        try
        {
            AddAlias(aliases, PinyinConvert.GetPinyin(program.Name));
            AddAlias(aliases, PinyinConvert.GetPinyinFirstLetter(program.Name));
        }
        catch
        {
            // Special shell display names can contain characters not supported by
            // the pinyin library. Native/fuzzy aliases still remain available.
        }

        foreach (var alias in program.AlternateNames)
            AddAlias(aliases, alias);

        return new AppSearchEntry(
            program.Name,
            program.Target,
            program.Category,
            program.Source,
            aliases.Where(alias => alias.Length > 0).ToArray());
    }

    private static void AddAlias(HashSet<string> aliases, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var compact = AppSearchMatcher.NormalizeCompact(value);
        if (compact.Length > 0) aliases.Add(compact);
    }

    private static string BuildWordInitials(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var initials = new List<char>();
        var previousWasSeparator = true;
        var previousWasLower = false;

        foreach (var character in value)
        {
            if (!char.IsLetterOrDigit(character))
            {
                previousWasSeparator = true;
                previousWasLower = false;
                continue;
            }

            if (previousWasSeparator || (char.IsUpper(character) && previousWasLower) || char.IsDigit(character))
                initials.Add(char.ToLowerInvariant(character));

            previousWasSeparator = false;
            previousWasLower = char.IsLower(character);
        }

        return new string(initials.ToArray());
    }
}
