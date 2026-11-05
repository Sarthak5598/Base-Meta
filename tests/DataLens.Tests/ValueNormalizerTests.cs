using System.Text.Json;
using DataLens.Infrastructure.Postgres;

namespace DataLens.Tests;

public class ValueNormalizerTests
{
    [Fact]
    public void Null_and_dbnull_become_null()
    {
        Assert.Null(ValueNormalizer.Normalize(null));
        Assert.Null(ValueNormalizer.Normalize(DBNull.Value));
    }

    [Fact]
    public void Plain_values_pass_through()
    {
        Assert.Equal(5, ValueNormalizer.Normalize(5));
        Assert.Equal(12.5m, ValueNormalizer.Normalize(12.5m));
        Assert.Equal(true, ValueNormalizer.Normalize(true));
        Assert.Equal("hi", ValueNormalizer.Normalize("hi"));
    }

    [Fact]
    public void Integers_beyond_what_javascript_can_hold_become_strings()
    {
        Assert.Equal(9_007_199_254_740_991L, ValueNormalizer.Normalize(9_007_199_254_740_991L));
        Assert.Equal("9007199254740993", ValueNormalizer.Normalize(9_007_199_254_740_993L));
        Assert.Equal("-9223372036854775808", ValueNormalizer.Normalize(long.MinValue));
    }

    [Fact]
    public void Nan_and_infinity_become_strings_because_json_cannot_hold_them()
    {
        Assert.Equal("NaN", ValueNormalizer.Normalize(double.NaN));
        Assert.Equal("Infinity", ValueNormalizer.Normalize(double.PositiveInfinity));
        Assert.Equal("NaN", ValueNormalizer.Normalize(float.NaN));
        Assert.Equal(1.5, ValueNormalizer.Normalize(1.5));
    }

    [Fact]
    public void Dates_and_times_become_iso_strings()
    {
        Assert.Equal("2026-03-01T10:00:00.0000000Z",
            ValueNormalizer.Normalize(new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc)));
        Assert.Equal("2026-03-01T10:00:00.0000000+02:00",
            ValueNormalizer.Normalize(new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.FromHours(2))));
        Assert.Equal("2026-03-01", ValueNormalizer.Normalize(new DateOnly(2026, 3, 1)));
    }

    [Fact]
    public void Guids_become_strings()
    {
        var g = Guid.NewGuid();
        Assert.Equal(g.ToString(), ValueNormalizer.Normalize(g));
    }

    [Fact]
    public void Bytes_become_a_short_hex_string()
    {
        Assert.Equal(@"\xdeadbeef", ValueNormalizer.Normalize(new byte[] { 0xde, 0xad, 0xbe, 0xef }));
        var big = (string)ValueNormalizer.Normalize(new byte[1000])!;
        Assert.EndsWith("…", big);
        Assert.True(big.Length < 100);
    }

    [Fact]
    public void Arrays_become_lists_of_normalized_values()
    {
        var list = Assert.IsType<List<object?>>(ValueNormalizer.Normalize(new[] { 1, 2, 3 }));
        Assert.Equal(new object?[] { 1, 2, 3 }, list);
    }

    [Fact]
    public void Very_long_text_is_cut()
    {
        var cut = (string)ValueNormalizer.Normalize(new string('x', ValueNormalizer.MaxTextLength + 500))!;
        Assert.Equal(ValueNormalizer.MaxTextLength + 1, cut.Length);
        Assert.EndsWith("…", cut);
    }

    [Fact]
    public void Everything_it_returns_serialises_to_json()
    {
        var values = new object?[]
        {
            1, "a", 2.5m, double.NaN, long.MaxValue, DateTime.UtcNow, Guid.NewGuid(),
            new byte[] { 1 }, new[] { 1, 2 }, new DateOnly(2026, 1, 1)
        };
        var row = values.Select(ValueNormalizer.Normalize).ToArray();
        JsonSerializer.Serialize(row);   // would throw on NaN or an unsupported type
    }
}
