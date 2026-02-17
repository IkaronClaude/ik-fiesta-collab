using System.Text.Json.Serialization;

namespace Mimir.Core.Templates;

public sealed class ProjectTemplate
{
    [JsonPropertyName("actions")]
    public List<TemplateAction> Actions { get; init; } = [];
}

public sealed class TemplateAction
{
    [JsonPropertyName("action")]
    public required string Action { get; init; }

    // copy / merge
    [JsonPropertyName("from")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TableRef? From { get; init; }

    [JsonPropertyName("to")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? To { get; init; }

    [JsonPropertyName("into")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Into { get; init; }

    [JsonPropertyName("on")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JoinClause? On { get; init; }

    [JsonPropertyName("columnStrategy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ColumnStrategy { get; init; }

    // setPrimaryKey / setUniqueKey / annotateColumn / setForeignKey
    [JsonPropertyName("table")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Table { get; init; }

    [JsonPropertyName("column")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Column { get; init; }

    // setForeignKey
    [JsonPropertyName("references")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ForeignKeyRef? References { get; init; }

    [JsonPropertyName("emptyValues")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? EmptyValues { get; init; }

    // annotateColumn
    [JsonPropertyName("displayName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; init; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    [JsonPropertyName("conflictStrategy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConflictStrategy { get; init; }
}

public sealed class TableRef
{
    [JsonPropertyName("table")]
    public required string Table { get; init; }

    [JsonPropertyName("env")]
    public required string Env { get; init; }
}

public sealed class JoinClause
{
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyName("target")]
    public required string Target { get; init; }
}

public sealed class ForeignKeyRef
{
    [JsonPropertyName("table")]
    public required string Table { get; init; }

    [JsonPropertyName("column")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Column { get; init; }
}
