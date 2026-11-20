using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using PtzJoystickControl.Application.Ndi;

namespace PtzJoystickControl.Gui.Video;

internal static class NdiSourceFinder
{
    /// <summary>Lists NDI sources currently visible on the network. Throws <see cref="InvalidOperationException"/> with a readable message when NDI is unavailable.</summary>
    public static IReadOnlyList<string> Find(TimeSpan listenTime, CancellationToken cancellationToken)
    {
        if (!NdiRuntime.TryInitialize(out var error))
            throw new InvalidOperationException(error);

        var settings = new NdiNative.FindCreate { ShowLocalSources = 1 };
        var finder = NdiNative.FindCreate2(ref settings);
        if (finder == IntPtr.Zero)
            throw new InvalidOperationException("Could not start searching for NDI sources.");

        try
        {
            // Sources announce themselves over a second or two; keep collecting for the whole listen time.
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var end = DateTime.UtcNow + listenTime;
            while (DateTime.UtcNow < end)
            {
                cancellationToken.ThrowIfCancellationRequested();
                NdiNative.FindWaitForSources(finder, 300);
                // The returned array is only valid until the next call, so copy the names right away.
                var sources = NdiNative.FindGetCurrentSources(finder, out var count);
                foreach (var name in NdiNative.ReadSourceNames(sources, count))
                    names.Add(name);
            }
            return names.ToList();
        }
        finally
        {
            NdiNative.FindDestroy(finder);
        }
    }
}
