using System.Globalization;
using System.Text.RegularExpressions;

namespace VehicleVision.PleasanterTools.MailIngest.Core.Mapping;

/// <summary>1 件の対応づけ定義（設定 JSON の <c>mappings</c> の 1 要素）。</summary>
public sealed record MappingRule(string Source, string Target, string? Default = null, string? Join = null);

/// <summary>対応づけの検査・評価で見つかった問題。メールの内容（件名・本文など）は載せない。</summary>
public sealed record MappingIssue(string Location, string Code, string Message);

/// <summary>対応づけの評価の入力条件。</summary>
public sealed class MappingContext
{
    /// <summary>日時列へ変換するときのタイムゾーン（接続設定の <c>timeZone</c>）。</summary>
    public required TimeZoneInfo TimeZone { get; init; }

    /// <summary>
    /// 列ごとの最大文字数（GetSite で確認したサイト側の長さ）。未指定の列は検査しない。
    /// 超過したら切り捨てず、メール全体を失敗にする。
    /// </summary>
    public IReadOnlyDictionary<string, int>? MaxLengths { get; init; }

    /// <summary>
    /// 日時の API 向け文字列。<b>Pleasanter が受け付ける書式は実機未確認</b>のため差し替えられる。
    /// 既定はタイムゾーン変換後の <c>yyyy-MM-ddTHH:mm:ss</c>（ISO 8601、オフセットなし）。
    /// </summary>
    public Func<DateTime, string> DateTimeFormatter { get; init; } = DefaultDateTimeFormatter;

    internal static string DefaultDateTimeFormatter(DateTime local) =>
        local.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
}

/// <summary>対応づけの評価結果。1 列でも失敗したらメール全体が失敗で、一部の列だけ送らない。</summary>
public sealed class MappedRecord
{
    internal MappedRecord(
        IReadOnlyDictionary<string, object>? values,
        IReadOnlyList<MappingIssue> errors)
    {
        Values = values;
        Errors = errors;
    }

    /// <summary>成功時の列名 → 型付き正規値（string / long / decimal / bool / UTC の DateTimeOffset）。</summary>
    public IReadOnlyDictionary<string, object>? Values { get; }

    public IReadOnlyList<MappingIssue> Errors { get; }

    public bool IsSuccess => Values is not null;

    /// <summary>
    /// 標準 API（Create / Update）のリクエスト断片を組み立てる。<c>ApiKey</c>・<c>ApiVersion</c> は含めない
    /// （クライアントが付ける）。SiteId・Id・権限は入力から作れない。
    /// </summary>
    /// <param name="context">日時の変換に使う条件。</param>
    /// <param name="reserved">予約列（外部キー・投射ハッシュ）。利用者の対応づけとは独立に追加する。</param>
    public IReadOnlyDictionary<string, object?> ToApiRecord(
        MappingContext context,
        IReadOnlyDictionary<string, string>? reserved = null)
    {
        if (Values is null)
        {
            throw new InvalidOperationException("失敗した対応づけから API レコードは作れない。");
        }

        var record = new Dictionary<string, object?>(StringComparer.Ordinal);
        var hashes = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);

        void Put(string column, object value)
        {
            var info = MappingPlan.ColumnInfo.Parse(column)!;
            if (info.HashName is null)
            {
                record[column] = value;
                return;
            }

            if (!hashes.TryGetValue(info.HashName, out var hash))
            {
                hash = new Dictionary<string, object?>(StringComparer.Ordinal);
                hashes[info.HashName] = hash;
            }

            hash[column] = value is DateTimeOffset utc
                ? context.DateTimeFormatter(TimeZoneInfo.ConvertTime(utc, context.TimeZone).DateTime)
                : value;
        }

        foreach (var (column, value) in Values)
        {
            Put(column, value);
        }

        if (reserved is not null)
        {
            foreach (var (column, value) in reserved)
            {
                Put(column, value);
            }
        }

        foreach (var (name, hash) in hashes)
        {
            record[name] = hash;
        }

        return record;
    }
}

/// <summary>検査済みの対応づけ。<see cref="Compile"/> で作り、<see cref="Evaluate"/> で評価する。</summary>
/// <remarks>
/// <para>テンプレート式・スクリプト・任意関数は持たない。宣言した項目参照・配列の連結・既定値・固定値・
/// 列の型変換だけ。設計文書 <c>メール対応づけ設定.md</c> 第 3 節の推奨案であり、確定仕様ではない。</para>
/// <para>本文の表示記法の無害化（Markdown のリテラル化）はここでは行わない。入力の <c>BodyText</c> は
/// 上流で「安全な表示テキスト」へ変換済みであること（本文の扱いは未決事項）。</para>
/// </remarks>
public sealed partial class MappingPlan
{
    private readonly List<Compiled> _mappings;
    private readonly List<(string Column, object Value)> _fixed;

    private MappingPlan(List<Compiled> mappings, List<(string, object)> fixedValues)
    {
        _mappings = mappings;
        _fixed = fixedValues;
    }

    /// <summary>検査して計画を作る。起動時に呼び、エラーがあれば停止する。</summary>
    /// <param name="rules">対応づけ。</param>
    /// <param name="fixedValues">固定値。入力に関係なく設定値を使う。</param>
    /// <param name="reservedColumns">予約列（外部キー・投射ハッシュ）。利用者の指定を禁止する。</param>
    public static (MappingPlan? Plan, IReadOnlyList<MappingIssue> Errors) Compile(
        IReadOnlyList<MappingRule> rules,
        IReadOnlyDictionary<string, object>? fixedValues,
        IReadOnlyCollection<string> reservedColumns)
    {
        var errors = new List<MappingIssue>();
        var compiled = new List<Compiled>();
        var used = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            var location = $"mappings[{i}]";
            var before = errors.Count;

            if (!SourceFields.TryGetValue(rule.Source, out var field))
            {
                errors.Add(new MappingIssue(location, "UnknownSource", "未知の入力項目。"));
            }

            var column = ColumnInfo.Parse(rule.Target);
            if (column is null)
            {
                errors.Add(new MappingIssue(location, "UnknownTarget", "未知・対応外の出力列。"));
            }
            else if (reservedColumns.Contains(rule.Target, StringComparer.Ordinal))
            {
                errors.Add(new MappingIssue(location, "ReservedColumn", "予約列（外部キー・投射ハッシュ）は指定できない。"));
            }
            else if (column.FixedOnly)
            {
                errors.Add(new MappingIssue(location, "FixedValueOnly", "この列は固定値だけ指定できる。"));
            }
            else if (!used.Add(rule.Target))
            {
                errors.Add(new MappingIssue(location, "DuplicateTarget", "同じ出力列が複数指定されている。"));
            }

            if (field is not null)
            {
                if (field.Kind == ValueKind.StringArray && rule.Join is null)
                {
                    errors.Add(new MappingIssue(location, "JoinRequired", "配列の入力には join が必要。"));
                }
                else if (field.Kind != ValueKind.StringArray && rule.Join is not null)
                {
                    errors.Add(new MappingIssue(location, "JoinNotAllowed", "join は配列にだけ指定できる。"));
                }

                if (column is { FixedOnly: false } && !IsCompatible(field.Kind, column.Type))
                {
                    errors.Add(new MappingIssue(location, "TypeMismatch", "入力項目の型と出力列の型が合わない。"));
                }
            }

            object? defaultValue = null;
            if (rule.Default is not null && column is not null && errors.Count == before)
            {
                if (!TryConvert(rule.Default, column, out defaultValue))
                {
                    errors.Add(new MappingIssue(location, "InvalidDefault", "既定値を出力列の型へ変換できない。"));
                }
            }

            if (errors.Count == before && field is not null && column is not null)
            {
                compiled.Add(new Compiled(location, field, column, rule.Join, defaultValue));
            }
        }

        var fixedList = new List<(string, object)>();
        if (fixedValues is not null)
        {
            foreach (var (name, raw) in fixedValues.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var location = $"fixedValues.{name}";
                var column = ColumnInfo.Parse(name);
                if (column is null)
                {
                    errors.Add(new MappingIssue(location, "UnknownTarget", "未知・対応外の出力列。"));
                }
                else if (reservedColumns.Contains(name, StringComparer.Ordinal))
                {
                    errors.Add(new MappingIssue(location, "ReservedColumn", "予約列は指定できない。"));
                }
                else if (used.Contains(name))
                {
                    errors.Add(new MappingIssue(location, "FixedValueConflict", "対応づけと固定値で同じ列を指定できない。"));
                }
                else if (!TryConvertFixed(raw, column, out var value))
                {
                    errors.Add(new MappingIssue(location, "InvalidFixedValue", "固定値を出力列の型へ変換できない。"));
                }
                else
                {
                    fixedList.Add((name, value));
                }
            }
        }

        return errors.Count == 0
            ? (new MappingPlan(compiled, fixedList), errors)
            : (null, errors);
    }

    /// <summary>メールを評価して列値を得る。1 列でも変換できなければ全体を失敗にする。</summary>
    public MappedRecord Evaluate(NormalizedMail mail, MappingContext context)
    {
        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        var errors = new List<MappingIssue>();

        foreach (var mapping in _mappings)
        {
            var raw = mapping.Field.Read(mail);
            var text = raw switch
            {
                null => null,
                IReadOnlyList<string> list => list.Count == 0 ? null : string.Join(mapping.Join, list),
                _ => raw is string s ? (s.Length == 0 ? null : s) : null,
            };

            if (raw is IReadOnlyList<string> items)
            {
                if (items.Any(HasLineBreakOrNul))
                {
                    errors.Add(new MappingIssue(mapping.Location, "ControlCharacter", "単一行の項目に改行・NUL が含まれている。"));
                    continue;
                }
            }
            else if (text is not null
                && (mapping.Field.Name == "bodyText" ? text.Contains('\0') : HasLineBreakOrNul(text)))
            {
                errors.Add(new MappingIssue(mapping.Location, "ControlCharacter", "単一行の項目に改行・NUL が含まれている。"));
                continue;
            }

            object? converted;
            switch (raw)
            {
                case DateTimeOffset date:
                    converted = date.ToUniversalTime();
                    break;
                case int number:
                    converted = (decimal)number;
                    break;
                default:
                    if (text is null)
                    {
                        converted = mapping.Default;
                        break;
                    }

                    if (!TryConvert(text, mapping.Column, out converted))
                    {
                        errors.Add(new MappingIssue(mapping.Location, "ConversionFailed", "値を出力列の型へ変換できない。"));
                        continue;
                    }

                    break;
            }

            if (converted is null)
            {
                continue; // 値が無く既定も無い: 列を送らない（更新でも既存値を消さない）
            }

            if (!FitsLength(mapping.Column.Name, converted, context))
            {
                errors.Add(new MappingIssue(mapping.Location, "TooLong", "サイトの長さ制限を超える。切り捨てない。"));
                continue;
            }

            values[mapping.Column.Name] = converted;
        }

        foreach (var (column, value) in _fixed)
        {
            values[column] = value;
        }

        return errors.Count == 0 ? new MappedRecord(values, errors) : new MappedRecord(null, errors);
    }

    private static bool FitsLength(string column, object value, MappingContext context) =>
        context.MaxLengths is null
        || value is not string text
        || !context.MaxLengths.TryGetValue(column, out var max)
        || text.Length <= max;

    private static bool HasLineBreakOrNul(string value) =>
        value.AsSpan().IndexOfAny('\r', '\n', '\0') >= 0;

    private static bool IsCompatible(ValueKind source, ColumnType column) => (source, column) switch
    {
        (ValueKind.String or ValueKind.StringArray, ColumnType.Text or ColumnType.Number or ColumnType.Check) => true,
        (ValueKind.DateTime, ColumnType.Date) => true,
        (ValueKind.Integer, ColumnType.Number) => true,
        _ => false,
    };

    private static bool TryConvert(string text, ColumnInfo column, out object? value)
    {
        switch (column.Type)
        {
            case ColumnType.Text:
                value = text;
                return true;
            case ColumnType.Number:
                if (decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
                {
                    value = number;
                    return true;
                }

                break;
            case ColumnType.Check:
                if (bool.TryParse(text, out var flag))
                {
                    value = flag;
                    return true;
                }

                break;
            case ColumnType.Date:
                if (text.EndsWith('Z') || text.Contains('+') || text.LastIndexOf('-') > 9)
                {
                    if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    {
                        value = date.ToUniversalTime();
                        return true;
                    }
                }

                break;
        }

        value = null;
        return false;
    }

    private static bool TryConvertFixed(object raw, ColumnInfo column, out object value)
    {
        value = raw;
        switch (column.Type)
        {
            case ColumnType.Text:
                return raw is string;
            case ColumnType.Integer:
                if (raw is decimal whole && whole == decimal.Truncate(whole) && whole is >= int.MinValue and <= int.MaxValue)
                {
                    value = (long)whole;
                    return true;
                }

                return false;
            case ColumnType.Number:
                return raw is decimal;
            case ColumnType.Check:
                return raw is bool;
            default:
                return false; // 固定の日時は対応外
        }
    }

    private sealed record Compiled(
        string Location,
        SourceField Field,
        ColumnInfo Column,
        string? Join,
        object? Default);

    private enum ValueKind
    {
        String,
        StringArray,
        DateTime,
        Integer,
    }

    private sealed record SourceField(string Name, ValueKind Kind, Func<NormalizedMail, object?> Read);

    private static readonly Dictionary<string, SourceField> SourceFields = new SourceField[]
    {
        new("subject", ValueKind.String, m => m.Subject),
        new("bodyText", ValueKind.String, m => m.BodyText),
        new("from.addresses", ValueKind.StringArray, m => m.From.Addresses),
        new("from.names", ValueKind.StringArray, m => m.From.Names),
        new("to.addresses", ValueKind.StringArray, m => m.To.Addresses),
        new("to.names", ValueKind.StringArray, m => m.To.Names),
        new("cc.addresses", ValueKind.StringArray, m => m.Cc.Addresses),
        new("cc.names", ValueKind.StringArray, m => m.Cc.Names),
        new("messageId", ValueKind.String, m => m.MessageId),
        new("sentAt", ValueKind.DateTime, m => m.SentAt),
        new("receivedAt", ValueKind.DateTime, m => m.ReceivedAt),
        new("fetchedAt", ValueKind.DateTime, m => m.FetchedAt),
        new("attachmentCount", ValueKind.Integer, m => m.AttachmentCount),
        new("attachmentNames", ValueKind.StringArray, m => m.AttachmentNames),
    }.ToDictionary(f => f.Name, StringComparer.Ordinal);

    internal enum ColumnType
    {
        Text,
        Integer,
        Number,
        Check,
        Date,
    }

    internal sealed partial record ColumnInfo(string Name, ColumnType Type, string? HashName, bool FixedOnly)
    {
        public static ColumnInfo? Parse(string name)
        {
            switch (name)
            {
                case "Title":
                case "Body":
                    return new ColumnInfo(name, ColumnType.Text, null, false);
                case "Status":
                case "Owner":
                case "Manager":
                    // 最初は固定値のみ推奨。メールアドレスからユーザー ID への自動解決はしない
                    return new ColumnInfo(name, ColumnType.Integer, null, true);
            }

            var match = HashColumnRegex().Match(name);
            if (!match.Success)
            {
                return null;
            }

            return match.Groups[1].Value switch
            {
                "Class" => new ColumnInfo(name, ColumnType.Text, "ClassHash", false),
                "Description" => new ColumnInfo(name, ColumnType.Text, "DescriptionHash", false),
                "Date" => new ColumnInfo(name, ColumnType.Date, "DateHash", false),
                "Num" => new ColumnInfo(name, ColumnType.Number, "NumHash", false),
                "Check" => new ColumnInfo(name, ColumnType.Check, "CheckHash", false),
                _ => null,
            };
        }

        [GeneratedRegex("^(Class|Description|Date|Num|Check)([A-Z]|[0-9]{3})$")]
        private static partial Regex HashColumnRegex();
    }
}
