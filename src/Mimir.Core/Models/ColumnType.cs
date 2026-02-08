namespace Mimir.Core.Models;

/// <summary>
/// Normalized column types used across all data providers.
/// Maps to both SHN type codes and SQLite column affinities.
/// </summary>
public enum ColumnType
{
    Byte,
    SByte,
    UInt16,
    Int16,
    UInt32,
    Int32,
    UInt64,
    Float,
    String
}
