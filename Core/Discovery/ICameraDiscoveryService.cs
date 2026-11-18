namespace PtzJoystickControl.Core.Discovery;

public interface ICameraDiscoveryService
{
    /// <summary>
    /// Looks for cameras on all local IPv4 networks using ONVIF WS-Discovery and a VISCA subnet scan.
    /// Results are reported through <paramref name="progress"/> as they are found (merged per address)
    /// and returned when the search is finished or cancelled.
    /// </summary>
    Task<IReadOnlyList<DiscoveredCamera>> DiscoverAsync(
        IProgress<DiscoveredCamera>? progress = null,
        CancellationToken cancellationToken = default);
}
