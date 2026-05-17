using System.Text.Json.Serialization;

namespace MTRCS;

/// <summary>
/// Source-generated JSON serializer context for mtrcs report types.
/// Registering <see cref="MtrcsReportJson"/> here causes the compiler to emit all
/// required serialization metadata at build time, making the JSON export fully
/// compatible with AOT (PublishAot=true) and trimming.
/// </summary>
[JsonSerializable(typeof(MtrcsReportJson))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class MtrcsReportJsonContext : JsonSerializerContext
{
}
