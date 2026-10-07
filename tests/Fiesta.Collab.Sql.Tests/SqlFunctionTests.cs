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
    public void Blob_compact_removes_records_and_moves_the_rest_up()
    {
        // header byte, then four 2-byte records AA01 BB02 CC03 DD04, then a trailer byte
        Scalar("blob_compact('FFAA01BB02CC03DD04EE', 1, 2, 4, '1,2')").ShouldBe("FFAA01DD0400000000EE");
        Scalar("blob_compact('FFAA01BB02CC03DD04EE', 1, 2, 4, '')").ShouldBe("FFAA01BB02CC03DD04EE");
        Scalar("blob_compact('ffaa01', 1, 2, 1, NULL)").ShouldBe("FFAA01");
        Scalar("blob_compact('FFAA01', 1, 2, 4, '0')").ShouldBeNull();       // past the end
    }

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
    public void Regexp_operator_matches_anywhere_and_is_case_sensitive()
    {
        _engine.Execute("CREATE TABLE n (s TEXT); INSERT INTO n VALUES ('HP Potion (Tier 7)'), ('Wood'), (NULL);");
        _engine.Query(@"SELECT s FROM n WHERE s REGEXP '\(Tier \d+\)$'").Count.ShouldBe(1);
        _engine.Query("SELECT s FROM n WHERE s REGEXP 'wood'").Count.ShouldBe(0);
        _engine.Query("SELECT s FROM n WHERE s REGEXP '(?i)wood'").Count.ShouldBe(1);
    }

    [Fact]
    public void Regexp_group_returns_a_capture_or_null()
    {
        const string rx = @"'^(.*?)\s*\(\s*Tier\s*(\d+)\s*\)\s*$'";
        Scalar($"regexp_group('HP Potion (Tier 7)', {rx}, 1)").ShouldBe("HP Potion");
        Scalar($"regexp_group('HP Potion (Tier 7)', {rx}, 2)").ShouldBe("7");
        Scalar($"regexp_group('Wood', {rx}, 1)").ShouldBeNull();             // no match
        Scalar($"regexp_group(NULL, {rx}, 1)").ShouldBeNull();
    }

    [Fact]
    public void Regexp_replace_all_or_the_first_n()
    {
        Scalar(@"regexp_replace('SEclipseBow', '^S[Ee]clipse ?(?=[A-Z])', 'Solar Eclipse ')").ShouldBe("Solar Eclipse Bow");
        Scalar(@"regexp_replace('Hat [Quest Item]', '\s*\[Quest Item\]$', '')").ShouldBe("Hat");
        Scalar("regexp_replace('a-a-a', 'a', 'b')").ShouldBe("b-b-b");               // every match, like re.sub
        Scalar("regexp_replace('a-a-a', 'a', 'b', 1)").ShouldBe("b-a-a");            // count, like re.sub(count=1)
        Scalar("regexp_replace('x12', '(\\d)(\\d)', '$2$1')").ShouldBe("x21");       // .NET replacement syntax
        Scalar("regexp_replace(NULL, 'a', 'b')").ShouldBeNull();
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
