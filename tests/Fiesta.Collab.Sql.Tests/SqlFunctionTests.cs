using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Fiesta.Collab.Sql.Tests;

/// <summary>
/// The functions every SqlEngine connection carries, so a migration can express what the Python generated steps
/// compute today (docs/DESIGN-variants-and-native-steps.md, section 2).
/// </summary>
public class SqlFunctionTests : IDisposable
{
    private readonly SqlEngine _engine = new(NullLogger<SqlEngine>.Instance);

    public void Dispose() => _engine.Dispose();

    private object? Scalar(string expr) => _engine.Query($"SELECT {expr} AS v")[0]["v"];

    private double Num(string expr) => Convert.ToDouble(Scalar(expr));

    [Fact]
    public void Ln_exp_pow()
    {
        Num("ln(exp(2.5))").ShouldBe(2.5, 1e-12);
        Num("pow(2, 10)").ShouldBe(1024);
        Scalar("ln(0)").ShouldBeNull();
        Scalar("ln(NULL)").ShouldBeNull();
    }

    [Fact]
    public void Median_of_odd_and_even_counts_ignores_nulls()
    {
        _engine.Execute("CREATE TABLE t (x REAL); INSERT INTO t VALUES (5), (1), (NULL), (3);");
        Convert.ToDouble(_engine.Query("SELECT median(x) AS v FROM t")[0]["v"]).ShouldBe(3);
        _engine.Execute("INSERT INTO t VALUES (10);");
        Convert.ToDouble(_engine.Query("SELECT median(x) AS v FROM t")[0]["v"]).ShouldBe(4);
        _engine.Query("SELECT median(x) AS v FROM t WHERE x > 100")[0]["v"].ShouldBeNull();
    }

    [Fact]
    public void Interp_log_is_log_linear_between_anchors_and_flat_outside()
    {
        const string pts = "'100:0.20,60:0.40'";                         // anchor order does not matter
        Num($"interp_log(10, {pts})").ShouldBe(0.40, 1e-12);
        Num($"interp_log(200, {pts})").ShouldBe(0.20, 1e-12);
        Num($"interp_log(80, {pts})").ShouldBe(Math.Sqrt(0.40 * 0.20), 1e-12);   // halfway = geometric mean
        Num($"interp_log(60, {pts})").ShouldBe(0.40, 1e-12);
    }

    [Fact]
    public void Blob_reads_are_little_endian_over_hex()
    {
        const string hex = "'0102030405060708'";
        Convert.ToInt64(Scalar($"blob_u8({hex}, 0)")).ShouldBe(1);
        Convert.ToInt64(Scalar($"blob_u16({hex}, 1)")).ShouldBe(0x0302);
        Convert.ToInt64(Scalar($"blob_u32({hex}, 4)")).ShouldBe(0x08070605);
        Scalar($"blob_u32({hex}, 6)").ShouldBeNull();                    // past the end
    }

    [Fact]
    public void Blob_set_returns_the_edited_hex_and_keeps_the_rest()
    {
        Scalar("blob_set_u16('0102030405', 1, 4660)").ShouldBe("0134120405");    // 0x1234
        Scalar("blob_set_u32('00000000', 0, 4294967295)").ShouldBe("FFFFFFFF");
        Scalar("blob_set_u8('abcd', 1, 1)").ShouldBe("AB01");                    // uppercase, as the tables store it
        Scalar("blob_set_u8('ab', 1, 1)").ShouldBeNull();                         // past the end
    }
}
