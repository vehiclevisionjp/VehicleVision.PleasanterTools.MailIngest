using VehicleVision.PleasanterTools.MailIngest.Core.Mapping;

namespace VehicleVision.PleasanterTools.MailIngest.Core.Tests;

public sealed class MappingPlanTests
{
    private static readonly string[] Reserved = ["ClassZ", "ClassY"];

    private static readonly MappingContext Context = new()
    {
        TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo"),
    };

    private static NormalizedMail Mail() => new()
    {
        Subject = "問い合わせ",
        BodyText = "本文1行目\n本文2行目",
        From = new MailAddressList(["a@example.invalid", "b@example.invalid"], ["Alice", ""]),
        To = new MailAddressList(["to@example.invalid"], [""]),
        MessageId = "<id@example.invalid>",
        SentAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
        FetchedAt = new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero),
        AttachmentCount = 2,
        AttachmentNames = ["a.txt", "b.pdf"],
    };

    private static MappingPlan Compile(
        IReadOnlyList<MappingRule> rules,
        IReadOnlyDictionary<string, object>? fixedValues = null)
    {
        var (plan, errors) = MappingPlan.Compile(rules, fixedValues, Reserved);
        Assert.True(plan is not null, string.Join(";", errors.Select(e => e.Code)));
        return plan;
    }

    private static string CompileError(
        IReadOnlyList<MappingRule> rules,
        IReadOnlyDictionary<string, object>? fixedValues = null)
    {
        var (plan, errors) = MappingPlan.Compile(rules, fixedValues, Reserved);
        Assert.Null(plan);
        return Assert.Single(errors).Code;
    }

    [Fact]
    public void 設計文書の例を評価して型付きの値を得る()
    {
        var plan = Compile(
            [
                new("subject", "Title", "件名なし"),
                new("bodyText", "Body"),
                new("from.addresses", "ClassA", Join: "; "),
                new("from.names", "DescriptionA", Join: "; "),
                new("sentAt", "DateA"),
                new("attachmentCount", "NumA"),
                new("attachmentNames", "DescriptionD", Join: "\n"),
            ],
            new Dictionary<string, object> { ["ClassB"] = "メール", ["Status"] = 100m });

        var result = plan.Evaluate(Mail(), Context);

        Assert.True(result.IsSuccess);
        var values = result.Values!;
        Assert.Equal("問い合わせ", values["Title"]);
        Assert.Equal("本文1行目\n本文2行目", values["Body"]);
        Assert.Equal("a@example.invalid; b@example.invalid", values["ClassA"]);
        Assert.Equal("Alice; ", values["DescriptionA"]);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), values["DateA"]);
        Assert.Equal(2m, values["NumA"]);
        Assert.Equal("a.txt\nb.pdf", values["DescriptionD"]);
        Assert.Equal("メール", values["ClassB"]);
        Assert.Equal(100L, values["Status"]);
    }

    [Fact]
    public void API_レコードはハッシュへまとめ_日時は設定のタイムゾーンへ変換し_予約列を足す()
    {
        var plan = Compile(
            [
                new("subject", "Title"),
                new("from.addresses", "ClassA", Join: "; "),
                new("sentAt", "DateA"),
                new("attachmentCount", "NumA"),
            ],
            new Dictionary<string, object> { ["CheckA"] = true, ["Status"] = 100m });

        var record = plan.Evaluate(Mail(), Context).ToApiRecord(
            Context,
            new Dictionary<string, string> { ["ClassZ"] = "key", ["ClassY"] = "hash" });

        Assert.Equal("問い合わせ", record["Title"]);
        Assert.Equal(100L, record["Status"]);
        var classHash = Assert.IsType<Dictionary<string, object?>>(record["ClassHash"]);
        Assert.Equal("a@example.invalid; b@example.invalid", classHash["ClassA"]);
        Assert.Equal("key", classHash["ClassZ"]);
        Assert.Equal("hash", classHash["ClassY"]);
        var dateHash = Assert.IsType<Dictionary<string, object?>>(record["DateHash"]);
        Assert.Equal("2026-01-02T12:04:05", dateHash["DateA"]); // UTC 03:04:05 → JST 12:04:05
        Assert.Equal(2m, Assert.IsType<Dictionary<string, object?>>(record["NumHash"])["NumA"]);
        Assert.Equal(true, Assert.IsType<Dictionary<string, object?>>(record["CheckHash"])["CheckA"]);
        Assert.DoesNotContain("ApiKey", record.Keys);
        Assert.DoesNotContain("SiteId", record.Keys);
    }

    [Fact]
    public void 欠落と空は既定値になり_既定も無ければ列を送らない()
    {
        var plan = Compile(
            [
                new("subject", "Title", "件名なし"),
                new("bodyText", "Body"),
                new("sentAt", "DateA"),
                new("to.addresses", "ClassA", Join: "; "),
            ]);
        var mail = Mail() with
        {
            Subject = "",
            BodyText = null,
            SentAt = null,
            To = MailAddressList.Empty,
        };

        var values = plan.Evaluate(mail, Context).Values!;

        Assert.Equal("件名なし", values["Title"]);
        Assert.DoesNotContain("Body", values.Keys);
        Assert.DoesNotContain("DateA", values.Keys);
        Assert.DoesNotContain("ClassA", values.Keys);
    }

    [Fact]
    public void 日時が欠落でも処理時刻へ置換しない()
    {
        var plan = Compile([new("sentAt", "DateA")]);

        var values = plan.Evaluate(Mail() with { SentAt = null }, Context).Values!;

        Assert.Empty(values);
    }

    [Fact]
    public void 長すぎる値は切り捨てずメール全体を失敗にする()
    {
        var plan = Compile([new("subject", "Title"), new("bodyText", "Body")]);
        var context = new MappingContext
        {
            TimeZone = Context.TimeZone,
            MaxLengths = new Dictionary<string, int> { ["Title"] = 3 },
        };

        var result = plan.Evaluate(Mail(), context);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Values);
        var error = Assert.Single(result.Errors);
        Assert.Equal("TooLong", error.Code);
        Assert.DoesNotContain("問い合わせ", error.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => result.ToApiRecord(context));
    }

    [Fact]
    public void 変換できない列が1つでもあれば部分送信せず全体が失敗する()
    {
        var plan = Compile([new("bodyText", "Body"), new("subject", "NumA")]);

        var result = plan.Evaluate(Mail(), Context);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Values);
        Assert.Equal("ConversionFailed", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void 件名を数値列へ変換できれば不変条件の数値になる()
    {
        var plan = Compile([new("subject", "NumA"), new("messageId", "CheckA")]);

        var values = plan.Evaluate(Mail() with { Subject = "12.5", MessageId = "True" }, Context).Values!;

        Assert.Equal(12.5m, values["NumA"]);
        Assert.Equal(true, values["CheckA"]);
    }

    [Theory]
    [InlineData("件名\r\n改行")]
    [InlineData("件名\0")]
    public void 単一行の項目の改行と_NUL_は拒否する(string subject)
    {
        var plan = Compile([new("subject", "Title")]);

        var result = plan.Evaluate(Mail() with { Subject = subject }, Context);

        Assert.Equal("ControlCharacter", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void 本文の改行は維持し_NUL_だけ拒否する()
    {
        var plan = Compile([new("bodyText", "Body")]);

        Assert.True(plan.Evaluate(Mail(), Context).IsSuccess);
        Assert.False(plan.Evaluate(Mail() with { BodyText = "a\0b" }, Context).IsSuccess);
    }

    [Fact]
    public void アドレス配列の要素にも改行を許さない()
    {
        var plan = Compile([new("from.addresses", "ClassA", Join: "; ")]);
        var mail = Mail() with { From = new MailAddressList(["a@example.invalid\nBcc: x"], [""]) };

        Assert.Equal("ControlCharacter", Assert.Single(plan.Evaluate(mail, Context).Errors).Code);
    }

    [Fact]
    public void 入力項目と出力列の検査は起動時エラーにする()
    {
        Assert.Equal("UnknownSource", CompileError([new("bcc", "Title")]));
        Assert.Equal("UnknownTarget", CompileError([new("subject", "Unknown")]));
        Assert.Equal("UnknownTarget", CompileError([new("subject", "ApiKey")]));
        Assert.Equal("UnknownTarget", CompileError([new("subject", "SiteId")]));
        Assert.Equal("UnknownTarget", CompileError([new("subject", "AttachmentsA")]));
        Assert.Equal("ReservedColumn", CompileError([new("subject", "ClassZ")]));
        Assert.Equal("FixedValueOnly", CompileError([new("subject", "Owner")]));
        Assert.Equal("DuplicateTarget", CompileError([new("subject", "Title"), new("messageId", "Title")]));
        Assert.Equal("JoinRequired", CompileError([new("from.addresses", "ClassA")]));
        Assert.Equal("JoinNotAllowed", CompileError([new("subject", "Title", Join: ",")]));
        Assert.Equal("TypeMismatch", CompileError([new("sentAt", "Title")]));
        Assert.Equal("TypeMismatch", CompileError([new("subject", "DateA")]));
        Assert.Equal("InvalidDefault", CompileError([new("subject", "NumA", "abc")]));
        Assert.Equal("InvalidDefault", CompileError([new("sentAt", "DateA", "2026-01-01T00:00:00")])); // 時差なしは拒否
    }

    [Fact]
    public void 固定値の検査()
    {
        Assert.Equal(
            "FixedValueConflict",
            CompileError([new("subject", "Title")], new Dictionary<string, object> { ["Title"] = "x" }));
        Assert.Equal("ReservedColumn", CompileError([], new Dictionary<string, object> { ["ClassY"] = "x" }));
        Assert.Equal("UnknownTarget", CompileError([], new Dictionary<string, object> { ["ApiVersion"] = "x" }));
        Assert.Equal("InvalidFixedValue", CompileError([], new Dictionary<string, object> { ["Status"] = "x" }));
        Assert.Equal("InvalidFixedValue", CompileError([], new Dictionary<string, object> { ["Status"] = 1.5m }));
        Assert.Equal("InvalidFixedValue", CompileError([], new Dictionary<string, object> { ["DateA"] = "2026-01-01" }));
    }

    [Fact]
    public void エラーにメールの内容を載せない()
    {
        var plan = Compile([new("subject", "NumA")]);

        var result = plan.Evaluate(Mail() with { Subject = "secret-subject" }, Context);

        Assert.All(result.Errors, e => Assert.DoesNotContain("secret", e.Message + e.Location, StringComparison.Ordinal));
    }
}
