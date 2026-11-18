using System.Runtime.InteropServices;

namespace PtzJoystickControl.Core.Video;

/// <summary>Stride-aware copying of raw video frames between native and managed memory.</summary>
public static class FrameCopy
{
    /// <summary>Copies <paramref name="rows"/> rows of <paramref name="rowBytes"/> bytes from native memory into a tightly packed managed buffer.</summary>
    public static void ToManaged(IntPtr source, int sourceStride, int rows, int rowBytes, byte[] destination)
    {
        Validate(source, sourceStride, rows, rowBytes, destination.Length);

        if (sourceStride == rowBytes)
        {
            Marshal.Copy(source, destination, 0, rows * rowBytes);
            return;
        }
        for (int row = 0; row < rows; row++)
            Marshal.Copy(IntPtr.Add(source, row * sourceStride), destination, row * rowBytes, rowBytes);
    }

    /// <summary>Copies a tightly packed managed buffer into native memory that has its own row stride.</summary>
    public static void ToNative(byte[] source, int rows, int rowBytes, IntPtr destination, int destinationStride)
    {
        Validate(destination, destinationStride, rows, rowBytes, source.Length);

        if (destinationStride == rowBytes)
        {
            Marshal.Copy(source, 0, destination, rows * rowBytes);
            return;
        }
        for (int row = 0; row < rows; row++)
            Marshal.Copy(source, row * rowBytes, IntPtr.Add(destination, row * destinationStride), rowBytes);
    }

    private static void Validate(IntPtr pointer, int stride, int rows, int rowBytes, int managedLength)
    {
        if (pointer == IntPtr.Zero)
            throw new ArgumentException("Native pointer is null.", nameof(pointer));
        if (rows <= 0 || rowBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(rows), "Frame size must be positive.");
        if (stride < rowBytes)
            throw new ArgumentOutOfRangeException(nameof(stride), "Stride is smaller than one row of pixels.");
        if ((long)rows * rowBytes > managedLength)
            throw new ArgumentException("Managed buffer is too small for the frame.");
    }
}
