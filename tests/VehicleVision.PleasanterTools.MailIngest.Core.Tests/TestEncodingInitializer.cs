using System.Runtime.CompilerServices;
using System.Text;

namespace VehicleVision.PleasanterTools.MailIngest.Core.Tests;

/// <summary>
/// 試験が MimeKit を直接使う（メールの作成）前に、日本語のコードページを登録する。
/// MimeKit は文字集合の検索結果を最初の利用時に固定するため、登録が後になると復号試験が並列実行の順序で落ちる。
/// </summary>
internal static class TestEncodingInitializer
{
#pragma warning disable CA2255 // 試験アセンブリの読み込み時に登録する
    [ModuleInitializer]
    internal static void Register() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
#pragma warning restore CA2255
}
