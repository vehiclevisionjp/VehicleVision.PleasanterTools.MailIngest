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

    /// <summary>ソース（またはその接続先）の処理を止めた理由。止めていなければ <c>null</c>。</summary>
    public string? StopReason { get; internal set; }

    /// <summary>保留の理由コードごとの件数。</summary>
    public IReadOnlyDictionary<string, int> HeldByCode => _heldByCode;

    internal void CountHeld(string code)
    {
        Held++;
        _heldByCode[code] = _heldByCode.GetValueOrDefault(code) + 1;
    }
}
