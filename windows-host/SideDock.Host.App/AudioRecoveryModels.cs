namespace SideDock.Host.App;

public enum AudioRecoveryIssue
{
    None,
    Healthy,
    AudioDisabled,
    HostNotRunning,
    AndroidNotConnected,
    PendingChanges,
    VirtualCableMissing,
    EndpointUnbound,
    EndpointMissing,
    EndpointDisabled,
    EndpointEnumerationFailed,
    HostTelemetryStale,
    AndroidTelemetryStale,
    AndroidMicrophonePermissionMissing,
    MicrophoneMutedOrStopped,
    SpeakerMutedOrStopped,
    NoMicrophonePackets,
    NoSpeakerPackets,
    SilentMicrophoneInput,
    NoSpeakerOutputLevel,
    RecentHostError,
    Unknown
}

public enum AudioRecoveryAction
{
    None,
    NoAction,
    StartHost,
    InstallOrRepairVirtualAudioCable,
    AutoBindRecommendedEndpoints,
    RefreshEndpoints,
    ApplyAndRestartAudio,
    OpenSoundSettings,
    CopyDiagnostics,
    WaitForAndroidDevice,
    RequestAndroidMicrophonePermission
}

public static class AudioRecoveryActions
{
    public static AudioRecoveryAction SelectPrimaryAudioRecoveryAction(
        AudioRecoveryIssue issue,
        bool canAutoBindRecommendedEndpoints)
    {
        return issue switch
        {
            AudioRecoveryIssue.None => AudioRecoveryAction.None,
            AudioRecoveryIssue.Healthy => AudioRecoveryAction.NoAction,
            AudioRecoveryIssue.AudioDisabled => AudioRecoveryAction.NoAction,
            AudioRecoveryIssue.HostNotRunning => AudioRecoveryAction.StartHost,
            AudioRecoveryIssue.PendingChanges => AudioRecoveryAction.ApplyAndRestartAudio,
            AudioRecoveryIssue.VirtualCableMissing => AudioRecoveryAction.InstallOrRepairVirtualAudioCable,
            AudioRecoveryIssue.EndpointEnumerationFailed => AudioRecoveryAction.RefreshEndpoints,
            AudioRecoveryIssue.EndpointDisabled => AudioRecoveryAction.OpenSoundSettings,
            AudioRecoveryIssue.EndpointUnbound or AudioRecoveryIssue.EndpointMissing =>
                canAutoBindRecommendedEndpoints
                    ? AudioRecoveryAction.AutoBindRecommendedEndpoints
                    : AudioRecoveryAction.RefreshEndpoints,
            AudioRecoveryIssue.AndroidNotConnected => AudioRecoveryAction.WaitForAndroidDevice,
            AudioRecoveryIssue.AndroidMicrophonePermissionMissing => AudioRecoveryAction.RequestAndroidMicrophonePermission,
            AudioRecoveryIssue.HostTelemetryStale or AudioRecoveryIssue.AndroidTelemetryStale => AudioRecoveryAction.ApplyAndRestartAudio,
            AudioRecoveryIssue.MicrophoneMutedOrStopped => AudioRecoveryAction.RequestAndroidMicrophonePermission,
            AudioRecoveryIssue.SpeakerMutedOrStopped => AudioRecoveryAction.WaitForAndroidDevice,
            AudioRecoveryIssue.NoMicrophonePackets or AudioRecoveryIssue.NoSpeakerPackets => AudioRecoveryAction.ApplyAndRestartAudio,
            AudioRecoveryIssue.SilentMicrophoneInput => AudioRecoveryAction.RequestAndroidMicrophonePermission,
            AudioRecoveryIssue.NoSpeakerOutputLevel => AudioRecoveryAction.OpenSoundSettings,
            AudioRecoveryIssue.RecentHostError or AudioRecoveryIssue.Unknown => AudioRecoveryAction.CopyDiagnostics,
            _ => AudioRecoveryAction.CopyDiagnostics
        };
    }
}
