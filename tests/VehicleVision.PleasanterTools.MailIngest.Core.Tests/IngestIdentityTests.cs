using System.Security.Cryptography;
using System.Text;
using VehicleVision.PleasanterTools.MailIngest.Core.Identity;
using VehicleVision.PleasanterTools.MailIngest.Core.Mapping;

namespace VehicleVision.PleasanterTools.MailIngest.Core.Tests;

public sealed class IngestIdentityTests
{
    private static readonly string[] Excluded = ["ClassZ", "ClassY"];

    private static NormalizedMail Mail() => new()
    {
        Subject = "件名",
        BodyText = "本文",
        From = new MailAddressList(["a@example.invalid"], ["Alice"]),
        To = new MailAddressList(["b@example.invalid"], [""]),
        MessageId = "id@example.invalid",
        SentAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
        FetchedAt = new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero),
        ReceivedAt = new DateTimeOffset(2026, 1, 2, 4, 0, 0, TimeSpan.Zero),
        AttachmentCount = 1,
        AttachmentNames = ["a.txt"],
    };

    private static string Field(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return $"01{bytes.Length:x8}{Convert.ToHexStringLower(bytes)}";
    }

    [Theory]
    [InlineData("<id@example.invalid>", "id@example.invalid")]
    [InlineData("  <id@example.invalid>\t", "id@example.invalid")]
    [InlineData("id@example.invalid", "id@example.invalid")]
    [InlineData("<ID@Example.invalid>", "ID@Example.invalid")]
    public void Message_ID_は外側の括弧と空白だけ正規化し_大小文字は保つ(string raw, string expected)
    {
        Assert.True(MessageIdNormalizer.TryNormalize(raw, out var id));
        Assert.Equal(expected, id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<>")]
    [InlineData("<a@x> <b@x>")]
    [InlineData("<a@x><b@x>")]
    [InlineData("<a@x\n>")]
    [InlineData("<a b@x>")]
    [InlineData("<a\0@x>")]
    public void 不正な_Message_ID_は拒否する(string? raw)
    {
        Assert.False(MessageIdNormalizer.TryNormalize(raw, out var id));
        Assert.Equal(string.Empty, id);
    }

    [Fact]
    public void Message_ID_の長さ上限を超えたら拒否する()
    {
        Assert.True(MessageIdNormalizer.TryNormalize(new string('a', MessageIdNormalizer.MaxLength), out _));
        Assert.False(MessageIdNormalizer.TryNormalize(new string('a', MessageIdNormalizer.MaxLength + 1), out _));
    }

    [Fact]
    public void 外部キーは長さ付き符号化の_SHA256_小文字64桁で_独立に計算した値と一致する()
    {
        var expectedInput = Field(ExternalKey.Version) + Field("dest") + Field("12345") + Field("src") + Field("id@x");
        var expected = Convert.ToHexStringLower(SHA256.HashData(Convert.FromHexString(expectedInput)));

        var key = ExternalKey.Create("dest", 12345, "src", "id@x");

        Assert.Equal(expected, key);
        Assert.Equal(64, key.Length);
        Assert.Matches("^[0-9a-f]{64}$", key);
    }

    [Fact]
    public void 外部キーは各フィールドの境界を移しても衝突しない()
    {
        var a = ExternalKey.Create("d", 1, "ab", "c");
        var b = ExternalKey.Create("d", 1, "a", "bc");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void 外部キーはソース_サイト_接続先が違えば別になる()
    {
        var baseKey = ExternalKey.Create("d", 1, "s", "m");

        Assert.NotEqual(baseKey, ExternalKey.Create("d", 1, "s2", "m"));
        Assert.NotEqual(baseKey, ExternalKey.Create("d", 2, "s", "m"));
        Assert.NotEqual(baseKey, ExternalKey.Create("d2", 1, "s", "m"));
        Assert.Equal(baseKey, ExternalKey.Create("d", 1, "s", "m"));
    }

    [Fact]
    public void 内容ハッシュは処理の都合で変わる値を含めない()
    {
        var baseHash = ContentHash.Compute(Mail());

        Assert.Equal(baseHash, ContentHash.Compute(Mail() with
        {
            FetchedAt = DateTimeOffset.UnixEpoch,
            ReceivedAt = null,
        }));
        Assert.Equal(
            baseHash,
            ContentHash.Compute(Mail() with { SentAt = new DateTimeOffset(2026, 1, 2, 12, 4, 5, TimeSpan.FromHours(9)) }));
    }

    [Fact]
    public void 内容ハッシュは内容の変化を検出する()
    {
        var baseHash = ContentHash.Compute(Mail());

        Assert.NotEqual(baseHash, ContentHash.Compute(Mail() with { Subject = "別" }));
        Assert.NotEqual(baseHash, ContentHash.Compute(Mail() with { BodyText = "別" }));
        Assert.NotEqual(baseHash, ContentHash.Compute(Mail() with { MessageId = "other@example.invalid" }));
        Assert.NotEqual(baseHash, ContentHash.Compute(Mail() with { SentAt = null }));
        Assert.NotEqual(
            baseHash,
            ContentHash.Compute(Mail() with { To = new MailAddressList(["c@example.invalid"], [""]) }));
        Assert.NotEqual(baseHash, ContentHash.Compute(Mail() with { AttachmentNames = ["b.txt"] }));
    }

    [Fact]
    public void 同じ_Message_ID_で添付の中身だけが違うメールを区別する()
    {
        var one = ContentHash.Compute(Mail(), ["aaaa"]);
        var two = ContentHash.Compute(Mail(), ["bbbb"]);

        Assert.NotEqual(one, two);
        Assert.NotEqual(one, ContentHash.Compute(Mail()));
    }

    [Fact]
    public void 内容ハッシュはアドレスと名前の境界や順序を区別する()
    {
        var a = ContentHash.Compute(Mail() with { From = new MailAddressList(["a", "b"], ["", ""]) });
        var b = ContentHash.Compute(Mail() with { From = new MailAddressList(["b", "a"], ["", ""]) });
        var c = ContentHash.Compute(Mail() with { From = new MailAddressList(["a"], ["b"]) });

        Assert.Equal(3, new[] { a, b, c }.Distinct().Count());
    }

    [Fact]
    public void 投射ハッシュは列順に依存せず_除外列を無視し_版と値で変わる()
    {
        var values = new Dictionary<string, object> { ["Title"] = "t", ["NumA"] = 1.0m, ["Status"] = 100L };
        var reordered = new Dictionary<string, object> { ["Status"] = 100L, ["NumA"] = 1m, ["Title"] = "t" };
        var withReserved = new Dictionary<string, object>(values) { ["ClassZ"] = "key", ["ClassY"] = "hash" };

        var hash = ProjectionHash.Compute(values, 1, Excluded);

        Assert.Equal(hash, ProjectionHash.Compute(reordered, 1, Excluded));
        Assert.Equal(hash, ProjectionHash.Compute(withReserved, 1, Excluded));
        Assert.NotEqual(hash, ProjectionHash.Compute(values, 2, Excluded));
        Assert.NotEqual(hash, ProjectionHash.Compute(new Dictionary<string, object>(values) { ["Title"] = "u" }, 1, Excluded));
        Assert.NotEqual(hash, ProjectionHash.Compute(new Dictionary<string, object>(values) { ["NumA"] = 2m }, 1, Excluded));
        Assert.Matches("^[0-9a-f]{64}$", hash);
    }

    [Fact]
    public void 投射ハッシュは型の違う同じ見た目の値を区別する()
    {
        var text = ProjectionHash.Compute(new Dictionary<string, object> { ["NumA"] = "1" }, 1, Excluded);
        var number = ProjectionHash.Compute(new Dictionary<string, object> { ["NumA"] = 1m }, 1, Excluded);

        Assert.NotEqual(text, number);
    }

    [Fact]
    public void 投射ハッシュの日時は時差が違っても同じ時刻なら同じ()
    {
        var utc = new Dictionary<string, object> { ["DateA"] = new DateTimeOffset(2026, 1, 2, 3, 0, 0, TimeSpan.Zero) };
        var jst = new Dictionary<string, object> { ["DateA"] = new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.FromHours(9)) };

        Assert.Equal(ProjectionHash.Compute(utc, 1, Excluded), ProjectionHash.Compute(jst, 1, Excluded));
    }

    [Fact]
    public void 投射ハッシュは対応外の型を拒否する()
    {
        Assert.Throws<ArgumentException>(
            () => ProjectionHash.Compute(new Dictionary<string, object> { ["NumA"] = 1.5 }, 1, Excluded));
    }

    [Fact]
    public void 重複の判定は新規_skip_競合に分かれる()
    {
        Assert.Equal(DuplicateOutcome.New, DuplicateJudge.Judge(null, "h1"));
        Assert.Equal(DuplicateOutcome.Skip, DuplicateJudge.Judge("h1", "h1"));
        Assert.Equal(DuplicateOutcome.Conflict, DuplicateJudge.Judge("h1", "h2"));
    }
}
