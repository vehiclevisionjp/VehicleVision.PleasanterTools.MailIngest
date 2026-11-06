using System.Security.Cryptography;
using System.Text;
using VehicleVision.PleasanterTools.MailIngest.Core.Configuration;
using VehicleVision.PleasanterTools.MailIngest.Core.Identity;
using VehicleVision.PleasanterTools.MailIngest.Mail.Mime;

namespace VehicleVision.PleasanterTools.MailIngest.Core.Tests;

/// <summary>合成データだけで MIME の正規化と、上限の強制方法を確かめる（実在のメールは使わない）。</summary>
public sealed class MimeMessageNormalizerTests
{
    private static readonly DateTimeOffset Fetched = new(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);

    private static byte[] Raw(params string[] lines) => Encoding.UTF8.GetBytes(string.Join("\r\n", lines));

    private static byte[] RawBytes(string header, byte[] body) =>
        [.. Encoding.ASCII.GetBytes(header + "\r\n\r\n"), .. body];

    private static Task<MimeNormalizeResult> Normalize(byte[] raw, LimitsConfig? limits = null) =>
        MimeMessageNormalizer.NormalizeAsync(new MemoryStream(raw), limits ?? LimitsConfig.Default, Fetched);

    private static string B64(string text, string charset) =>
        Convert.ToBase64String(Encoding.GetEncoding(charset).GetBytes(text));

    private const string Headers =
        "From: Alice <alice@example.invalid>\r\nTo: bob@example.invalid\r\nMessage-ID: <m1@example.invalid>\r\n"
        + "Date: Fri, 02 Jan 2026 03:04:05 +0000\r\n";

    [Fact]
    public async Task UTF8_の件名と_base64_本文を正規化する()
    {
        var raw = Raw(
            Headers.TrimEnd(),
            "Subject: =?UTF-8?B?" + B64("問い合わせ", "utf-8") + "?=",
            "MIME-Version: 1.0",
            "Content-Type: text/plain; charset=utf-8",
            "Content-Transfer-Encoding: base64",
            string.Empty,
            B64("本文です\n二行目", "utf-8"));

        var result = await Normalize(raw);

        Assert.True(result.IsSuccess, result.Failure.ToString());
        var mail = result.Mail!;
        Assert.Equal("問い合わせ", mail.Subject);
        Assert.Equal("本文です\n二行目", mail.BodyText);
        Assert.Equal(["alice@example.invalid"], mail.From.Addresses);
        Assert.Equal(["Alice"], mail.From.Names);
        Assert.Equal(["bob@example.invalid"], mail.To.Addresses);
        Assert.Equal([string.Empty], mail.To.Names);
        Assert.Equal("m1@example.invalid", mail.MessageId);
        Assert.True(result.MessageIdIsValid);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), mail.SentAt);
        Assert.Equal(Fetched, mail.FetchedAt);
        Assert.Null(mail.ReceivedAt);
    }

    [Fact]
    public async Task ISO2022JP_の件名と本文を復号する()
    {
        var body = Encoding.GetEncoding("iso-2022-jp").GetBytes("日本語の本文です。");
        var raw = RawBytes(
            Headers + "Subject: =?ISO-2022-JP?B?" + B64("日本語の件名", "iso-2022-jp") + "?=\r\n"
            + "Content-Type: text/plain; charset=ISO-2022-JP\r\nContent-Transfer-Encoding: 7bit",
            body);

        var result = await Normalize(raw);

        Assert.True(result.IsSuccess, result.Failure.ToString());
        Assert.Equal("日本語の件名", result.Mail!.Subject);
        Assert.Equal("日本語の本文です。", result.Mail.BodyText);
    }

    [Fact]
    public async Task ShiftJIS_の本文と_Q_エンコードの件名を復号する()
    {
        var subject = string.Concat(Encoding.GetEncoding("shift_jis").GetBytes("件名").Select(b => "=" + b.ToString("X2")));
        var raw = Raw(
            Headers.TrimEnd(),
            "Subject: =?Shift_JIS?Q?" + subject + "?=",
            "Content-Type: text/plain; charset=Shift_JIS",
            "Content-Transfer-Encoding: base64",
            string.Empty,
            B64("シフトJISの本文", "shift_jis"));

        var result = await Normalize(raw);

        Assert.True(result.IsSuccess, result.Failure.ToString());
        Assert.Equal("件名", result.Mail!.Subject);
        Assert.Equal("シフトJISの本文", result.Mail.BodyText);
    }

    [Fact]
    public async Task 折り返された複数の_encoded_word_の件名をつなぐ()
    {
        var raw = Raw(
            Headers.TrimEnd(),
            "Subject: =?UTF-8?B?" + B64("前半", "utf-8") + "?=",
            " =?UTF-8?B?" + B64("後半", "utf-8") + "?=",
            "Content-Type: text/plain; charset=utf-8",
            string.Empty,
            "body");

        var result = await Normalize(raw);

        Assert.Equal("前半後半", result.Mail!.Subject);
    }

    [Fact]
    public async Task quoted_printable_の本文を復号する()
    {
        var raw = Raw(
            Headers.TrimEnd(),
            "Subject: qp",
            "Content-Type: text/plain; charset=utf-8",
            "Content-Transfer-Encoding: quoted-printable",
            string.Empty,
            "=E6=97=A5=E6=9C=AC=E8=AA=9E=0D=0A=E3=81=A7=E3=81=99");

        Assert.Equal("日本語\nです", (await Normalize(raw)).Mail!.BodyText);
    }

    [Fact]
    public async Task multipart_alternative_は_plain_を優先する()
    {
        var raw = Raw(
            Headers.TrimEnd(),
            "Subject: alt",
            "Content-Type: multipart/alternative; boundary=\"b1\"",
            string.Empty,
            "--b1",
            "Content-Type: text/html; charset=utf-8",
            string.Empty,
            "<p>HTML 本文</p>",
            "--b1",
            "Content-Type: text/plain; charset=utf-8",
            string.Empty,
            "プレーン本文",
            "--b1--");

        var result = await Normalize(raw);

        Assert.True(result.IsSuccess);
        Assert.Equal("プレーン本文", result.Mail!.BodyText);
        Assert.Equal(0, result.Mail.AttachmentCount);
    }

    [Fact]
    public async Task HTML_だけなら_テキスト化し_script_と_style_を捨てる()
    {
        var html = "<html><head><title>t</title><style>p{color:red}</style></head><body>"
            + "<script>alert('x')</script><p>一行目</p><p>二行目<br>三行目 &amp; &lt;b&gt;</p>"
            + "<a href=\"http://tracker.example.invalid/x\">リンク文字列</a><img src=\"http://img.example.invalid/a.png\"></body></html>";
        var raw = Raw(
            Headers.TrimEnd(),
            "Content-Type: text/html; charset=utf-8",
            string.Empty,
            html);

        var result = await Normalize(raw);

        Assert.True(result.IsSuccess);
        var text = result.Mail!.BodyText!;
        Assert.Equal("一行目\n二行目\n三行目 & <b>\nリンク文字列", text);
        Assert.DoesNotContain("alert", text, StringComparison.Ordinal);
        Assert.DoesNotContain("color", text, StringComparison.Ordinal);
        Assert.DoesNotContain("tracker", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 添付は数と安全化した名前と内容のダイジェストだけを取る()
    {
        var content = new byte[] { 1, 2, 3, 4, 5 };
        var raw = Raw(
            Headers.TrimEnd(),
            "Content-Type: multipart/mixed; boundary=\"b1\"",
            string.Empty,
            "--b1",
            "Content-Type: text/plain; charset=utf-8",
            string.Empty,
            "本文",
            "--b1",
            "Content-Type: application/pdf; name=\"..\\..\\evil\\report.pdf\"",
            "Content-Disposition: attachment; filename=\"../../evil/report.pdf\"",
            "Content-Transfer-Encoding: base64",
            string.Empty,
            Convert.ToBase64String(content),
            "--b1--");

        var result = await Normalize(raw);

        Assert.True(result.IsSuccess);
        Assert.Equal("本文", result.Mail!.BodyText);
        Assert.Equal(1, result.Mail.AttachmentCount);
        Assert.Equal(["report.pdf"], result.Mail.AttachmentNames);
        Assert.Equal([Convert.ToHexStringLower(SHA256.HashData(content))], result.AttachmentDigests);
    }

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("C:\\temp\\a.txt", "a.txt")]
    [InlineData("a\r\nb.txt", "ab.txt")]
    [InlineData("..hidden", "hidden")]
    [InlineData("", "attachment-3")]
    [InlineData("/", "attachment-3")]
    public void 添付名を安全にする(string name, string expected)
    {
        Assert.Equal(expected, MimeMessageNormalizer.SanitizeAttachmentName(name, 3));
    }

    [Fact]
    public void 添付名は長さを制限する()
    {
        Assert.Equal(
            MimeMessageNormalizer.MaxAttachmentNameLength,
            MimeMessageNormalizer.SanitizeAttachmentName(new string('a', 1000), 1).Length);
    }

    [Fact]
    public async Task 添付だけのメールは本文なし()
    {
        var raw = Raw(
            Headers.TrimEnd(),
            "Content-Type: application/octet-stream; name=a.bin",
            "Content-Transfer-Encoding: base64",
            string.Empty,
            Convert.ToBase64String([9, 9]));

        var result = await Normalize(raw);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Mail!.BodyText);
        Assert.Equal(["a.bin"], result.Mail.AttachmentNames);
    }

    [Fact]
    public async Task Message_ID_欠落は無効とし_RAW_の_SHA256_を返し_別名前空間のキーを作れる()
    {
        var raw = Raw(
            "From: a@example.invalid",
            "To: b@example.invalid",
            "Subject: no id",
            "Content-Type: text/plain; charset=utf-8",
            string.Empty,
            "x");

        var result = await Normalize(raw);

        Assert.True(result.IsSuccess);
        Assert.False(result.MessageIdIsValid);
        Assert.Null(result.Mail!.MessageId);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(raw)), result.RawSha256);
        var rawKey = ExternalKey.CreateFromRawHash("d", 1, "s", result.RawSha256!);
        Assert.Matches("^[0-9a-f]{64}$", rawKey);
        Assert.NotEqual(ExternalKey.Create("d", 1, "s", result.RawSha256!), rawKey);
        Assert.NotEqual(rawKey, ExternalKey.CreateFromRawHash("d", 1, "s", new string('0', 64)));
    }

    [Fact]
    public async Task 複数の_Message_ID_ヘッダや不正な_Message_ID_は無効とする()
    {
        var twice = Raw(
            "Message-ID: <a@x>", "Message-ID: <b@x>", "From: a@example.invalid", "Content-Type: text/plain", string.Empty, "x");
        var spaced = Raw(
            "Message-ID: <a b@x>", "From: a@example.invalid", "Content-Type: text/plain", string.Empty, "x");

        Assert.False((await Normalize(twice)).MessageIdIsValid);
        Assert.False((await Normalize(spaced)).MessageIdIsValid);
    }

    [Fact]
    public async Task Date_欠落は処理時刻に置換せず_null()
    {
        var raw = Raw(
            "From: a@example.invalid", "Message-ID: <a@x>", "Content-Type: text/plain; charset=utf-8", string.Empty, "x");

        Assert.Null((await Normalize(raw)).Mail!.SentAt);
    }

    [Fact]
    public async Task 複数の差出人と宛先を先頭の1件にしない()
    {
        var raw = Raw(
            "From: A <a@example.invalid>, B <b@example.invalid>",
            "To: c@example.invalid",
            "Cc: d@example.invalid, \"E\" <e@example.invalid>",
            "Message-ID: <a@x>",
            "Content-Type: text/plain; charset=utf-8",
            string.Empty,
            "x");

        var mail = (await Normalize(raw)).Mail!;

        Assert.Equal(["a@example.invalid", "b@example.invalid"], mail.From.Addresses);
        Assert.Equal(["A", "B"], mail.From.Names);
        Assert.Equal(["d@example.invalid", "e@example.invalid"], mail.Cc.Addresses);
        Assert.Equal(["", "E"], mail.Cc.Names);
    }

    [Fact]
    public async Task 不明な文字コード_復号できないバイト_charset_省略の非_ASCII_は_InvalidEncoding()
    {
        var unknown = Raw(Headers.TrimEnd(), "Content-Type: text/plain; charset=x-unknown-charset", string.Empty, "abc");
        var broken = RawBytes(Headers + "Content-Type: text/plain; charset=utf-8", [0xE3, 0x81]);
        var noCharset = RawBytes(Headers + "Content-Type: text/plain", [0xE3, 0x81, 0x82]);

        Assert.Equal(MimeFailure.InvalidEncoding, (await Normalize(unknown)).Failure);
        Assert.Equal(MimeFailure.InvalidEncoding, (await Normalize(broken)).Failure);
        Assert.Equal(MimeFailure.InvalidEncoding, (await Normalize(noCharset)).Failure);
    }

    [Fact]
    public async Task 件名へ改行を紛れ込ませる_encoded_word_は無害化され_制御文字が残らない()
    {
        var raw = Raw(
            Headers.TrimEnd(),
            "Subject: =?UTF-8?B?" + B64("a\r\nBcc: x@example.invalid", "utf-8") + "?=",
            "Content-Type: text/plain; charset=utf-8",
            string.Empty,
            "x");

        var result = await Normalize(raw);

        // MimeKit が decode 時に CR / LF を除くため、ヘッダの注入にならない。残っていれば ControlCharacter で拒否する（多層防御）
        Assert.True(result.IsSuccess || result.Failure == MimeFailure.ControlCharacter);
        Assert.True(result.Mail is null || result.Mail.Subject!.IndexOfAny(['\r', '\n', '\0']) < 0);
        Assert.DoesNotContain("\nBcc:", result.Mail?.Subject ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 本文の_NUL_は拒否する()
    {
        var raw = Raw(
            Headers.TrimEnd(),
            "Content-Type: text/plain; charset=utf-8",
            "Content-Transfer-Encoding: base64",
            string.Empty,
            Convert.ToBase64String(Encoding.UTF8.GetBytes("a\0b")));

        Assert.Equal(MimeFailure.ControlCharacter, (await Normalize(raw)).Failure);
    }

    [Fact]
    public async Task RAW_の上限ちょうどは通り_プラス_1_は拒否する()
    {
        var raw = Raw(Headers.TrimEnd(), "Content-Type: text/plain; charset=utf-8", string.Empty, "x");
        var exact = LimitsConfig.Default with { RawBytes = raw.Length };
        var tooSmall = LimitsConfig.Default with { RawBytes = raw.Length - 1 };

        Assert.True((await Normalize(raw, exact)).IsSuccess);
        var rejected = await Normalize(raw, tooSmall);
        Assert.Equal(MimeFailure.RawTooLarge, rejected.Failure);
        Assert.Null(rejected.Mail);
    }

    [Fact]
    public async Task RAW_の上限は実ストリームで強制し_上限を超えて読まない()
    {
        var limit = 1000;
        await using var stream = new CountingEndlessStream();

        var result = await MimeMessageNormalizer.NormalizeAsync(
            stream,
            LimitsConfig.Default with { RawBytes = limit },
            Fetched);

        Assert.Equal(MimeFailure.RawTooLarge, result.Failure);
        Assert.InRange(stream.BytesRead, limit + 1, limit + 64 * 1024); // 無限のストリームでも、1 回分の読み込みで止まる
    }

    [Fact]
    public async Task 本文のデコード後サイズの上限は_上限ちょうどで通り_プラス_1_で拒否する()
    {
        var content = new string('A', 1000);
        var raw = Raw(
            Headers.TrimEnd(),
            "Content-Type: text/plain; charset=us-ascii",
            "Content-Transfer-Encoding: base64",
            string.Empty,
            Convert.ToBase64String(Encoding.ASCII.GetBytes(content)));

        Assert.True((await Normalize(raw, LimitsConfig.Default with { DecodedBodyBytes = 1000 })).IsSuccess);
        Assert.Equal(
            MimeFailure.BodyTooLarge,
            (await Normalize(raw, LimitsConfig.Default with { DecodedBodyBytes = 999 })).Failure);
    }

    [Fact]
    public async Task 入れ子の深さは上限ちょうどで通り_プラス_1_で拒否する()
    {
        var limits = LimitsConfig.Default with { MimeDepth = 5 };

        // 本文の葉の深さ = 入れ子の multipart の数 + 1
        Assert.True((await Normalize(Nested(4), limits)).IsSuccess);
        Assert.Equal(MimeFailure.MimeTooDeep, (await Normalize(Nested(5), limits)).Failure);
    }

    [Fact]
    public async Task 極端に深い入れ子でも例外にならず拒否する()
    {
        var result = await Normalize(Nested(3000), LimitsConfig.Default with { MimeDepth = 32 });

        Assert.Equal(MimeFailure.MimeTooDeep, result.Failure);
    }

    [Fact]
    public async Task パート数の上限は上限ちょうどで通り_プラス_1_で拒否する()
    {
        var limits = LimitsConfig.Default with { MimeParts = 10 };

        // multipart 1 + 葉 9 = 10
        Assert.True((await Normalize(Flat(9), limits)).IsSuccess);
        Assert.Equal(MimeFailure.TooManyParts, (await Normalize(Flat(10), limits)).Failure);
    }

    [Fact]
    public async Task ヘッダ合計の上限は超えたら拒否する()
    {
        var many = string.Join("\r\n", Enumerable.Range(0, 50).Select(i => $"X-Header-{i}: {new string('v', 100)}"));
        var raw = Raw(Headers.TrimEnd(), many, "Content-Type: text/plain; charset=utf-8", string.Empty, "x");

        Assert.True((await Normalize(raw)).IsSuccess);
        Assert.Equal(
            MimeFailure.HeadersTooLarge,
            (await Normalize(raw, LimitsConfig.Default with { HeaderBytes = 2000 })).Failure);
    }

    [Fact]
    public async Task 暗号化された本文しか無ければ未対応として残す()
    {
        var smime = Raw(
            Headers.TrimEnd(),
            "Content-Type: application/pkcs7-mime; smime-type=enveloped-data; name=smime.p7m",
            "Content-Transfer-Encoding: base64",
            string.Empty,
            Convert.ToBase64String([1, 2, 3]));
        var pgp = Raw(
            Headers.TrimEnd(),
            "Content-Type: multipart/encrypted; protocol=\"application/pgp-encrypted\"; boundary=\"b1\"",
            string.Empty,
            "--b1",
            "Content-Type: application/pgp-encrypted",
            string.Empty,
            "Version: 1",
            "--b1",
            "Content-Type: application/octet-stream",
            string.Empty,
            "-----BEGIN PGP MESSAGE-----",
            "--b1--");

        Assert.Equal(MimeFailure.UnsupportedEncrypted, (await Normalize(smime)).Failure);
        Assert.Equal(MimeFailure.UnsupportedEncrypted, (await Normalize(pgp)).Failure);
    }

    [Fact]
    public async Task 空の入力は_InvalidMime()
    {
        var result = await Normalize([]);

        Assert.Equal(MimeFailure.InvalidMime, result.Failure);
    }

    [Fact]
    public async Task 失敗してもメールの内容は結果へ載せない()
    {
        var raw = Raw(Headers.TrimEnd(), "Subject: 秘密の件名", "Content-Type: text/plain; charset=x-unknown", string.Empty, "秘密の本文");

        var result = await Normalize(raw);

        Assert.Equal(MimeFailure.InvalidEncoding, result.Failure);
        Assert.Null(result.Mail);
        Assert.DoesNotContain("秘密", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task キャンセルは伝える()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => MimeMessageNormalizer.NormalizeAsync(
                new MemoryStream(Raw("From: a@example.invalid", string.Empty, "x")),
                LimitsConfig.Default,
                Fetched,
                cancellationToken: cts.Token));
    }

    [Fact]
    public async Task 正規化したメールは内容ハッシュに添付のダイジェストを含められる()
    {
        byte[] Message(byte content) => Raw(
            Headers.TrimEnd(),
            "Content-Type: multipart/mixed; boundary=\"b1\"",
            string.Empty,
            "--b1",
            "Content-Type: text/plain; charset=utf-8",
            string.Empty,
            "同じ本文",
            "--b1",
            "Content-Type: application/octet-stream; name=a.bin",
            "Content-Transfer-Encoding: base64",
            string.Empty,
            Convert.ToBase64String([content]),
            "--b1--");

        var one = await Normalize(Message(1));
        var two = await Normalize(Message(2));

        Assert.NotEqual(ContentHash.Compute(one.Mail!, one.AttachmentDigests), ContentHash.Compute(two.Mail!, two.AttachmentDigests));
        Assert.Equal(ContentHash.Compute(one.Mail!, one.AttachmentDigests), ContentHash.Compute(one.Mail!, one.AttachmentDigests));
    }

    [Theory]
    [InlineData("<p>a</p><p>b</p>", "a\nb")]
    [InlineData("a<br/>b<br>c", "a\nb\nc")]
    [InlineData("  a   \n\t b  ", "a b")]
    [InlineData("<div>a</div><div></div><div></div><div>b</div>", "a\n\nb")]
    [InlineData("<script>x()</script>t<style>.a{}</style>", "t")]
    [InlineData("&lt;tag&gt; &amp; &quot;q&quot; &#12354;", "<tag> & \"q\" あ")]
    [InlineData("<ul><li>1</li><li>2</li></ul>", "1\n2")]
    [InlineData("", "")]
    public void HTML_のテキスト化(string html, string expected)
    {
        Assert.Equal(expected, HtmlTextExtractor.ToText(html));
    }

    private static byte[] Nested(int multiparts)
    {
        var builder = new StringBuilder(Headers);
        for (var i = 0; i < multiparts; i++)
        {
            builder.Append($"Content-Type: multipart/mixed; boundary=\"b{i}\"\r\n\r\n--b{i}\r\n");
        }

        builder.Append("Content-Type: text/plain; charset=utf-8\r\n\r\nleaf\r\n");
        for (var i = multiparts - 1; i >= 0; i--)
        {
            builder.Append($"--b{i}--\r\n");
        }

        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    private static byte[] Flat(int leaves)
    {
        var builder = new StringBuilder(Headers);
        builder.Append("Content-Type: multipart/mixed; boundary=\"b\"\r\n\r\n");
        for (var i = 0; i < leaves; i++)
        {
            builder.Append("--b\r\nContent-Type: text/plain; charset=utf-8\r\n\r\npart\r\n");
        }

        builder.Append("--b--\r\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    /// <summary>終わらない入力。読んだ量を数える。</summary>
    private sealed class CountingEndlessStream : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Fill(buffer, (byte)'a', offset, count);
            BytesRead += count;
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            buffer.Span.Fill((byte)'a');
            BytesRead += buffer.Length;
            return ValueTask.FromResult(buffer.Length);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
