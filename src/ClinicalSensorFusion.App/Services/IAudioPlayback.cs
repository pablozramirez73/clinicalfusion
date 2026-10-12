namespace ClinicalSensorFusion.App.Services;

/// <summary>Riascolto delle registrazioni CAF (iOS: AVAudioPlayer; Android: AudioTrack).</summary>
public interface IAudioPlayback
{
    bool IsPlaying { get; }

    void Play(string absolutePath);

    void StopPlayback();
}
