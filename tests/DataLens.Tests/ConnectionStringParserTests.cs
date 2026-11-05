using DataLens.Infrastructure.Postgres;
using Npgsql;

namespace DataLens.Tests;

public class ConnectionStringParserTests
{
    [Fact]
    public void Reads_key_value_form()
    {
        var b = ConnectionStringParser.Parse("Host=db.example.com;Port=5433;Database=shop;Username=ro;Password=secret");
        Assert.Equal("db.example.com", b.Host);
        Assert.Equal(5433, b.Port);
        Assert.Equal("shop", b.Database);
        Assert.Equal("ro", b.Username);
        Assert.Equal("secret", b.Password);
    }

    [Theory]
    [InlineData("postgresql://ro:secret@db.example.com:5433/shop")]
    [InlineData("postgres://ro:secret@db.example.com:5433/shop")]
    public void Reads_uri_form(string uri)
    {
        var b = ConnectionStringParser.Parse(uri);
        Assert.Equal("db.example.com", b.Host);
        Assert.Equal(5433, b.Port);
        Assert.Equal("shop", b.Database);
        Assert.Equal("ro", b.Username);
        Assert.Equal("secret", b.Password);
    }

    [Fact]
    public void Uri_defaults_to_port_5432_and_decodes_the_password()
    {
        var b = ConnectionStringParser.Parse("postgresql://ro:p%40ss%3Aword@localhost/shop");
        Assert.Equal(5432, b.Port);
        Assert.Equal("p@ss:word", b.Password);
    }

    [Fact]
    public void Uri_sslmode_is_applied()
    {
        var b = ConnectionStringParser.Parse("postgresql://ro:x@localhost/shop?sslmode=require");
        Assert.Equal(SslMode.Require, b.SslMode);
    }

    [Fact]
    public void Connect_timeout_defaults_to_five_seconds_unless_given()
    {
        Assert.Equal(5, ConnectionStringParser.Parse("Host=h;Database=d;Username=u").Timeout);
        Assert.Equal(30, ConnectionStringParser.Parse("Host=h;Database=d;Username=u;Timeout=30").Timeout);
    }

    [Fact]
    public void Normalize_and_Mask_add_nothing_the_user_did_not_type()
    {
        var typed = "Host=h;Database=d;Username=u;Password=pw";
        Assert.DoesNotContain("Timeout", ConnectionStringParser.Normalize(typed).ConnectionString);
        Assert.DoesNotContain("Timeout", ConnectionStringParser.Mask(typed));
        Assert.Contains("Timeout=30", ConnectionStringParser.Mask(typed + ";Timeout=30"));
    }

    [Fact]
    public void Mask_hides_the_password()
    {
        var masked = ConnectionStringParser.Mask("Host=h;Database=d;Username=u;Password=hunter2");
        Assert.DoesNotContain("hunter2", masked);
        Assert.Contains("****", masked);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a connection string")]
    [InlineData("Host=localhost;Username=u")]   // no database
    [InlineData("Database=d;Username=u")]        // no host
    public void Rejects_bad_input_with_a_readable_error(string input)
    {
        Assert.Throws<FormatException>(() => ConnectionStringParser.Parse(input));
    }
}
