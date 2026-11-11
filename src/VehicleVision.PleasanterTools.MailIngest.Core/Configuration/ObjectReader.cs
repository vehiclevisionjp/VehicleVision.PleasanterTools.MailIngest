using System.Text.Json;

namespace VehicleVision.PleasanterTools.MailIngest.Core.Configuration;

/// <summary>JSON オブジェクトを、未知のプロパティ・重複・型違いを検出しながら読む。</summary>
internal sealed class ObjectReader
{
    private readonly JsonElement _element;
    private readonly ConfigIssues _issues;
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);
    private readonly HashSet<string> _consumed = new(StringComparer.Ordinal);

    private ObjectReader(JsonElement element, string path, ConfigIssues issues)
    {
        _element = element;
        Path = path;
        _issues = issues;
        foreach (var property in element.EnumerateObject())
        {
            if (!_names.Add(property.Name))
            {
                issues.Error(Join(property.Name), "DuplicateProperty", "The same property is duplicated.");
            }
        }
    }

    public string Path { get; }

    public static ObjectReader? Create(JsonElement element, string path, ConfigIssues issues)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            issues.Error(path, "InvalidType", "An object is required.");
            return null;
        }

        return new ObjectReader(element, path, issues);
    }

    public string Join(string name) => Path.Length == 0 ? name : $"{Path}.{name}";

    public bool Has(string name) => _names.Contains(name);

    public JsonElement? Raw(string name)
    {
        _consumed.Add(name);
        return _element.TryGetProperty(name, out var value) ? value : null;
    }

    public ObjectReader? Object(string name, bool required)
    {
        var raw = Raw(name);
        if (raw is null)
        {
            if (required)
            {
                _issues.Error(Join(name), "Missing", "Required.");
            }

            return null;
        }

        return Create(raw.Value, Join(name), _issues);
    }

    public string? String(string name, bool required, string? defaultValue = null, bool allowNull = false)
    {
        var raw = Raw(name);
        if (raw is null || (allowNull && raw.Value.ValueKind == JsonValueKind.Null))
        {
            if (raw is null && required)
            {
                _issues.Error(Join(name), "Missing", "Required.");
            }

            return defaultValue;
        }

        if (raw.Value.ValueKind != JsonValueKind.String)
        {
            _issues.Error(Join(name), "InvalidType", "A string is required.");
            return defaultValue;
        }

        return raw.Value.GetString();
    }

    public int? Int(string name, bool required, int? defaultValue = null)
    {
        var raw = Raw(name);
        if (raw is null)
        {
            if (required)
            {
                _issues.Error(Join(name), "Missing", "Required.");
            }

            return defaultValue;
        }

        if (raw.Value.ValueKind != JsonValueKind.Number || !raw.Value.TryGetInt32(out var value))
        {
            _issues.Error(Join(name), "InvalidType", "An integer is required.");
            return defaultValue;
        }

        return value;
    }

    public long? Long(string name, bool required, long? defaultValue = null)
    {
        var raw = Raw(name);
        if (raw is null)
        {
            if (required)
            {
                _issues.Error(Join(name), "Missing", "Required.");
            }

            return defaultValue;
        }

        if (raw.Value.ValueKind != JsonValueKind.Number || !raw.Value.TryGetInt64(out var value))
        {
            _issues.Error(Join(name), "InvalidType", "An integer is required.");
            return defaultValue;
        }

        return value;
    }

    public decimal? Decimal(string name, bool required, decimal? defaultValue = null)
    {
        var raw = Raw(name);
        if (raw is null)
        {
            if (required)
            {
                _issues.Error(Join(name), "Missing", "Required.");
            }

            return defaultValue;
        }

        if (raw.Value.ValueKind != JsonValueKind.Number || !raw.Value.TryGetDecimal(out var value))
        {
            _issues.Error(Join(name), "InvalidType", "A number is required.");
            return defaultValue;
        }

        return value;
    }

    public bool Bool(string name, bool defaultValue)
    {
        var raw = Raw(name);
        if (raw is null)
        {
            return defaultValue;
        }

        if (raw.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            _issues.Error(Join(name), "InvalidType", "true or false is required.");
            return defaultValue;
        }

        return raw.Value.GetBoolean();
    }

    /// <summary>消費しなかったプロパティを未知として報告する。</summary>
    public void Finish()
    {
        foreach (var name in _names)
        {
            if (!_consumed.Contains(name))
            {
                _issues.Error(Join(name), "UnknownProperty", "Unknown property.");
            }
        }
    }
}
