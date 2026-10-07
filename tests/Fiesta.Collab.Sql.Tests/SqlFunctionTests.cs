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
        Convert.ToInt64(Scalar($"blob_u64({hex}, 0)")).ShouldBe(0x0807060504030201);
        Scalar("blob_set_u64('00000000000000000000', 1, 2147483648)").ShouldBe("00000000800000000000");   // a QuestData reward value
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
    public void Regexp_split_join_drops_the_matching_parts()
    {
        // a tooltip's sentences without the ones that state a stat (Python: ' '.join(p for p in re.split(s, x) if not re.search(d, p)))
        Scalar(@"regexp_split_join('Adds 3% crit. A cute hat! Very red.', '(?<=[.!?])\s+', '(?i)\d+\s*%|crit', ' ')")
            .ShouldBe("A cute hat! Very red.");
        Scalar(@"regexp_split_join('a,b,,c', ',', NULL, '-')").ShouldBe("a-b--c");                 // NULL drop keeps every part
        Scalar(@"regexp_split_join('x1 y z2', ' ', '\d', ' ')").ShouldBe("y");
        Scalar(@"regexp_split_join('x1', ' ', '\d', ' ')").ShouldBe("");                          // every part dropped
        Scalar(@"regexp_split_join(NULL, ' ', NULL, ' ')").ShouldBeNull();
    }

    [Fact]
    public void Regexp_matches_gives_character_positions_substr_agrees_with()
    {
        // an emoji (two UTF-16 units) before the match: positions count it once, as SQLite does
        Scalar(@"regexp_matches('😀 kill 40 Stonies', '(\d+) (\w+)')")
            .ShouldBe(@"[{""s"":7,""e"":17,""m"":""40 Stonies"",""g"":[""40"",""Stonies""]}]");
        Scalar(@"(SELECT substr('😀 kill 40 Stonies', json_extract(value, '$.s') + 1, json_extract(value, '$.e') - json_extract(value, '$.s'))
                  FROM json_each(regexp_matches('😀 kill 40 Stonies', '\d+')))").ShouldBe("40");
        Scalar(@"regexp_matches('none', '\d')").ShouldBe("[]");
        Scalar(@"regexp_matches('a1', '(x)?(\d)')").ShouldBe(@"[{""s"":1,""e"":2,""m"":""1"",""g"":[null,""1""]}]");
    }

    [Fact]
    public void Text_splice_puts_a_decision_per_match_back()
    {
        // Python: re.sub(r'\d+', lambda m: str(int(m.group(0)) * 2) if int(m.group(0)) > 5 else m.group(0), x)
        Scalar(@"text_splice('😀 3 and 40 and 7', (SELECT json_group_array(json_array(json_extract(value, '$.s'), json_extract(value, '$.e'),
                    CAST(json_extract(value, '$.m') AS INTEGER) * 2))
                  FROM json_each(regexp_matches('😀 3 and 40 and 7', '\d+')) WHERE CAST(json_extract(value, '$.m') AS INTEGER) > 5))")
            .ShouldBe("😀 3 and 80 and 14");
        Scalar(@"text_splice('abc', '[[2, 3, ""Z""], [0, 1, ""X""]]')").ShouldBe("XbZ");      // any order
        Scalar(@"text_splice('abc', '[]')").ShouldBe("abc");
        Scalar(@"text_splice('abc', NULL)").ShouldBe("abc");
        Should.Throw<Exception>(() => Scalar(@"text_splice('abc', '[[0, 2, ""x""], [1, 3, ""y""]]')"));   // overlapping
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
