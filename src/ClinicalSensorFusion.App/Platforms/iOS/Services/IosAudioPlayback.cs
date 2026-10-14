using ClinicalSensorFusion.Core.Localization;
using AVFoundation;
using ClinicalSensorFusion.App.Services;
using Foundation;

namespace ClinicalSensorFusion.App.Platforms.iOS.Services;

internal sealed class IosAudioPlayback : IAudioPlayback
{
    private AVAudioPlayer? _player;

    public bool IsPlaying => _player?.Playing == true;

    public void Play(string absolutePath)
    {
        StopPlayback();
        AVAudioSession.SharedInstance().SetCategory(AVAudioSessionCategory.Playback);
        AVAudioSession.SharedInstance().SetActive(true, out _);
        _player = AVAudioPlayer.FromUrl(NSUrl.FromFilename(absolutePath), out var error)
            ?? throw new InvalidOperationException(error?.LocalizedDescription ?? Loc.T("File audio non leggibile."));
        _player.Play();
    }

    public void StopPlayback()
    {
        _player?.Stop();
        _player?.Dispose();
        _player = null;
    }
}
