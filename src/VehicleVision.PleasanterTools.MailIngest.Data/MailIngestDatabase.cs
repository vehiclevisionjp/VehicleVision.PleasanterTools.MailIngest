using System.Runtime.CompilerServices;

namespace VehicleVision.PleasanterTools.MailIngest.Data;

/// <summary>
/// このアプリの DB。名前（設定名・ファイル名・ロック名）は、アプリ ID から Core.Data の <see cref="AppDatabase"/> が
/// 規約で導く（<c>mailingest.db</c>、<c>MAILINGEST_DB_PROVIDER</c> など）。<b>ここに手書きしない。</b>
/// </summary>
public static class MailIngestDatabase
{
    /// <summary>アプリ ID から導いた DB の名前と、マイグレーション・接続文字列の解決・暗号化検査。</summary>
    public static AppDatabase Instance { get; } =
        new("mailingest", typeof(MailIngestDatabase).Assembly);

    /// <summary>
    /// アセンブリの読み込み時に <see cref="Instance"/> を作る（SQLite のモデル型の登録が、必ず先に済む）。
    /// </summary>
    // CA2255 は「ライブラリでの [ModuleInitializer]」への注意。ここは再利用するライブラリではなく、このアプリのデータ層。
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Initialize() => _ = Instance;
}
