using Mimir.Core.Constraints;
using Mimir.Core.Project;
using Shouldly;
using Xunit;

namespace Mimir.Core.Tests;

public class DefinitionResolverTests
{
    [Theory]
    [InlineData("data/shinetable/Shine/NPCItemList/Tab00.json", "data/shinetable/Shine/NPCItemList/**", true)]
    [InlineData("data/shinetable/Shine/NPCItemList/Sub/Tab00.json", "data/shinetable/Shine/NPCItemList/**", true)]
    [InlineData("data/shn/Shine/ItemInfo.json", "data/shinetable/Shine/NPCItemList/**", false)]
    [InlineData("MobRegen_Group1", "MobRegen_*", true)]
    [InlineData("ItemInfo", "Item*", true)]
    [InlineData("ItemInfo", "Mob*", false)]
    public void GlobMatch_MatchesCorrectly(string input, string pattern, bool expected)
    {
        DefinitionResolver.GlobMatch(input, pattern).ShouldBe(expected);
    }

    [Fact]
    public void Resolve_MatchesByPath()
    {
        var definitions = new ProjectDefinitions
        {
            Tables = new()
            {
                ["ItemInfo"] = new TableKeyInfo { KeyColumn = "InxName" }
            },
            Constraints =
            [
                new ConstraintRule
                {
                    Description = "NPC items ref ItemInfo",
                    Match = new ConstraintMatch { Path = "data/shinetable/Shine/NPCItemList/**", Column = "Column*" },
                    ForeignKey = new ForeignKeyTarget { Table = "ItemInfo" },
                    EmptyValues = ["-", ""]
                }
            ]
        };

        var manifest = new MimirProject
        {
            Tables = new()
            {
                ["ItemInfo"] = "data/shn/Shine/ItemInfo.json",
                ["AdlShop_Tab00"] = "data/shinetable/Shine/NPCItemList/AdlShop_Tab00.json",
                ["SomeOther"] = "data/shinetable/Shine/Script/Something.json"
            }
        };

        var resolved = DefinitionResolver.Resolve(definitions, manifest);

        resolved.Count.ShouldBe(1);
        resolved[0].SourceTable.ShouldBe("AdlShop_Tab00");
        resolved[0].TargetTable.ShouldBe("ItemInfo");
        resolved[0].TargetColumn.ShouldBe("InxName");
        resolved[0].ColumnPattern.ShouldBe("Column*");
    }

    [Fact]
    public void Resolve_IdColumnShorthand()
    {
        var definitions = new ProjectDefinitions
        {
            Tables = new()
            {
                ["ItemInfo"] = new TableKeyInfo { IdColumn = "ID", KeyColumn = "InxName" }
            },
            Constraints =
            [
                new ConstraintRule
                {
                    Description = "Drop table refs ItemInfo by ID",
                    Match = new ConstraintMatch { Table = "DropTable*", Column = "ItemID" },
                    ForeignKey = new ForeignKeyTarget { Table = "ItemInfo", Column = "@id" },
                    EmptyValues = ["0"]
                }
            ]
        };

        var manifest = new MimirProject
        {
            Tables = new()
            {
                ["ItemInfo"] = "data/shn/ItemInfo.json",
                ["DropTable01"] = "data/shinetable/DropTable01.json"
            }
        };

        var resolved = DefinitionResolver.Resolve(definitions, manifest);

        resolved.Count.ShouldBe(1);
        resolved[0].TargetColumn.ShouldBe("ID"); // @id resolved to idColumn
    }

    [Fact]
    public async Task LoadAsync_SearchesUpward()
    {
        // Create a temp directory structure: parent/child/
        var tempDir = Path.Combine(Path.GetTempPath(), $"mimir_test_{Guid.NewGuid():N}");
        var parentDir = tempDir;
        var childDir = Path.Combine(tempDir, "child");
        Directory.CreateDirectory(childDir);

        try
        {
            // Place definitions file in parent
            var defsPath = Path.Combine(parentDir, "mimir.definitions.json");
            await File.WriteAllTextAsync(defsPath, """
            {
              "tables": { "TestTable": { "idColumn": "ID" } },
              "constraints": []
            }
            """);

            // Load from child dir - should find it in parent
            var defs = await DefinitionResolver.LoadAsync(childDir);
            defs.Tables.ShouldNotBeNull();
            defs.Tables.ShouldContainKey("TestTable");
            defs.Tables["TestTable"].IdColumn.ShouldBe("ID");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_ReturnsEmpty_WhenNotFound()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"mimir_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var defs = await DefinitionResolver.LoadAsync(tempDir);
            defs.ShouldNotBeNull();
            defs.Constraints.ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
