using Shouldly;
using Xunit;

namespace Fiesta.Collab.ShineTable.Tests;

/// <summary>
/// NPC.txt declares `#delimiter \x20`, and the operator adds rows separated by spaces; the server reads
/// them, so the parser must too. Quoted fields stay whole (Script tables quote multi-word dialogue).
///
/// A parsed value keeps the form the FILE uses, quotes and all. #ignore and #exchange tell the loader
/// how to read a value; they do not mean the file omits the quoting. Decoding on read would leave a
/// writer unable to tell a quoted value from an unquoted one - and here it would be actively wrong,
/// because with space as a delimiter an unquoted Hello there is two fields, not one.
/// Preprocessor.Apply gives the value as the server finally sees it, for anything that wants that.
/// </summary>
public class DelimiterDirectiveTests
{
    private static readonly string[] Lines =
    [
        "#ignore\t\\o042",
        "#delimiter\t\\x20\t\t; Space is delimiter",
        "#Table\tShineNPC",
        "#ColumnType\tSTRING[33]\tSTRING[20]\tDWRD\tSTRING[64]",
        "#ColumnName\tMobName\tMap\tCoordX\tText",
        "#Record\tRouSmithJames\tRouN\t5645\t\"Hello there\"",
        "    #Record    Xiaoming    Eld    11683    \"Two words\"",
        "\t#Record\tMixed  Rou 7  Plain",
        "#End",
    ];

    [Fact]
    public void SpaceSeparatedRecords_AreParsed()
    {
        var tables = ShineTableFormatParser.Parse("NPC.txt", Lines);

        var rows = tables.Single().Rows;
        rows.Count.ShouldBe(3);
        rows[0]["MobName"].ShouldBe("RouSmithJames");
        rows[0]["Text"].ShouldBe("\"Hello there\"");   // as written, quotes included
        rows[1]["MobName"].ShouldBe("Xiaoming");
        rows[1]["Map"].ShouldBe("Eld");
        rows[1]["CoordX"].ShouldBe(11683);
        rows[1]["Text"].ShouldBe("\"Two words\"");
        rows[2]["MobName"].ShouldBe("Mixed");
        rows[2]["CoordX"].ShouldBe(7);
        rows[2]["Text"].ShouldBe("Plain");
    }

    [Fact]
    public void WithoutTheDirective_SpacesStayInsideFields()
    {
        var lines = Lines.Where(l => !l.StartsWith("#delimiter")).ToArray();

        var rows = ShineTableFormatParser.Parse("T.txt", lines).Single().Rows;

        rows.Count.ShouldBe(3);
        rows[2]["MobName"].ShouldBe("Mixed  Rou 7  Plain");
    }
}
