using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NewTek.NDI;

namespace PtzJoystickControl.Gui.Video;

internal static class NdiSourceFinder
{
    /// <summary>Lists NDI sources currently visible on the network. Throws <see cref="InvalidOperationException"/> with a readable message when NDI is unavailable.</summary>
    public static async Task<IReadOnlyList<string>> FindAsync(TimeSpan listenTime, CancellationToken cancellationToken)
    {
        if (!NdiRuntime.TryInitialize(out var error))
            throw new InvalidOperationException(error);

        using var finder = new Finder(showLocalSources: true, groups: null!, extraIps: null!);

        // Sources announce themselves over a second or two; keep collecting for the whole listen time.
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var end = DateTime.UtcNow + listenTime;
        while (DateTime.UtcNow < end)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var source in finder.Sources.ToList())
                if (!string.IsNullOrWhiteSpace(source.Name))
                    names.Add(source.Name);
            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        }
        return names.ToList();
    }
}
