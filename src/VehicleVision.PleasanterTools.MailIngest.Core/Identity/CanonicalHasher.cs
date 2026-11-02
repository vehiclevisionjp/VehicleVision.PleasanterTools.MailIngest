using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace VehicleVision.PleasanterTools.MailIngest.Core.Identity;

/// <summary>
/// 曖昧さのない符号化で SHA-256 を計算する。各フィールドは「種別 1 バイト + 長さ 4 バイト（ビッグエンディアン）+ UTF-8」。
/// </summary>
/// <remarks>文字列の単純な連結は、境界の移動で同じ入力になるため使わない。</remarks>
internal sealed class CanonicalHasher : IDisposable
{
    private const byte StringTag = 1;
    private const byte NullTag = 2;
    private const byte CountTag = 3;

    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    public CanonicalHasher(string algorithmVersion)
    {
        Add(algorithmVersion);
    }

    public void Add(string? value)
    {
        if (value is null)
        {
            _hash.AppendData([NullTag, 0, 0, 0, 0]);
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> header = stackalloc byte[5];
        header[0] = StringTag;
        BinaryPrimitives.WriteInt32BigEndian(header[1..], bytes.Length);
        _hash.AppendData(header);
        _hash.AppendData(bytes);
    }

    public void AddCount(int count)
    {
        Span<byte> header = stackalloc byte[5];
        header[0] = CountTag;
        BinaryPrimitives.WriteInt32BigEndian(header[1..], count);
        _hash.AppendData(header);
    }

    public void AddList(IReadOnlyList<string> values)
    {
        AddCount(values.Count);
        foreach (var value in values)
        {
            Add(value);
        }
    }

    /// <summary>SHA-256 を小文字 16 進 64 桁で返す。</summary>
    public string Finish() => Convert.ToHexStringLower(_hash.GetHashAndReset());

    public void Dispose() => _hash.Dispose();
}
