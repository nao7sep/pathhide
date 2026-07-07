using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using PathHide.Services;
using Xunit;

namespace PathHide.Tests.Services;

public sealed class LogRedactorTests
{
    private static readonly IReadOnlySet<string> Denied =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "token", "password", "secret", "apiKey" };

    private static JsonObject Obj(string json) => (JsonObject)JsonNode.Parse(json)!;

    [Theory]
    [InlineData("token")]
    [InlineData("Token")]
    [InlineData("TOKEN")]
    [InlineData("apikey")] // configured as apiKey; match is case-insensitive
    public void Redact_matches_a_denied_key_regardless_of_case(string key)
    {
        var obj = Obj($$"""{ "{{key}}": "sensitive" }""");

        LogRedactor.Redact(obj, Denied);

        Assert.Equal(LogRedactor.Marker, obj[key]!.GetValue<string>());
    }

    [Theory]
    [InlineData("tokenCount")]
    [InlineData("broken")]
    [InlineData("mytoken")]
    [InlineData("token_id")]
    public void Redact_never_matches_a_substring(string key)
    {
        var obj = Obj($$"""{ "{{key}}": "kept" }""");

        LogRedactor.Redact(obj, Denied);

        Assert.Equal("kept", obj[key]!.GetValue<string>());
    }

    [Fact]
    public void Redact_recurses_into_nested_objects()
    {
        var obj = Obj("""{ "outer": { "inner": { "password": "p" }, "keep": "k" } }""");

        LogRedactor.Redact(obj, Denied);

        Assert.Equal(LogRedactor.Marker, obj["outer"]!["inner"]!["password"]!.GetValue<string>());
        Assert.Equal("k", obj["outer"]!["keep"]!.GetValue<string>());
    }

    [Fact]
    public void Redact_recurses_through_arrays()
    {
        var obj = Obj("""{ "items": [ { "secret": "a" }, { "name": "b" } ] }""");

        LogRedactor.Redact(obj, Denied);

        Assert.Equal(LogRedactor.Marker, obj["items"]![0]!["secret"]!.GetValue<string>());
        Assert.Equal("b", obj["items"]![1]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void Redact_replaces_a_matched_value_of_any_type_with_the_marker()
    {
        // The value need not be a string: a number, bool, object, or array under a
        // denied key is replaced wholesale by the marker.
        var obj = Obj("""{ "password": 12345, "token": { "nested": true }, "secret": [1, 2] }""");

        LogRedactor.Redact(obj, Denied);

        Assert.Equal(LogRedactor.Marker, obj["password"]!.GetValue<string>());
        Assert.Equal(LogRedactor.Marker, obj["token"]!.GetValue<string>());
        Assert.Equal(LogRedactor.Marker, obj["secret"]!.GetValue<string>());
    }

    [Fact]
    public void Redact_leaves_non_matching_fields_byte_identical()
    {
        const string json = """{"a":1,"b":"x","arr":[1,2,{"c":true}],"obj":{"d":null}}""";
        var expected = JsonNode.Parse(json)!.ToJsonString();
        var obj = Obj(json);

        LogRedactor.Redact(obj, Denied);

        Assert.Equal(expected, obj.ToJsonString());
    }

    [Fact]
    public void Redact_is_a_no_op_on_null()
    {
        var ex = Record.Exception(() => LogRedactor.Redact(null, Denied));
        Assert.Null(ex);
    }

    [Fact]
    public void Redact_does_not_drop_sibling_fields_when_redacting()
    {
        var obj = Obj("""{ "password": "p", "user": "bob", "count": 3 }""");

        LogRedactor.Redact(obj, Denied);

        Assert.Equal(3, obj.Count);
        Assert.Equal(LogRedactor.Marker, obj["password"]!.GetValue<string>());
        Assert.Equal("bob", obj["user"]!.GetValue<string>());
        Assert.Equal(3, obj["count"]!.GetValue<int>());
    }
}
