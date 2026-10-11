using System.Text.Json;
using System.Text.Json.Serialization;
using ClinicalSensorFusion.Core.Domain;
using ClinicalSensorFusion.Core.Signal;

namespace ClinicalSensorFusion.Core.Persistence;

/// <summary>
/// Serializzazione JSON con source generator: su iOS il codice è compilato AOT/ReadyToRun e trimmato,
/// la reflection di System.Text.Json non è affidabile.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(SessionSummary))]
[JsonSerializable(typeof(AudioFeatures))]
public sealed partial class CsfJsonContext : JsonSerializerContext
{
    /// <summary>Variante indentata, usata per il prompt al modello linguistico (più leggibile).</summary>
    public static CsfJsonContext Indented => s_indented ??= new CsfJsonContext(new JsonSerializerOptions(Default.Options) { WriteIndented = true });

    private static CsfJsonContext? s_indented;
}
