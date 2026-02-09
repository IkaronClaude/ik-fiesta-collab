using Mimir.Core.Constraints;

namespace Mimir.Core.Templates;

/// <summary>
/// Converts template actions to ProjectDefinitions format for backwards compatibility
/// with the existing constraint/SQL engine infrastructure.
/// </summary>
public static class TemplateDefinitionBridge
{
    public static ProjectDefinitions ToDefinitions(ProjectTemplate template)
    {
        var tables = new Dictionary<string, TableKeyInfo>();
        var constraints = new List<ConstraintRule>();

        // Temporary storage for building TableKeyInfo with annotations
        var tableIdColumns = new Dictionary<string, string>();
        var tableKeyColumns = new Dictionary<string, string>();
        var tableAnnotations = new Dictionary<string, Dictionary<string, ColumnAnnotation>>();

        foreach (var action in template.Actions)
        {
            switch (action.Action)
            {
                case "setPrimaryKey" when action.Table != null && action.Column != null:
                    tableIdColumns[action.Table] = action.Column;
                    break;

                case "setUniqueKey" when action.Table != null && action.Column != null:
                    tableKeyColumns[action.Table] = action.Column;
                    break;

                case "setForeignKey" when action.Table != null && action.Column != null && action.References != null:
                    constraints.Add(new ConstraintRule
                    {
                        Match = new ConstraintMatch
                        {
                            Table = action.Table,
                            Column = action.Column
                        },
                        ForeignKey = new ForeignKeyTarget
                        {
                            Table = action.References.Table,
                            Column = action.References.Column
                        },
                        EmptyValues = action.EmptyValues
                    });
                    break;

                case "annotateColumn" when action.Table != null && action.Column != null:
                    if (!tableAnnotations.ContainsKey(action.Table))
                        tableAnnotations[action.Table] = new();
                    tableAnnotations[action.Table][action.Column] = new ColumnAnnotation
                    {
                        DisplayName = action.DisplayName,
                        Description = action.Description
                    };
                    break;
            }
        }

        // Build TableKeyInfo for each table that has any declarations
        var allTableNames = tableIdColumns.Keys
            .Union(tableKeyColumns.Keys)
            .Union(tableAnnotations.Keys)
            .Distinct();

        foreach (var tableName in allTableNames)
        {
            tables[tableName] = new TableKeyInfo
            {
                IdColumn = tableIdColumns.GetValueOrDefault(tableName),
                KeyColumn = tableKeyColumns.GetValueOrDefault(tableName),
                ColumnAnnotations = tableAnnotations.TryGetValue(tableName, out var anns)
                    ? anns
                    : null
            };
        }

        return new ProjectDefinitions
        {
            Tables = tables.Count > 0 ? tables : null,
            Constraints = constraints
        };
    }
}
