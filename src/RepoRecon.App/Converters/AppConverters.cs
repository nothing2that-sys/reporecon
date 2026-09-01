using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Documents;

namespace RepoRecon.App.Converters;

/// <summary>bool -&gt; bool, inverted. Replaces the previous (non-functional) use of
/// BooleanToVisibilityConverter with ConverterParameter="invert" against IsEnabled.</summary>
public class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : value!;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : value!;
}

/// <summary>bool -&gt; Visibility, inverted (true -&gt; Collapsed). Replaces the previous
/// (non-functional) use of BooleanToVisibilityConverter with ConverterParameter="invert".</summary>
public class InvertedBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && !b ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility v && v != Visibility.Visible;
}

/// <summary>bool -&gt; string label, parameter format "trueText|falseText". Replaces the
/// previous (non-functional) use of BooleanToVisibilityConverter against Content.</summary>
public class BoolToLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var parts = (parameter as string)?.Split('|') ?? Array.Empty<string>();
        if (parts.Length != 2) return value?.ToString() ?? string.Empty;
        var isTrue = value is bool b && b;
        return isTrue ? parts[0] : parts[1];
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Front-truncates a long path/string ("...\Work\Projects\ProjectA") so the
/// identifying tail stays visible. §5-2: a trailing ellipsis hides which repo it is.
/// Shared by FrontEllipsisConverter (XAML bindings) and code-behind Text assignments.</summary>
public static class PathDisplay
{
    public const int DefaultMaxChars = 48;

    public static string Truncate(string? text, int maxChars = DefaultMaxChars)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (text.Length <= maxChars) return text;

        var tailLength = maxChars - 3; // reserve room for the leading "..."
        return tailLength <= 0 ? text : "..." + text[^tailLength..];
    }
}

/// <summary>Truncates a long path/string from the FRONT ("...\Work\Projects\ProjectA")
/// so the identifying tail stays visible. §5-2: trailing ellipsis hides which repo it is.
/// ConverterParameter overrides the default max length (characters).</summary>
public class FrontEllipsisConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var maxChars = PathDisplay.DefaultMaxChars;
        if (parameter is string p && int.TryParse(p, out var parsed) && parsed > 0)
        {
            maxChars = parsed;
        }

        return PathDisplay.Truncate(value as string, maxChars);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>§5-7: tooltip explaining what a state badge means, e.g. what "갈라짐" implies
/// for Pull/Push. Matches by substring since the bound text is a composed Korean display
/// string (§3.2.2), not a clean enum — returns null (no tooltip) when nothing matches so
/// self-explanatory badges (최신, plain ↓N/↑N counts) stay uncluttered.</summary>
public class StateExplanationConverter : IValueConverter
{
    private static readonly (string Match, string Explanation)[] Explanations =
    {
        ("갈라짐 · pull 먼저 필요", "로컬과 원격이 서로 다른 커밋을 갖고 있습니다. Pull/Push 모두 차단되며, 먼저 사람이 정리해야 합니다."),
        ("추적 브랜치 없음", "추적 브랜치가 설정되어 있지 않습니다. Pull/Push가 차단됩니다."),
        ("서버에서 사라진 브랜치", "원격 추적 브랜치가 서버에서 삭제되었습니다. 기본 브랜치로 전환이 필요할 수 있습니다."),
        ("브랜치 미체크아웃", "브랜치가 체크아웃되어 있지 않습니다. Pull/Push가 차단됩니다."),
        ("머지 충돌 중", "머지 충돌이 있습니다. Pull/Push가 차단되며 직접 해결해야 합니다."),
        ("카탈로그에 없음", "이 서버 프로파일의 호스트이지만 현재 카탈로그에서 찾을 수 없습니다(권한 변경 또는 삭제/아카이브 가능성)."),
        ("미등록 호스트", "등록된 서버 프로파일에 없는 호스트의 저장소입니다."),
        ("remote 없음", "remote가 없는 로컬 전용 저장소입니다."),
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string text || string.IsNullOrWhiteSpace(text)) return null;
        foreach (var (match, explanation) in Explanations)
        {
            if (text.Contains(match, StringComparison.OrdinalIgnoreCase)) return explanation;
        }
        return null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>bool -&gt; TextDecorationCollection. §3.2.2 (b): local_repos.is_missing
/// applies strikethrough to the path text alongside the "확인 안 됨" badge.</summary>
public class BoolToStrikethroughConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? TextDecorations.Strikethrough : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>string -&gt; Visibility, Visible when non-empty. §5.3.1: the left tree's
/// rollup badge only shows once a folder actually has a ↓/↑/⚠ to report.</summary>
public class StringNotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s && !string.IsNullOrEmpty(s) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
