using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using WinRT;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;

internal sealed class WindowsGraphicsCaptureSource : IAsyncDisposable
{
    private const uint D3D11CreateDeviceBgraSupport = 0x20;
    private const uint D3D11SdkVersion = 7;
    private static readonly Guid GraphicsCaptureItemInteropId = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IdxgiDeviceId = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");

    private readonly object _sync = new();
    private readonly GraphicsCaptureItem _item;
    private readonly IDirect3DDevice _device;
    private readonly Direct3D11CaptureFramePool _framePool;
    private readonly GraphicsCaptureSession _session;

    private SoftwareBitmap? _latestFrame;
    private SizeInt32 _lastSize;
    private string? _lastError;
    private bool _captureClosed;
    private bool _started;
    private bool _disposed;
    private int _frameProcessing;

    public WindowsGraphicsCaptureSource(nint windowHandle)
    {
        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new NotSupportedException("Windows Graphics Capture is not supported on this machine.");
        }

        _item = CreateItemForWindow(windowHandle);
        _device = CreateDirect3DDevice();
        _lastSize = _item.Size;
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _device,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            _lastSize);
        _session = _framePool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = false;
        _framePool.FrameArrived += HandleFrameArrived;
        _item.Closed += HandleItemClosed;
    }

    private WindowsGraphicsCaptureSource(GraphicsCaptureItem item)
    {
        _item = item;
        _device = CreateDirect3DDevice();
        _lastSize = _item.Size;
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _device,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            _lastSize);
        _session = _framePool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = false;
        _framePool.FrameArrived += HandleFrameArrived;
        _item.Closed += HandleItemClosed;
    }

    public static WindowsGraphicsCaptureSource CreateForMonitor(nint monitorHandle)
    {
        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new NotSupportedException("Windows Graphics Capture is not supported on this machine.");
        }

        var item = CreateItemForMonitor(monitorHandle);
        return new WindowsGraphicsCaptureSource(item);
    }

    public static bool IsSupported => GraphicsCaptureSession.IsSupported();

    public void Start()
    {
        if (_started || _captureClosed)
        {
            return;
        }

        _started = true;
        _session.StartCapture();
    }

    public unsafe bool TryEncodeLatestFrame(VpxVideoEncoder videoEncoder, VideoCodecsEnum codec, out byte[]? encodedFrame, out int width, out int height, out string message)
    {
        lock (_sync)
        {
            if (_latestFrame is null)
            {
                encodedFrame = null;
                width = 0;
                height = 0;
                message = _lastError ?? (_captureClosed ? "The capture item was closed." : "Waiting for the first Windows Graphics Capture frame.");
                return false;
            }

            using var buffer = _latestFrame.LockBuffer(BitmapBufferAccessMode.Read);
            using var reference = buffer.CreateReference();
            var plane = buffer.GetPlaneDescription(0);
            var accessor = reference.As<IMemoryBufferByteAccess>();
            accessor.GetBuffer(out var data, out _);
            var widthEven = plane.Width & ~1;
            var heightEven = plane.Height & ~1;
            if (widthEven < 2 || heightEven < 2)
            {
                encodedFrame = null;
                width = 0;
                height = 0;
                message = "The captured frame was too small after even-dimension alignment.";
                return false;
            }

            var pixelBuffer = VideoFrameBuffer.CopyToContiguousBgraBuffer((nint)(data + plane.StartIndex), widthEven, heightEven, plane.Stride);
            try
            {
                encodedFrame = videoEncoder.EncodeVideo(widthEven, heightEven, pixelBuffer, VideoPixelFormatsEnum.Bgra, codec);
            }
            catch (Exception ex)
            {
                encodedFrame = null;
                width = widthEven;
                height = heightEven;
                message = $"The video encoder failed: {ex.GetType().Name}: {ex.Message}";
                return false;
            }

            if (encodedFrame is null || encodedFrame.Length == 0)
            {
                width = widthEven;
                height = heightEven;
                message = "The video encoder returned an empty frame.";
                return false;
            }

            width = widthEven;
            height = heightEven;
            message = string.Empty;
            return true;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _framePool.FrameArrived -= HandleFrameArrived;
        _item.Closed -= HandleItemClosed;

        lock (_sync)
        {
            _latestFrame?.Dispose();
            _latestFrame = null;
        }

        (_session as IDisposable)?.Dispose();
        (_framePool as IDisposable)?.Dispose();
        (_device as IDisposable)?.Dispose();
        return ValueTask.CompletedTask;
    }

    private async void HandleFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (_disposed || _captureClosed)
        {
            using var ignored = sender.TryGetNextFrame();
            return;
        }

        if (Interlocked.Exchange(ref _frameProcessing, 1) == 1)
        {
            using var ignored = sender.TryGetNextFrame();
            return;
        }

        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame is null)
            {
                return;
            }

            var contentSize = frame.ContentSize;
            if (contentSize.Width <= 0 || contentSize.Height <= 0)
            {
                return;
            }

            if (contentSize.Width != _lastSize.Width || contentSize.Height != _lastSize.Height)
            {
                _framePool.Recreate(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, contentSize);
                _lastSize = contentSize;
            }

            var copiedFrame = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface);
            SoftwareBitmap normalizedFrame = copiedFrame;
            if (copiedFrame.BitmapPixelFormat != BitmapPixelFormat.Bgra8 ||
                copiedFrame.BitmapAlphaMode != BitmapAlphaMode.Premultiplied)
            {
                normalizedFrame = SoftwareBitmap.Convert(copiedFrame, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                copiedFrame.Dispose();
            }

            lock (_sync)
            {
                var previousFrame = _latestFrame;
                _latestFrame = normalizedFrame;
                _lastError = null;
                previousFrame?.Dispose();
            }
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                _lastError = ex.Message;
            }
        }
        finally
        {
            Interlocked.Exchange(ref _frameProcessing, 0);
        }
    }

    private void HandleItemClosed(GraphicsCaptureItem sender, object args)
    {
        _captureClosed = true;
    }

    private static GraphicsCaptureItem CreateItemForWindow(nint windowHandle)
    {
        var className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        nint classId = nint.Zero;
        nint factoryPointer = nint.Zero;

        try
        {
            CheckHResult(WindowsCreateString(className, className.Length, out classId));
            CheckHResult(RoGetActivationFactory(classId, GraphicsCaptureItemInteropId, out factoryPointer));
            var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPointer);
            Marshal.Release(factoryPointer);
            factoryPointer = nint.Zero;

            try
            {
                CheckHResult(interop.CreateForWindow(windowHandle, typeof(GraphicsCaptureItem).GUID, out var itemPointer));
                try
                {
                    return MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPointer);
                }
                finally
                {
                    Marshal.Release(itemPointer);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(interop);
            }
        }
        finally
        {
            if (factoryPointer != nint.Zero)
            {
                Marshal.Release(factoryPointer);
            }

            if (classId != nint.Zero)
            {
                WindowsDeleteString(classId);
            }
        }
    }

    private static GraphicsCaptureItem CreateItemForMonitor(nint monitorHandle)
    {
        var className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        nint classId = nint.Zero;
        nint factoryPointer = nint.Zero;

        try
        {
            CheckHResult(WindowsCreateString(className, className.Length, out classId));
            CheckHResult(RoGetActivationFactory(classId, GraphicsCaptureItemInteropId, out factoryPointer));
            var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPointer);
            Marshal.Release(factoryPointer);
            factoryPointer = nint.Zero;

            try
            {
                CheckHResult(interop.CreateForMonitor(monitorHandle, typeof(GraphicsCaptureItem).GUID, out var itemPointer));
                try
                {
                    return MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPointer);
                }
                finally
                {
                    Marshal.Release(itemPointer);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(interop);
            }
        }
        finally
        {
            if (factoryPointer != nint.Zero)
            {
                Marshal.Release(factoryPointer);
            }

            if (classId != nint.Zero)
            {
                WindowsDeleteString(classId);
            }
        }
    }

    private static IDirect3DDevice CreateDirect3DDevice()
    {
        CheckHResult(D3D11CreateDevice(
            nint.Zero,
            D3DDriverType.Hardware,
            nint.Zero,
            D3D11CreateDeviceBgraSupport,
            nint.Zero,
            0,
            D3D11SdkVersion,
            out var devicePointer,
            out _,
            out var contextPointer));

        try
        {
            if (Marshal.QueryInterface(devicePointer, in IdxgiDeviceId, out var dxgiDevicePointer) != 0)
            {
                throw new InvalidOperationException("Failed to query IDXGIDevice.");
            }

            try
            {
                CheckHResult(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevicePointer, out var inspectablePointer));
                try
                {
                    return MarshalInterface<IDirect3DDevice>.FromAbi(inspectablePointer);
                }
                finally
                {
                    Marshal.Release(inspectablePointer);
                }
            }
            finally
            {
                Marshal.Release(dxgiDevicePointer);
            }
        }
        finally
        {
            if (contextPointer != nint.Zero)
            {
                Marshal.Release(contextPointer);
            }

            if (devicePointer != nint.Zero)
            {
                Marshal.Release(devicePointer);
            }
        }
    }

    private static void CheckHResult(int hresult)
    {
        if (hresult < 0)
        {
            Marshal.ThrowExceptionForHR(hresult);
        }
    }

    [DllImport("combase.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string sourceString, int length, out nint hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(nint hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(nint activatableClassId, in Guid iid, out nint factory);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(
        nint adapter,
        D3DDriverType driverType,
        nint software,
        uint flags,
        nint featureLevels,
        uint featureLevelsCount,
        uint sdkVersion,
        out nint device,
        out uint featureLevel,
        out nint immediateContext);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig]
        int CreateForWindow(nint window, in Guid iid, out nint result);

        [PreserveSig]
        int CreateForMonitor(nint monitor, in Guid iid, out nint result);
    }

    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private unsafe interface IMemoryBufferByteAccess
    {
        void GetBuffer(out byte* value, out uint capacity);
    }

    private enum D3DDriverType : uint
    {
        Hardware = 1,
    }
}
