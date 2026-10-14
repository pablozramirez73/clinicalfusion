using ClinicalSensorFusion.Core.Domain;
using ClinicalSensorFusion.Core.Persistence;

namespace ClinicalSensorFusion.App.Services;

/// <summary>
/// Percorsi dei dati, identici all'app originale: su iOS <see cref="FileSystem.AppDataDirectory"/> è
/// <c>Library/</c> del contenitore → <c>Library/clinical.db</c> e <c>Library/audio/&lt;id&gt;/audio.caf</c>.
/// La cartella Documents resta vuota: niente è esposto nell'app File.
/// </summary>
public static class AppPaths
{
    public static string Library => FileSystem.AppDataDirectory;

    public static string Database => Path.Combine(Library, ClinicalDatabase.FileName);

    public static string Audio(AudioRecording recording) => Path.Combine(Library, recording.RelativePath);

    public static string AudioDirectory(string recordingId) => Path.Combine(Library, "audio", recordingId);
}
