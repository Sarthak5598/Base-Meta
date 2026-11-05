using System.Security.Cryptography;
using System.Text;
using DataLens.Core;
using DataLens.Core.Models;
using DataLens.Infrastructure.Security;

namespace DataLens.Tests;

public class TypeMapperTests
{
    [Theory]
    [InlineData("integer", ColumnKind.Number)]
    [InlineData("numeric", ColumnKind.Number)]
    [InlineData("text", ColumnKind.Text)]
    [InlineData("character varying", ColumnKind.Text)]
    [InlineData("boolean", ColumnKind.Boolean)]
    [InlineData("date", ColumnKind.Date)]
    [InlineData("timestamp with time zone", ColumnKind.Timestamp)]
    [InlineData("jsonb", ColumnKind.Other)]
    [InlineData("ARRAY", ColumnKind.Other)]
    public void Maps_postgres_types(string raw, ColumnKind expected) =>
        Assert.Equal(expected, TypeMapper.Map(raw));
}

public class ConnectionProtectorTests
{
    private static ConnectionProtector NewProtector() => new(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public void Round_trips()
    {
        var p = NewProtector();
        Assert.Equal("Host=h;Password=pw", p.Unprotect(p.Protect("Host=h;Password=pw")));
    }

    [Fact]
    public void Output_does_not_contain_the_plaintext()
    {
        var protectedText = NewProtector().Protect("Host=h;Password=hunter2");
        Assert.DoesNotContain("hunter2", protectedText);
        Assert.DoesNotContain("hunter2", Encoding.UTF8.GetString(Convert.FromBase64String(protectedText)));
    }

    [Fact]
    public void Same_input_encrypts_differently_each_time()
    {
        var p = NewProtector();
        Assert.NotEqual(p.Protect("x"), p.Protect("x"));
    }

    [Fact]
    public void Wrong_key_fails()
    {
        var text = NewProtector().Protect("secret");
        Assert.ThrowsAny<CryptographicException>(() => NewProtector().Unprotect(text));
    }

    [Fact]
    public void Tampering_is_detected()
    {
        var p = NewProtector();
        var bytes = Convert.FromBase64String(p.Protect("secret"));
        bytes[^1] ^= 0x01;
        Assert.ThrowsAny<CryptographicException>(() => p.Unprotect(Convert.ToBase64String(bytes)));
    }

    [Fact]
    public void Rejects_a_key_of_the_wrong_size() =>
        Assert.Throws<ArgumentException>(() => new ConnectionProtector(new byte[16]));
}
