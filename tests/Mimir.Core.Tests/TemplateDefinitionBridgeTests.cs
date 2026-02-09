using Mimir.Core.Constraints;
using Mimir.Core.Templates;
using Shouldly;
using Xunit;

namespace Mimir.Core.Tests;

public class TemplateDefinitionBridgeTests
{
    [Fact]
    public void ExtractsTableKeyInfo_FromPrimaryAndUniqueKeyActions()
    {
        var template = new ProjectTemplate
        {
            Actions =
            [
                new TemplateAction { Action = "setPrimaryKey", Table = "ItemInfo", Column = "ID" },
                new TemplateAction { Action = "setUniqueKey", Table = "ItemInfo", Column = "InxName" },
                new TemplateAction { Action = "setPrimaryKey", Table = "MonsterInfo", Column = "ID" },
            ]
        };

        var definitions = TemplateDefinitionBridge.ToDefinitions(template);

        definitions.Tables.ShouldNotBeNull();
        definitions.Tables!.ShouldContainKey("ItemInfo");
        definitions.Tables["ItemInfo"].IdColumn.ShouldBe("ID");
        definitions.Tables["ItemInfo"].KeyColumn.ShouldBe("InxName");

        definitions.Tables.ShouldContainKey("MonsterInfo");
        definitions.Tables["MonsterInfo"].IdColumn.ShouldBe("ID");
        definitions.Tables["MonsterInfo"].KeyColumn.ShouldBeNull();
    }

    [Fact]
    public void ExtractsConstraintRules_FromForeignKeyActions()
    {
        var template = new ProjectTemplate
        {
            Actions =
            [
                new TemplateAction
                {
                    Action = "setForeignKey",
                    Table = "NPCItemList",
                    Column = "Column*",
                    References = new ForeignKeyRef { Table = "ItemInfo", Column = "InxName" },
                    EmptyValues = ["-", ""]
                }
            ]
        };

        var definitions = TemplateDefinitionBridge.ToDefinitions(template);

        definitions.Constraints.Count.ShouldBe(1);
        definitions.Constraints[0].Match.Table.ShouldBe("NPCItemList");
        definitions.Constraints[0].Match.Column.ShouldBe("Column*");
        definitions.Constraints[0].ForeignKey!.Table.ShouldBe("ItemInfo");
        definitions.Constraints[0].ForeignKey!.Column.ShouldBe("InxName");
        definitions.Constraints[0].EmptyValues.ShouldBe(["-", ""]);
    }

    [Fact]
    public void ExtractsAnnotations_FromAnnotateColumnActions()
    {
        var template = new ProjectTemplate
        {
            Actions =
            [
                new TemplateAction
                {
                    Action = "annotateColumn",
                    Table = "ItemInfo",
                    Column = "AC",
                    DisplayName = "Armor Class",
                    Description = "Defense stat"
                },
                new TemplateAction
                {
                    Action = "annotateColumn",
                    Table = "ItemInfo",
                    Column = "MR",
                    DisplayName = "Magic Resist"
                }
            ]
        };

        var definitions = TemplateDefinitionBridge.ToDefinitions(template);

        definitions.Tables.ShouldNotBeNull();
        definitions.Tables!["ItemInfo"].ColumnAnnotations.ShouldNotBeNull();
        definitions.Tables["ItemInfo"].ColumnAnnotations!["AC"].DisplayName.ShouldBe("Armor Class");
        definitions.Tables["ItemInfo"].ColumnAnnotations!["AC"].Description.ShouldBe("Defense stat");
        definitions.Tables["ItemInfo"].ColumnAnnotations!["MR"].DisplayName.ShouldBe("Magic Resist");
    }

    [Fact]
    public void EmptyTemplate_ReturnsEmptyDefinitions()
    {
        var template = new ProjectTemplate();
        var definitions = TemplateDefinitionBridge.ToDefinitions(template);

        definitions.Constraints.ShouldBeEmpty();
        definitions.Tables.ShouldBeNull();
    }
}
