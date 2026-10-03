using System.Text.Json.Serialization;

namespace Fiesta.Collab.Core.Templates;

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

    /// <summary>
    /// Rows that exist only in the source become part of the SHARED content rather than being tagged to
    /// the source environment.
    ///
    /// The default (false) suits a round-trip project, where every environment must rebuild its own source
    /// exactly. A content overlay wants the opposite: merging a newer client over an older one should make
    /// its new rows part of the data every environment builds, not rows only the overlay can see.
    /// </summary>
    /// <summary>copyFiles: directory to take files from. Absolute, or relative to the project.</summary>
    [JsonPropertyName("fromPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FromPath { get; init; }

    /// <summary>copyFiles: glob against <see cref="FromPath"/>, e.g. <c>**/*.shbd</c>. The output
    /// directory is the existing <see cref="To"/>, relative to the target environment's build output.</summary>
    [JsonPropertyName("pattern")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Pattern { get; init; }

    [JsonPropertyName("sharedRows")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? SharedRows { get; init; }

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

    /// <summary>
    /// Overrides the output filename (without extension) when building.
    /// Used for incompatible-schema tables that share a source filename but need
    /// distinct internal names (e.g. GBHouse__server builds to GBHouse.shn).
    /// </summary>
    [JsonPropertyName("outputName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OutputName { get; init; }

    // createTable — a table no import source has; its rows arrive by migration
    /// <summary>createTable: an existing table whose columns, format and directives the new one takes.</summary>
    [JsonPropertyName("like")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Like { get; init; }

    /// <summary>createTable: the file the new table is written to, e.g. <c>TevaL.txt</c>.</summary>
    [JsonPropertyName("sourceFile")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceFile { get; init; }

    /// <summary>
    /// createTable: the #Table section name inside that file, when it differs from the model's. One file
    /// holds several sections - a MobRegen file holds MobRegenGroup and MobRegen both.
    /// </summary>
    [JsonPropertyName("sectionName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SectionName { get; init; }

    // copyMapFiles — per-map client files, named after the MAP rather than the client's folder
    /// <summary>
    /// copyMapFiles: subdirectories of <see cref="FromPath"/> to look in, in order. A client sorts its
    /// map folders into field, IDField, KDField and MHField, and which one a map lives in is not
    /// recorded anywhere, so they are searched.
    /// </summary>
    [JsonPropertyName("searchDirs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? SearchDirs { get; init; }

    /// <summary>copyMapFiles: "Table.Column" naming the maps to copy for - the server's own map list.</summary>
    [JsonPropertyName("mapsFrom")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MapsFrom { get; init; }

    /// <summary>
    /// copyMapFiles: "Table.KeyColumn:FolderColumn" resolving a map name to the client folder that
    /// holds its files - MapInfo.MapName:MapFolderName. The two differ often enough to matter: the map
    /// FroTundra lives in Tunnel01, Rou_Val26 in Rou.
    /// </summary>
    [JsonPropertyName("foldersFrom")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FoldersFrom { get; init; }

    /// <summary>copyMapFiles: file extensions to take for each map, e.g. .shbd, .sbi, .aid.</summary>
    [JsonPropertyName("extensions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Extensions { get; init; }

    // copyFile — copy a raw file verbatim from env source dir to build output
    [JsonPropertyName("env")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Env { get; init; }

    [JsonPropertyName("path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; init; }
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
