namespace VehicleVision.PleasanterTools.MailIngest.Core.State;

/// <summary>取り込み項目（1 通のメール）の状態。</summary>
/// <remarks>遷移は <see cref="IngestStateTransitions"/>。設計 <c>取り込み状態とAPI.md</c> 第 4 節の状態遷移図に従う。</remarks>
public enum IngestState
{
    /// <summary>状態を確保した。まだ送っていない。</summary>
    Pending,

    /// <summary>送信前に永続化した。再起動で残っていたら結果不明として扱う。</summary>
    Sending,

    /// <summary>未作成が確実な一時拒否。待機後に <see cref="Pending"/> へ戻る。</summary>
    RetryPending,

    /// <summary>API 成功を DB へ確定した。受信後操作が残っている（leave なら完了）。</summary>
    AckPending,

    /// <summary>応答不明・送信中に再起動。外部キーで照合してから判断する。再作成しない。</summary>
    Unknown,

    /// <summary>恒久エラー・競合。人の判断を待つ。メールは削除・既読化しない。</summary>
    Held,

    /// <summary>完了。</summary>
    Completed,
}

/// <summary>許される状態遷移。</summary>
public static class IngestStateTransitions
{
    private static readonly IReadOnlyDictionary<IngestState, IngestState[]> Allowed =
        new Dictionary<IngestState, IngestState[]>
        {
            [IngestState.Pending] = [IngestState.Sending, IngestState.Held],
            [IngestState.Sending] = [IngestState.AckPending, IngestState.Unknown, IngestState.RetryPending, IngestState.Held],
            [IngestState.RetryPending] = [IngestState.Pending],
            [IngestState.Unknown] = [IngestState.AckPending, IngestState.Held],
            [IngestState.AckPending] = [IngestState.AckPending, IngestState.Completed],
            [IngestState.Held] = [],
            [IngestState.Completed] = [],
        };

    public static bool IsAllowed(IngestState from, IngestState to) => Allowed[from].Contains(to);
}

/// <summary>メールサーバ上の受信後操作の状態。</summary>
public enum AckState
{
    /// <summary>未実施（leave なら何もしない）。</summary>
    Pending,

    /// <summary>実施済み、または不要。</summary>
    Done,

    /// <summary>応答不明。元 UID の消失だけで成功と断定せず、照合か手動確認へ回す。</summary>
    Unknown,
}

/// <summary>取り込み項目。本文・RAW・パスワード・ApiKey・添付は持たない。</summary>
public sealed record IngestItem(
    Guid Id,
    string DestinationId,
    long SiteId,
    string SourceId,
    string ExternalKey,
    string KeyVersion,
    string ContentHash,
    string? ProjectionHash,
    long? RecordId,
    int MappingRevision,
    IngestState State,
    int AttemptCount,
    DateTimeOffset? NextAttemptAt,
    string? LastErrorCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>新しい取り込み項目の確保要求。</summary>
public sealed record NewIngestItem(
    string DestinationId,
    long SiteId,
    string SourceId,
    string ExternalKey,
    string KeyVersion,
    string ContentHash,
    int MappingRevision);

/// <summary>状態遷移に伴って更新する値。<c>null</c> の項目は変更しない。</summary>
public sealed record TransitionData
{
    public long? RecordId { get; init; }

    public string? ProjectionHash { get; init; }

    public string? LastErrorCode { get; init; }

    public DateTimeOffset? NextAttemptAt { get; init; }
}

/// <summary>確保の結果。</summary>
/// <param name="Item">確保した（または既にあった）項目。</param>
/// <param name="Created">今回新規に作った。<c>false</c> なら既処理で、内容ハッシュを比較する。</param>
public sealed record EnsureResult(IngestItem Item, bool Created);

/// <summary>メールサーバ上の位置。UIDL / UID は恒久の業務キーではなく、操作用の識別。</summary>
/// <param name="LocationKey">
/// POP3 は <c>pop3:{UIDL}</c>、IMAP は <c>imap:{フォルダ}:{UIDVALIDITY}:{UID}</c> のように、ソース内で一意な文字列。
/// </param>
public sealed record MailLocation(string Protocol, string LocationKey, string? Folder, long? UidValidity, long? Uid, string? Uidl);

/// <summary>実行（ソース単位）。</summary>
public sealed record SourceRun(Guid Id, string SourceId, string OwnerId, DateTimeOffset StartedAt);

/// <summary>取り込み状態の保管。実装は <c>.Data</c>（Core.Data の DB）。</summary>
public interface IIngestStateStore
{
    Task<IngestItem?> FindAsync(
        string destinationId, long siteId, string sourceId, string externalKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// 状態を確保する。同じ <c>(DestinationId, SiteId, SourceId, ExternalKey)</c> が既にあれば、それを返す（<c>Created = false</c>）。
    /// 一意制約で二重確保を防ぎ、競合したら既存を返す。
    /// </summary>
    Task<EnsureResult> EnsureAsync(NewIngestItem item, CancellationToken cancellationToken = default);

    /// <summary>
    /// 状態を遷移する。現在の状態が <paramref name="from"/> のときだけ更新し、違えば <c>false</c>（他の実行が先に進めた）。
    /// 許されない遷移は <see cref="InvalidOperationException"/>。<see cref="IngestState.Sending"/> へ入るとき試行回数を増やす。
    /// </summary>
    Task<bool> TransitionAsync(
        Guid id, IngestState from, IngestState to, TransitionData? data = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 再起動で残った <see cref="IngestState.Sending"/> を <see cref="IngestState.Unknown"/> にする（結果不明の扱い）。
    /// 自動再送はしない。変更した件数を返す。
    /// </summary>
    Task<int> MarkInterruptedAsUnknownAsync(string sourceId, CancellationToken cancellationToken = default);

    Task AddLocationAsync(
        Guid ingestItemId, string sourceId, MailLocation location, CancellationToken cancellationToken = default);

    /// <summary>位置（UIDL / UID）から既処理の項目を引く。</summary>
    Task<IngestItem?> FindByLocationAsync(
        string sourceId, string locationKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// 受信済みで、再取得しない位置の一覧（状態が <see cref="IngestState.Pending"/> と <see cref="IngestState.RetryPending"/> 以外の項目の位置）。
    /// 取得時の既処理判定（<see cref="Mail.IMailSession.ListAsync"/> の <c>isKnown</c>）に使う。
    /// </summary>
    Task<IReadOnlySet<string>> ListSettledLocationKeysAsync(string sourceId, CancellationToken cancellationToken = default);

    /// <summary>ソースの項目を、状態を指定して古い順（更新時刻）に最大 <paramref name="limit"/> 件返す。照合・再開の対象を挙げる。</summary>
    Task<IReadOnlyList<IngestItem>> ListByStateAsync(
        string sourceId, IngestState state, int limit, CancellationToken cancellationToken = default);

    /// <summary>ソースの項目の、状態ごとの件数（0 件の状態は含まない）。運用サマリー・終了コードに使う。</summary>
    Task<IReadOnlyDictionary<IngestState, int>> CountByStateAsync(string sourceId, CancellationToken cancellationToken = default);

    /// <summary>項目に結びつくすべての位置の受信後操作の状態を設定する。</summary>
    Task SetItemAckStateAsync(Guid ingestItemId, AckState state, CancellationToken cancellationToken = default);

    Task<bool> SetAckStateAsync(
        string sourceId, string locationKey, AckState state, CancellationToken cancellationToken = default);

    /// <summary>
    /// ソースの実行を開始する。**同じソースの実行が終わっていなければ <c>null</c>**（同時実行の拒否）。
    /// 実行が異常終了して残った場合も、期限切れで自動的に引き継がない（進行中の作成を再送しないため）。人が確認して解除する。
    /// </summary>
    Task<SourceRun?> TryBeginRunAsync(string sourceId, string ownerId, CancellationToken cancellationToken = default);

    Task EndRunAsync(Guid runId, string status, CancellationToken cancellationToken = default);

    /// <summary>終わっていない実行（あれば）。異常終了の診断用。</summary>
    Task<SourceRun?> FindActiveRunAsync(string sourceId, CancellationToken cancellationToken = default);
}
