using System.Runtime.CompilerServices;
using System.Text;

namespace VehicleVision.PleasanterTools.MailIngest.Mail;

/// <summary>
/// 日本語のコードページ（ISO-2022-JP・Shift_JIS・EUC-JP など）を、MimeKit が最初に文字集合を引く前に使えるようにする。
/// </summary>
/// <remarks>
/// MimeKit は文字集合の検索結果を最初の利用時に固定する。登録が後になると、以降は復号できず
/// 件名・本文が文字化けする（先に MimeKit を使った処理や並列実行の順序で起きる）。
/// 型の静的初期化ではなくアセンブリの読み込み時に登録して、順序に依存しないようにする。
/// </remarks>
internal static class MailEncodingInitializer
{
#pragma warning disable CA2255 // ライブラリでの ModuleInitializer: 文字集合の登録は MimeKit の初回利用より前に要る
    [ModuleInitializer]
    internal static void Register() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
#pragma warning restore CA2255
}
