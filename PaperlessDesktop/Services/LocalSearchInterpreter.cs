using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PaperlessDesktop.Models;

namespace PaperlessDesktop.Services;

internal static class LocalSearchInterpreter
{
    private const int MaximumQueryLength = 300;

    private static readonly Regex WordPattern = new(
        @"[\p{L}][\p{L}\p{M}]*|[0-9]+(?:[-./][0-9]+)*",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex NumericDatePattern = new(
        @"^(?:[0-9]{4}-[0-9]{1,2}-[0-9]{1,2}|[0-9]{1,2}[./][0-9]{1,2}[./][0-9]{2,4})$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly string[] DateFormats = ["yyyy-MM-dd", "d.M.yyyy"];

    private static readonly Dictionary<string, int> Months = IndexWords(
        "januar januara januaru january jan",
        "februar februara februaru february feb",
        "mart marta martu march mar",
        "april aprila aprilu travanj travnja apr",
        "maj maja maju svibanj svibnja may",
        "jun juni juna junu june lipanj lipnja",
        "jul juli jula julu july srpanj srpnja",
        "avgust avgusta avgustu august aug kolovoz kolovoza",
        "septembar septembra septembru september sep sept rujan rujna",
        "oktobar oktobra oktobru october oct listopad listopada",
        "novembar novembra novembru november nov studeni studenog",
        "decembar decembra decembru december dec prosinac prosinca");

    private static readonly Dictionary<string, int> NameAliases = IndexWords(
        "racun racuni racuna racunu racune racunom racunima invoice invoices bill bills",
        "ugovor ugovori ugovora ugovoru ugovore ugovorima contract contracts",
        "potvrda potvrde potvrdu potvrdom potvrdama certificate certificates",
        "priznanica priznanice priznanicu receipt receipts",
        "izvod izvodi izvoda izvode statement statements",
        "izvjestaj izvjestaji izvjestaja izvjestaje izvestaj izvestaji izvestaja izvestaje report reports",
        "pismo pisma pismu letter letters",
        "porez poreza porezi poreze tax taxes");

    private static readonly HashSet<string> DateIntroducers = new(
        ["iz", "u", "od", "in", "from", "for", "on", "dated", "date", "datum"]);

    private static readonly HashSet<string> UnresolvedTimeWords = new(
        ["nedavno", "skoro", "recent", "recently", "ytd", "prije", "pre", "before", "od", "nakon", "poslije", "posle",
         "after", "between", "izmedu", "since", "until", "through", "last", "this", "next", "prosli", "prosle",
         "sljedeci", "sledeci", "sljedece", "sledece", "mjesec", "mesec", "month", "year", "godina", "godine",
         "week", "weeks", "sedmica", "nedelja", "dana", "days", "date", "datum"]);

    private static readonly HashSet<string> SearchWords = new(
        ["nadi", "nadji", "pronadi", "pronadji", "prikazi", "potrazi", "pretrazi", "listaj", "pokazi", "find", "list",
         "search", "show", "get", "please", "molim", "mi", "me", "za", "for", "iz", "u", "in", "from", "on", "od",
         "s", "sa", "with", "the", "a", "and", "i", "sve", "svi", "all", "docs", "document", "documents",
         "dokument", "dokumenti", "dokumente"]);

    public static NaturalSearchInterpretation Interpret(NaturalSearchRequest request)
    {
        var query = request.Query.Trim();
        if (query.Length > MaximumQueryLength)
        {
            return new(new DocumentSearchCriteria(), NeedsClarification: true);
        }

        var words = Tokenize(query);
        var used = new bool[words.Length];
        var dates = ReadDates(words, used, request.Today);
        var entities = ReadNames(words, used, request.Metadata);

        bool unresolvedTime = dates.Count > 1 || dates.Any(date => date.Range is null);
        bool unrecognizedQualifier = false;
        for (int index = 0; index < words.Length; index++)
        {
            if (!used[index])
            {
                unresolvedTime |= UnresolvedTimeWords.Contains(words[index].Normalized);
                unrecognizedQualifier |= GetQualifier(words[index].Normalized) is not null;
            }
        }

        var date = !unresolvedTime && dates.Count == 1 ? dates[0].Range : null;
        var criteria = new DocumentSearchCriteria(
            ReadKeywords(query, words, used),
            entities.TagId,
            entities.CorrespondentId,
            entities.DocumentTypeId,
            date?.From,
            date?.To);
        return new(criteria, unresolvedTime || unrecognizedQualifier || entities.Ambiguous || criteria == new DocumentSearchCriteria());
    }

    private static List<DateMatch> ReadDates(Word[] words, bool[] used, DateOnly today)
    {
        var dates = new List<DateMatch>();
        for (int index = 0; index < words.Length; index++)
        {
            var match = ReadRelativeDate(words, index, today) ??
                ReadNumericDate(words, index) ??
                ReadMonth(words, index, today.Year) ??
                ReadYear(words, index);
            if (match is null)
            {
                continue;
            }

            dates.Add(match);
            Array.Fill(used, true, match.Start, match.Length);
            if (WordAt(words, match.Start - 1) is "dated" or "date" or "datum")
            {
                used[match.Start - 1] = true;
            }

            index = match.Start + match.Length - 1;
        }

        return dates;
    }

    private static DateMatch? ReadNumericDate(Word[] words, int index)
    {
        var value = words[index].Normalized;
        if (!NumericDatePattern.IsMatch(value))
        {
            return null;
        }

        // A date written with slashes can mean different things in each language.
        var range = DateOnly.TryParseExact(value, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? new DateRange(date, date)
            : null;
        return new DateMatch(index, 1, range);
    }

    private static DateMatch? ReadMonth(Word[] words, int index, int defaultYear)
    {
        var value = words[index].Normalized;
        if (!Months.TryGetValue(value, out int month))
        {
            return null;
        }

        var before = WordAt(words, index - 1);
        var after = WordAt(words, index + 1);
        int? beforeNumber = ParseNumber(before);
        int? afterNumber = ParseNumber(after);
        if (value == "may" && (index == 0 || after == "i") && afterNumber is null && beforeNumber is null)
        {
            return null;
        }

        int start = index;
        int end = index;
        int? day = null;
        int year = defaultYear;
        if (beforeNumber is int leadingNumber)
        {
            start--;
            if (before.Length == 4)
            {
                year = leadingNumber;
            }
            else
            {
                day = leadingNumber;
            }
        }

        if (afterNumber is int trailingNumber)
        {
            end++;
            if (after.Length >= 4)
            {
                year = trailingNumber;
            }
            else if (day is null && start == index)
            {
                day = trailingNumber;
                var followingYear = WordAt(words, end + 1);
                if (followingYear.Length >= 4 && ParseNumber(followingYear) is int explicitYear)
                {
                    year = explicitYear;
                    end++;
                }
            }
            else
            {
                return new DateMatch(start, end - start + 1, null);
            }
        }

        if (IsYearWord(WordAt(words, end + 1)))
        {
            end++;
        }

        return new DateMatch(start, end - start + 1, TryBuildRange(() =>
        {
            var first = new DateOnly(year, month, day ?? 1);
            return new DateRange(first, day is null ? MonthEnd(first) : first);
        }));
    }

    private static DateMatch? ReadYear(Word[] words, int index)
    {
        var word = words[index].Normalized;
        var next = WordAt(words, index + 1);
        bool hasYearWord = IsYearWord(next);
        if (word.Length != 4 || ParseNumber(word) is not int year || Months.ContainsKey(next) ||
            (!hasYearWord && !DateIntroducers.Contains(WordAt(words, index - 1))))
        {
            return null;
        }

        return new DateMatch(index, hasYearWord ? 2 : 1, TryBuildRange(() =>
            new DateRange(new DateOnly(year, 1, 1), new DateOnly(year, 12, 31))));
    }

    private static DateMatch? ReadRelativeDate(Word[] words, int index, DateOnly today)
    {
        var word = words[index].Normalized;
        int? dayOffset = word switch
        {
            "danas" or "today" => 0,
            "juce" or "jucer" or "yesterday" => -1,
            "prekjuce" or "prekjucer" => -2,
            "sutra" or "tomorrow" => 1,
            _ => null,
        };
        if (dayOffset is int offset)
        {
            return new DateMatch(index, 1, TryBuildRange(() =>
            {
                var date = today.AddDays(offset);
                return new DateRange(date, date);
            }));
        }

        var next = WordAt(words, index + 1);
        if ((word is "last" or "posljednjih" or "poslednjih" or "proteklih" or "zadnjih") &&
            ParseNumber(next) is int days && (WordAt(words, index + 2) is "days" or "day" or "dana" or "dan"))
        {
            return new DateMatch(index, 3, days > 0
                ? TryBuildRange(() => new DateRange(today.AddDays(1 - days), today))
                : null);
        }

        var period = (word + " " + next) switch
        {
            "this week" or "ove sedmice" or "ove nedelje" or "ove nedjelje" => ("week", 0),
            "last week" or "prosle sedmice" or "prosle nedelje" or "prosle nedjelje" => ("week", -1),
            "this month" or "ovog mjeseca" or "ovog meseca" => ("month", 0),
            "last month" or "proslog mjeseca" or "proslog meseca" => ("month", -1),
            "this year" or "ove godine" => ("year", 0),
            "last year" or "prosle godine" => ("year", -1),
            _ => (string.Empty, 0),
        };
        if (period.Item1.Length == 0)
        {
            return null;
        }

        return new DateMatch(index, 2, TryBuildRange(() => RelativePeriod(today, period.Item1, period.Item2)));
    }

    private static DateRange RelativePeriod(DateOnly today, string period, int offset)
    {
        switch (period)
        {
            case "week":
                int daysFromMonday = ((int)today.DayOfWeek + 6) % 7;
                var monday = today.AddDays(-daysFromMonday + offset * 7);
                return new DateRange(monday, monday.AddDays(6));
            case "month":
                var first = new DateOnly(today.Year, today.Month, 1).AddMonths(offset);
                return new DateRange(first, MonthEnd(first));
            default:
                int year = today.Year + offset;
                return new DateRange(new DateOnly(year, 1, 1), new DateOnly(year, 12, 31));
        }
    }

    private static DateRange? TryBuildRange(Func<DateRange> create)
    {
        try
        {
            return create();
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static DateOnly MonthEnd(DateOnly date) =>
        new(date.Year, date.Month, DateTime.DaysInMonth(date.Year, date.Month));

    private static EntitySelection ReadNames(Word[] words, bool[] used, PaperlessDocumentMetadata? metadata)
    {
        if (metadata is null)
        {
            return new(null, null, null, false);
        }

        var names = ReadNames(metadata.Tags, EntityKind.Tag)
            .Concat(ReadNames(metadata.DocumentTypes, EntityKind.Type))
            .Concat(ReadNames(metadata.Correspondents, EntityKind.Correspondent))
            .ToArray();
        var matches = new List<EntityMatch>();
        bool ambiguous = false;
        for (int index = 0; index < words.Length; index++)
        {
            if (used[index] || GetQualifier(words[index].Normalized) is not null)
            {
                continue;
            }

            var qualifier = GetQualifier(WordAt(words, index - 1));
            var possibilities = names.Where(name => qualifier is null || name.Kind == qualifier)
                .Select(name => new EntityMatch(name, index, ScoreName(words, used, index, name.Words)))
                .Where(match => match.Score > 0)
                .ToArray();
            if (possibilities.Length == 0)
            {
                continue;
            }

            int length = possibilities.Max(match => match.Name.Words.Length);
            var longest = possibilities.Where(match => match.Name.Words.Length == length).ToArray();
            int bestScore = longest.Max(match => match.Score);
            var preferred = longest.Where(match => match.Score == bestScore).ToArray();
            if (preferred.Length == 1)
            {
                matches.Add(preferred[0]);
            }
            else
            {
                ambiguous = true;
            }

            index += length - 1;
        }

        var ids = new Dictionary<EntityKind, int>();
        foreach (var group in matches.GroupBy(match => match.Name.Kind))
        {
            var distinctIds = group.Select(match => match.Name.Id).Distinct().ToArray();
            if (distinctIds.Length != 1)
            {
                ambiguous = true;
                continue;
            }

            ids[group.Key] = distinctIds[0];
            foreach (var match in group)
            {
                Array.Fill(used, true, match.Start, match.Name.Words.Length);
                if (GetQualifier(WordAt(words, match.Start - 1)) == group.Key)
                {
                    used[match.Start - 1] = true;
                }
            }
        }

        int? Id(EntityKind kind) => ids.TryGetValue(kind, out int id) ? id : null;
        return new(Id(EntityKind.Tag), Id(EntityKind.Type), Id(EntityKind.Correspondent), ambiguous);
    }

    private static IEnumerable<KnownName> ReadNames(IReadOnlyDictionary<int, string> source, EntityKind kind) =>
        source.Where(pair => pair.Key > 0 && !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => new KnownName(kind, pair.Key, Tokenize(pair.Value).Select(word => word.Normalized).ToArray()))
            .Where(name => name.Words.Length > 0);

    private static int ScoreName(Word[] words, bool[] used, int start, string[] name)
    {
        if (start + name.Length > words.Length)
        {
            return 0;
        }

        int score = 2;
        for (int offset = 0; offset < name.Length; offset++)
        {
            int index = start + offset;
            var word = words[index].Normalized;
            if (used[index])
            {
                return 0;
            }

            if (word == name[offset])
            {
                continue;
            }

            if (!NameAliases.TryGetValue(word, out int group) ||
                !NameAliases.TryGetValue(name[offset], out int nameGroup) || group != nameGroup)
            {
                return 0;
            }

            score = 1;
        }

        return score;
    }

    private static string? ReadKeywords(string query, Word[] words, bool[] used)
    {
        var parts = new List<string>();
        int first = -1;
        for (int index = 0; index <= words.Length; index++)
        {
            if (index < words.Length && !used[index] &&
                !SearchWords.Contains(words[index].Normalized) && GetQualifier(words[index].Normalized) is null)
            {
                if (first < 0)
                {
                    first = index;
                }
            }
            else if (first >= 0)
            {
                parts.Add(query[words[first].Start..words[index - 1].End]);
                first = -1;
            }
        }

        return parts.Count == 0 ? null : string.Join(' ', parts);
    }

    private static EntityKind? GetQualifier(string word) => word switch
    {
        "tag" or "tagom" or "tagu" or "tags" or "oznaka" or "oznaku" or "oznakom" => EntityKind.Tag,
        "tip" or "tipa" or "tipom" or "type" or "vrsta" or "vrste" or "vrstu" => EntityKind.Type,
        "korespondent" or "korespondenta" or "korespondentom" or "correspondent" or "sender" or "posiljalac" or "posiljaoca" =>
            EntityKind.Correspondent,
        _ => null,
    };

    private static Word[] Tokenize(string value) => WordPattern.Matches(value)
        .Select(match => new Word(Normalize(match.Value), match.Index, match.Index + match.Length))
        .ToArray();

    private static string Normalize(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (char character in value.ToLowerInvariant().Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                result.Append(character == 'đ' ? 'd' : character);
            }
        }

        return result.ToString();
    }

    private static string WordAt(Word[] words, int index) =>
        index >= 0 && index < words.Length ? words[index].Normalized : string.Empty;

    private static int? ParseNumber(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) ? number : null;

    private static bool IsYearWord(string word) => word is "godina" or "godine" or "godini" or "year";

    private static Dictionary<string, int> IndexWords(params string[] groups) =>
        groups.SelectMany((words, index) => words.Split(' ').Select(word => (word, index)))
            .ToDictionary(group => group.word, group => group.index + 1);

    private sealed record Word(string Normalized, int Start, int End);
    private sealed record DateRange(DateOnly From, DateOnly To);
    private sealed record DateMatch(int Start, int Length, DateRange? Range);
    private sealed record KnownName(EntityKind Kind, int Id, string[] Words);
    private sealed record EntityMatch(KnownName Name, int Start, int Score);
    private sealed record EntitySelection(int? TagId, int? DocumentTypeId, int? CorrespondentId, bool Ambiguous);

    private enum EntityKind
    {
        Tag,
        Type,
        Correspondent,
    }
}
