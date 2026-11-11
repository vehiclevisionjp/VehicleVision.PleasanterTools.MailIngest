using VehicleVision.PleasanterTools.MailIngest.Core.Configuration;
using VehicleVision.PleasanterTools.MailIngest.Core.Mapping;

namespace VehicleVision.PleasanterTools.MailIngest.Core.Ingest;

/// <summary>
/// 1 ソース分の、検査済みの実行計画。起動時に作り、エラーがあれば実行せず停止する（終了コード 2）。
/// </summary>
public sealed class SourcePlan
{
    private SourcePlan(
        PleasanterDestinationConfig destination,
        MailSourceConfig source,
        MappingPlan mapping,
        MappingContext context)
    {
        Destination = destination;
        Source = source;
        Mapping = mapping;
        Context = context;
    }

    public PleasanterDestinationConfig Destination { get; }

    public MailSourceConfig Source { get; }

    public MappingPlan Mapping { get; }

    public MappingContext Context { get; }

    /// <summary>予約列（外部キー・投射ハッシュ）。</summary>
    public IReadOnlyCollection<string> ReservedColumns =>
        [Source.Target.ExternalKeyColumn, Source.Target.ProjectionHashColumn];

    /// <summary>
    /// 受信後操作。決定（2026-11-02、#53）により当面は <see cref="AfterSuccessAction.Leave"/> だけ実装する。
    /// markSeen・move・delete は後続（#84）で、設定にあっても実行しない（起動エラー）。
    /// </summary>
    public static AfterSuccessAction AfterSuccessOf(MailSourceConfig source) =>
        source.Protocol == MailProtocol.Imap
            ? source.Imap?.AfterSuccess ?? AfterSuccessAction.Leave
            : source.Pop3?.AfterSuccess ?? AfterSuccessAction.Leave;

    public static (SourcePlan? Plan, IReadOnlyList<ConfigIssue> Errors) Create(
        PleasanterDestinationConfig destination,
        MailSourceConfig source,
        string path)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(source);

        var errors = new List<ConfigIssue>();

        if (AfterSuccessOf(source) != AfterSuccessAction.Leave)
        {
            errors.Add(new ConfigIssue(
                path,
                "AfterSuccessNotSupported",
                "Only leave is implemented for the after-success action (markSeen, move and delete come later)."));
        }

        TimeZoneInfo? zone = null;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(destination.TimeZone);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            errors.Add(new ConfigIssue("pleasanter.timeZone", "UnknownTimeZone", "The time zone cannot be resolved."));
        }

        var target = source.Target;
        var rules = target.Mappings.Select(m => new MappingRule(m.Source, m.Target, m.Default, m.Join)).ToList();
        var (mapping, mappingErrors) = MappingPlan.Compile(
            rules,
            target.FixedValues,
            [target.ExternalKeyColumn, target.ProjectionHashColumn]);
        foreach (var issue in mappingErrors)
        {
            errors.Add(new ConfigIssue($"{path}.target.{issue.Location}", issue.Code, issue.Message));
        }

        if (errors.Count > 0 || mapping is null || zone is null)
        {
            return (null, errors);
        }

        return (new SourcePlan(destination, source, mapping, new MappingContext { TimeZone = zone }), errors);
    }
}
