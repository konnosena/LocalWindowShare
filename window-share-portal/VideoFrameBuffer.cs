internal static class VideoFrameBuffer
{
    private const int BytesPerPixel = 4;

    public static unsafe byte[] CopyToContiguousBgraBuffer(nint source, int width, int height, int stride)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfZero(stride);

        var rowBytes = checked(width * BytesPerPixel);
        var buffer = GC.AllocateUninitializedArray<byte>(checked(rowBytes * height));
        var destination = buffer.AsSpan();

        var sourceBase = (byte*)source;
        if (stride < 0)
        {
            sourceBase += (height - 1) * -stride;
        }

        for (var row = 0; row < height; row++)
        {
            var sourceRow = sourceBase + (row * stride);
            new ReadOnlySpan<byte>(sourceRow, rowBytes).CopyTo(destination.Slice(row * rowBytes, rowBytes));
        }

        return buffer;
    }
}
