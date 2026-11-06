namespace VehicleVision.PleasanterTools.MailIngest.Core.Ingest;

/// <summary>
/// 1 ソースの 1 回の実行の運用サマリー（件数だけ。本文・件名・差出人・Message-ID などは持たない）。
/// </summary>
public sealed class SourceRunSummary(string sourceId)
{
    private readonly Dictionary<string, int> _heldByCode = new(StringComparer.Ordinal);

    public string SourceId { get; } = sourceId;

    /// <summary>今回の取り込み候補にした通数（既処理を除く）。</summary>
    public int Listed { get; internal set; }

    /// <summary>候補が上限を超えて、さらに残っている。</summary>
    public bool HasMore { get; internal set; }

    /// <summary>今回、レコードを作成して DB へ確定した通数。</summary>
    public int Created { get; internal set; }

    /// <summary>既にサーバにあった同じレコードを見つけて回復した通数（再作成していない）。</summary>
    public int Recovered { get; internal set; }

    /// <summary>同じメール（同一キー・同一内容）の重複として、作成しなかった通数。</summary>
    public int Duplicates { get; internal set; }

    /// <summary>今回、新たに保留（<c>Held</c>）にした通数。</summary>
    public int Held { get; internal set; }

    /// <summary>今回、結果不明（<c>Unknown</c>）にした通数。照合してから判断する。</summary>
    public int Unknown { get; internal set; }

    /// <summary>未作成が確実で、次回へ回した通数（<c>RetryPending</c> と、送る前に止まった <c>Pending</c>）。</summary>
    public int RetryLater { get; internal set; }

    /// <summary>実行を止めたため（または待機中のため）、今回は処理しなかった通数。</summary>
    public int Deferred { get; internal set; }

    /// <summary>分類できない例外で失敗した通数。</summary>
    public int Failed { get; internal set; }

    /// <summary>実行の開始時に、再起動で残っていた送信中（<c>Sending</c>）を結果不明（<c>Unknown</c>）にした件数。</summary>
    public int Interrupted { get; internal set; }

    /// <summary>結果不明を外部キーで照合し、レコードの存在と内容の一致を確認して確定した件数（再作成していない）。</summary>
    public int Reconciled { get; internal set; }

    /// <summary>API 成功済みで受信後操作が残っていた項目を、API を呼ばずに完了した件数。</summary>
    public int ResumedAck { get; internal set; }

    /// <summary>照合の検索が失敗し（通信・429 など）、結果不明のまま次回へ残した件数。</summary>
    public int ReconcileDeferred { get; internal set; }

    /// <summary>429 などで待たずに次回へ回したとき、これより前には再開しない時刻。</summary>
    public DateTimeOffset? RetryNotBefore { get; internal set; }

    /// <summary>ソース（またはその接続先）の処理を止めた理由。止めていなければ <c>null</c>。</summary>
    public string? StopReason { get; internal set; }

    /// <summary>実行の終了時点の、ソースの項目の状態ごとの件数（過去の保留を含む）。</summary>
    public IReadOnlyDictionary<State.IngestState, int> StateCounts { get; internal set; } = new Dictionary<State.IngestState, int>();

    /// <summary>終了時点で、指定の状態にある項目の件数。</summary>
    public int Outstanding(State.IngestState state) => StateCounts.GetValueOrDefault(state);

    /// <summary>保留の理由コードごとの件数。</summary>
    public IReadOnlyDictionary<string, int> HeldByCode => _heldByCode;

    internal void CountHeld(string code)
    {
        Held++;
        _heldByCode[code] = _heldByCode.GetValueOrDefault(code) + 1;
    }
}
