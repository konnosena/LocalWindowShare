using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

internal sealed class WindowBroker
{
    private static readonly Lazy<ImageCodecInfo?> JpegCodec = new(() =>
        ImageCodecInfo.GetImageEncoders().FirstOrDefault(codec => codec.FormatID == ImageFormat.Jpeg.Guid));

    private static readonly nint ShellWindow = NativeMethods.GetShellWindow();

    public IReadOnlyList<WindowSummary> ListWindows()
    {
        var windows = new List<WindowSummary>();

        NativeMethods.EnumWindows((windowHandle, _) =>
        {
            if (TryBuildWindowSummary(windowHandle, out var summary))
            {
                windows.Add(summary);
            }

            return true;
        }, nint.Zero);

        return windows
            .OrderBy(window => window.IsMinimized)
            .ThenBy(window => window.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(window => window.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public bool TryGetWindow(long handle, out WindowSummary summary)
    {
        return TryBuildWindowSummary((nint)handle, out summary);
    }

    public bool TryCaptureWindow(long handle, int? maxWidth, int? quality, string? format, out WindowFrame frame, out string message, out int statusCode)
    {
        frame = default!;

        if (!TryResolveWindow(handle, out var windowHandle, out var summary, out message, out statusCode))
        {
            return false;
        }

        if (summary.IsMinimized)
        {
            message = "The window is minimized. Restore it before streaming.";
            statusCode = StatusCodes.Status409Conflict;
            return false;
        }

        if (summary.Bounds.Width <= 0 || summary.Bounds.Height <= 0)
        {
            message = "The window bounds are invalid.";
            statusCode = StatusCodes.Status409Conflict;
            return false;
        }

        try
        {
            using var capturedBitmap = CaptureBitmap(windowHandle, summary);
            using var scaledBitmap = ScaleBitmapIfNeeded(capturedBitmap, maxWidth);
            var imageBytes = EncodeFrameImage(scaledBitmap, format, quality, out var contentType);

            frame = new WindowFrame(summary, scaledBitmap.Width, scaledBitmap.Height, imageBytes, contentType);
            message = string.Empty;
            statusCode = StatusCodes.Status200OK;
            return true;
        }
        catch (Exception ex)
        {
            message = $"Failed to capture the window: {ex.Message}";
            statusCode = StatusCodes.Status500InternalServerError;
            return false;
        }
    }

    public bool TryCaptureWindowBitmap(long handle, int? maxWidth, out Bitmap bitmap, out WindowSummary summary, out string message, out int statusCode, bool preferScreenCaptureOnly = false, bool lowLatencyScaling = false)
    {
        bitmap = default!;
        summary = default!;

        if (!TryResolveWindow(handle, out var windowHandle, out summary, out message, out statusCode))
        {
            return false;
        }

        if (summary.IsMinimized)
        {
            message = "The window is minimized. Restore it before streaming.";
            statusCode = StatusCodes.Status409Conflict;
            return false;
        }

        if (summary.Bounds.Width <= 0 || summary.Bounds.Height <= 0)
        {
            message = "The window bounds are invalid.";
            statusCode = StatusCodes.Status409Conflict;
            return false;
        }

        try
        {
            using var capturedBitmap = CaptureBitmap(windowHandle, summary, preferScreenCaptureOnly);
            bitmap = ScaleBitmapIfNeeded(capturedBitmap, maxWidth, lowLatencyScaling);
            message = string.Empty;
            statusCode = StatusCodes.Status200OK;
            return true;
        }
        catch (Exception ex)
        {
            message = $"Failed to capture the window: {ex.Message}";
            statusCode = StatusCodes.Status500InternalServerError;
            return false;
        }
    }

    public OperationError? ActivateWindow(long handle)
    {
        if (!TryResolveWindow(handle, out var windowHandle, out _, out var message, out var statusCode))
        {
            return new OperationError(statusCode, message);
        }

        if (NativeMethods.IsIconic(windowHandle))
        {
            NativeMethods.ShowWindow(windowHandle, NativeMethods.SW_RESTORE);
        }
        else
        {
            NativeMethods.ShowWindow(windowHandle, NativeMethods.SW_SHOW);
        }

        NativeMethods.BringWindowToTop(windowHandle);
        var success = NativeMethods.SetForegroundWindow(windowHandle);
        var foregroundHandle = NativeMethods.GetForegroundWindow();
        if (!success && foregroundHandle != windowHandle)
        {
            return new OperationError(StatusCodes.Status409Conflict, "Windows blocked the foreground activation. Try tapping Activate again after one local interaction.");
        }

        return null;
    }

    public OperationError? ClickWindow(long handle, ClickInputRequest request)
    {
        return HandlePointerInput(handle, new PointerInputRequest("click", request.XRatio, request.YRatio, request.Button, request.Clicks, null));
    }

    public OperationError? HandlePointerInput(long handle, PointerInputRequest request)
    {
        if (request.XRatio is < 0 or > 1 || request.YRatio is < 0 or > 1)
        {
            return new OperationError(StatusCodes.Status400BadRequest, "Pointer coordinates must be between 0 and 1.");
        }

        var action = string.IsNullOrWhiteSpace(request.Action) ? "click" : request.Action.Trim().ToLowerInvariant();
        var button = NormalizeMouseButton(request.Button);
        if (button is null)
        {
            return new OperationError(StatusCodes.Status400BadRequest, "Unsupported mouse button.");
        }

        if (!TryGetWindow(handle, out var summary))
        {
            return new OperationError(StatusCodes.Status404NotFound, "Window not found.");
        }

        if (action is not "move" && action is not "down" && action is not "up" && action is not "click" && action is not "wheel")
        {
            return new OperationError(StatusCodes.Status400BadRequest, $"Unsupported pointer action: {request.Action}");
        }

        if (action is not "move")
        {
            var activationError = ActivateWindow(handle);
            if (activationError is not null)
            {
                return activationError;
            }
        }

        var screenPoint = ToScreenPoint(summary, request.XRatio, request.YRatio);
        var inputs = new List<NativeMethods.INPUT>();
        inputs.Add(BuildAbsoluteMoveInput(screenPoint.X, screenPoint.Y));

        switch (action)
        {
            case "move":
                break;
            case "down":
                inputs.Add(BuildMouseButtonInput(button.Value, keyDown: true));
                break;
            case "up":
                inputs.Add(BuildMouseButtonInput(button.Value, keyDown: false));
                break;
            case "click":
            {
                var clickCount = request.Clicks.GetValueOrDefault(1);
                if (clickCount < 1 || clickCount > 2)
                {
                    return new OperationError(StatusCodes.Status400BadRequest, "Clicks must be 1 or 2.");
                }

                for (var i = 0; i < clickCount; i++)
                {
                    inputs.Add(BuildMouseButtonInput(button.Value, keyDown: true));
                    inputs.Add(BuildMouseButtonInput(button.Value, keyDown: false));
                }
                break;
            }
            case "wheel":
            {
                var wheelDelta = request.WheelDelta.GetValueOrDefault(0);
                if (wheelDelta == 0)
                {
                    return new OperationError(StatusCodes.Status400BadRequest, "Wheel delta must not be zero.");
                }

                inputs.Add(NativeMethods.CreateMouseInput(NativeMethods.MOUSEEVENTF_WHEEL, mouseData: unchecked((uint)wheelDelta)));
                break;
            }
        }

        var sent = NativeMethods.SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<NativeMethods.INPUT>());
        if (sent != inputs.Count)
        {
            return new OperationError(StatusCodes.Status409Conflict, "Windows rejected the mouse injection.");
        }

        return null;
    }

    public OperationError? TypeText(long handle, TextInputRequest request)
    {
        if (string.IsNullOrEmpty(request.Text))
        {
            return new OperationError(StatusCodes.Status400BadRequest, "Text must not be empty.");
        }

        if (request.Text.Length > PortalSecurityLimits.MaxTextInputChars)
        {
            return new OperationError(
                StatusCodes.Status413PayloadTooLarge,
                $"Text must be {PortalSecurityLimits.MaxTextInputChars} characters or shorter.");
        }

        var activationError = ActivateWindow(handle);
        if (activationError is not null)
        {
            return activationError;
        }

        var inputs = BuildTextInputs(request.Text);
        if (inputs.Length == 0)
        {
            return new OperationError(StatusCodes.Status400BadRequest, "Text must contain at least one sendable character.");
        }

        var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
        if (sent != inputs.Length)
        {
            return new OperationError(StatusCodes.Status409Conflict, "Windows rejected the keyboard injection.");
        }

        return null;
    }

    public OperationError? SendKey(long handle, KeyInputRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Key))
        {
            return new OperationError(StatusCodes.Status400BadRequest, "Key must not be empty.");
        }

        var activationError = ActivateWindow(handle);
        if (activationError is not null)
        {
            return activationError;
        }

        if (!TryBuildKeyInputs(request.Key, out var inputs))
        {
            return new OperationError(StatusCodes.Status400BadRequest, $"Unsupported key command: {request.Key}");
        }

        var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
        if (sent != inputs.Length)
        {
            return new OperationError(StatusCodes.Status409Conflict, "Windows rejected the keyboard injection.");
        }

        return null;
    }

    private static bool TryResolveWindow(long handle, out nint windowHandle, out WindowSummary summary, out string message, out int statusCode)
    {
        windowHandle = (nint)handle;
        if (!NativeMethods.IsWindow(windowHandle))
        {
            summary = default!;
            message = "Window not found.";
            statusCode = StatusCodes.Status404NotFound;
            return false;
        }

        if (!TryBuildWindowSummary(windowHandle, out summary))
        {
            message = "Window is not shareable anymore.";
            statusCode = StatusCodes.Status404NotFound;
            return false;
        }

        message = string.Empty;
        statusCode = StatusCodes.Status200OK;
        return true;
    }

    private static bool TryBuildWindowSummary(nint windowHandle, out WindowSummary summary)
    {
        summary = default!;

        if (windowHandle == nint.Zero || windowHandle == ShellWindow || !NativeMethods.IsWindowVisible(windowHandle))
        {
            return false;
        }

        var title = GetWindowTitle(windowHandle);
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        if (IsCloaked(windowHandle) || IsToolWindow(windowHandle))
        {
            return false;
        }

        var bounds = GetWindowBounds(windowHandle);
        if (bounds.Width < 120 || bounds.Height < 90)
        {
            return false;
        }

        var className = GetClassName(windowHandle);
        if (string.Equals(className, "Progman", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var processId = 0;
        NativeMethods.GetWindowThreadProcessId(windowHandle, ref processId);
        var processName = ResolveProcessName(processId);
        var isMinimized = NativeMethods.IsIconic(windowHandle);
        var isForeground = NativeMethods.GetForegroundWindow() == windowHandle;

        summary = new WindowSummary(
            (long)windowHandle,
            title,
            processId,
            processName,
            className,
            isMinimized,
            isForeground,
            new WindowBounds(bounds.Left, bounds.Top, bounds.Width, bounds.Height));

        return true;
    }

    private static string ResolveProcessName(int processId)
    {
        if (processId <= 0)
        {
            return "unknown";
        }

        try
        {
            return Process.GetProcessById(processId).ProcessName;
        }
        catch
        {
            return processId.ToString();
        }
    }

    private static string GetWindowTitle(nint windowHandle)
    {
        var length = NativeMethods.GetWindowTextLength(windowHandle);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(length + 1);
        NativeMethods.GetWindowText(windowHandle, buffer, buffer.Capacity);
        return buffer.ToString().Trim();
    }

    private static string GetClassName(nint windowHandle)
    {
        var buffer = new StringBuilder(256);
        var result = NativeMethods.GetClassName(windowHandle, buffer, buffer.Capacity);
        return result > 0 ? buffer.ToString() : string.Empty;
    }

    private static bool IsToolWindow(nint windowHandle)
    {
        var extendedStyle = NativeMethods.GetWindowLongPtr(windowHandle, NativeMethods.GWL_EXSTYLE).ToInt64();
        return (extendedStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0;
    }

    private static bool IsCloaked(nint windowHandle)
    {
        if (NativeMethods.DwmGetWindowAttribute(windowHandle, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int)) != 0)
        {
            return false;
        }

        return cloaked != 0;
    }

    private static Rectangle GetWindowBounds(nint windowHandle)
    {
        if (NativeMethods.DwmGetWindowAttribute(windowHandle, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out NativeMethods.RECT extendedFrameBounds, Marshal.SizeOf<NativeMethods.RECT>()) == 0 &&
            extendedFrameBounds.Width > 0 &&
            extendedFrameBounds.Height > 0)
        {
            return extendedFrameBounds.ToRectangle();
        }

        NativeMethods.GetWindowRect(windowHandle, out var windowRect);
        return windowRect.ToRectangle();
    }

    private static Bitmap CaptureBitmap(nint windowHandle, WindowSummary summary, bool preferScreenCaptureOnly = false)
    {
        var bounds = summary.Bounds.ToRectangle();
        var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Black);

        var preferScreenCapture = preferScreenCaptureOnly || ShouldPreferScreenCapture(summary);
        if (preferScreenCapture && TryCopyFromScreen(graphics, bounds))
        {
            return bitmap;
        }

        if (preferScreenCaptureOnly)
        {
            return bitmap;
        }

        var deviceContext = graphics.GetHdc();
        var printSucceeded = false;
        try
        {
            printSucceeded = NativeMethods.PrintWindow(windowHandle, deviceContext, 0);
        }
        finally
        {
            graphics.ReleaseHdc(deviceContext);
        }

        if (!printSucceeded || IsLikelyBlankFrame(bitmap))
        {
            TryCopyFromScreen(graphics, bounds);
        }

        return bitmap;
    }

    private static bool ShouldPreferScreenCapture(WindowSummary summary)
    {
        if (summary.IsForeground)
        {
            return true;
        }

        if (summary.ClassName is "ConsoleWindowClass" or "CASCADIA_HOSTING_WINDOW_CLASS" or "CabinetWClass" or "ExploreWClass")
        {
            return true;
        }

        return summary.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase) ||
               summary.ProcessName.Equals("conhost", StringComparison.OrdinalIgnoreCase) ||
               summary.ProcessName.Equals("windowsterminal", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryCopyFromScreen(Graphics graphics, Rectangle bounds)
    {
        try
        {
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsLikelyBlankFrame(Bitmap bitmap)
    {
        const int gridSize = 10;

        var nonBlackSamples = 0;
        var totalSamples = 0;
        for (var y = 0; y < gridSize; y++)
        {
            var sampleY = (int)Math.Round((bitmap.Height - 1) * (y / (double)Math.Max(gridSize - 1, 1)));
            for (var x = 0; x < gridSize; x++)
            {
                var sampleX = (int)Math.Round((bitmap.Width - 1) * (x / (double)Math.Max(gridSize - 1, 1)));
                var color = bitmap.GetPixel(sampleX, sampleY);
                totalSamples++;

                if (color.A > 24 && (color.R > 10 || color.G > 10 || color.B > 10))
                {
                    nonBlackSamples++;
                    if (nonBlackSamples >= 3)
                    {
                        return false;
                    }
                }
            }
        }

        return totalSamples > 0;
    }

    private static Bitmap ScaleBitmapIfNeeded(Bitmap source, int? maxWidth, bool lowLatencyScaling = false)
    {
        var requestedWidth = maxWidth ?? source.Width;
        var targetWidth = Math.Clamp(requestedWidth, 240, source.Width);
        if (targetWidth >= source.Width)
        {
            return (Bitmap)source.Clone();
        }

        targetWidth = MakeEven(targetWidth);
        var targetHeight = MakeEven((int)Math.Round(source.Height * (targetWidth / (double)source.Width)));
        var scaledBitmap = new Bitmap(targetWidth, targetHeight, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(scaledBitmap);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        if (lowLatencyScaling)
        {
            graphics.InterpolationMode = InterpolationMode.Bilinear;
            graphics.PixelOffsetMode = PixelOffsetMode.HighSpeed;
            graphics.CompositingQuality = CompositingQuality.HighSpeed;
        }
        else
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
        }
        graphics.DrawImage(source, new Rectangle(0, 0, targetWidth, targetHeight));
        return scaledBitmap;
    }

    private static int MakeEven(int value)
    {
        if (value <= 2)
        {
            return 2;
        }

        return value % 2 == 0 ? value : value - 1;
    }

    private static byte[] EncodeFrameImage(Bitmap bitmap, string? format, int? quality, out string contentType)
    {
        if (string.Equals(format, "jpg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(format, "jpeg", StringComparison.OrdinalIgnoreCase))
        {
            contentType = "image/jpeg";
            return EncodeJpeg(bitmap, quality);
        }

        contentType = "image/png";
        using var memoryStream = new MemoryStream();
        bitmap.Save(memoryStream, ImageFormat.Png);
        return memoryStream.ToArray();
    }

    private static byte[] EncodeJpeg(Bitmap bitmap, int? quality)
    {
        using var memoryStream = new MemoryStream();
        var jpegQuality = Math.Clamp(quality ?? 72, 35, 95);
        var encoder = JpegCodec.Value;
        if (encoder is null)
        {
            bitmap.Save(memoryStream, ImageFormat.Jpeg);
            return memoryStream.ToArray();
        }

        using var encoderParameters = new EncoderParameters(1);
        encoderParameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)jpegQuality);
        bitmap.Save(memoryStream, encoder, encoderParameters);
        return memoryStream.ToArray();
    }

    private static NativeMethods.INPUT[] BuildTextInputs(string text)
    {
        var normalizedText = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var inputs = new List<NativeMethods.INPUT>(normalizedText.Length * 2);

        foreach (var character in normalizedText)
        {
            if (character == '\n')
            {
                inputs.AddRange(BuildVirtualKeyInputs(NativeMethods.VK_RETURN));
                continue;
            }

            inputs.Add(NativeMethods.CreateUnicodeKeyInput(character, keyUp: false));
            inputs.Add(NativeMethods.CreateUnicodeKeyInput(character, keyUp: true));
        }

        return inputs.ToArray();
    }

    private static bool TryBuildKeyInputs(string keyName, out NativeMethods.INPUT[] inputs)
    {
        inputs = Array.Empty<NativeMethods.INPUT>();
        var normalized = keyName.Trim().ToLowerInvariant();

        switch (normalized)
        {
            case "enter":
                inputs = BuildVirtualKeyInputs(NativeMethods.VK_RETURN);
                return true;
            case "tab":
                inputs = BuildVirtualKeyInputs(NativeMethods.VK_TAB);
                return true;
            case "escape":
            case "esc":
                inputs = BuildVirtualKeyInputs(NativeMethods.VK_ESCAPE);
                return true;
            case "backspace":
                inputs = BuildVirtualKeyInputs(NativeMethods.VK_BACK);
                return true;
            case "delete":
                inputs = BuildVirtualKeyInputs(NativeMethods.VK_DELETE);
                return true;
            case "up":
                inputs = BuildVirtualKeyInputs(NativeMethods.VK_UP);
                return true;
            case "down":
                inputs = BuildVirtualKeyInputs(NativeMethods.VK_DOWN);
                return true;
            case "left":
                inputs = BuildVirtualKeyInputs(NativeMethods.VK_LEFT);
                return true;
            case "right":
                inputs = BuildVirtualKeyInputs(NativeMethods.VK_RIGHT);
                return true;
            case "home":
                inputs = BuildVirtualKeyInputs(NativeMethods.VK_HOME);
                return true;
            case "end":
                inputs = BuildVirtualKeyInputs(NativeMethods.VK_END);
                return true;
            case "pageup":
                inputs = BuildVirtualKeyInputs(NativeMethods.VK_PRIOR);
                return true;
            case "pagedown":
                inputs = BuildVirtualKeyInputs(NativeMethods.VK_NEXT);
                return true;
            case "ctrl+c":
                inputs = BuildModifiedVirtualKeyInputs(NativeMethods.VK_CONTROL, (ushort)'C');
                return true;
            default:
                return false;
        }
    }

    private static NativeMethods.INPUT[] BuildVirtualKeyInputs(ushort virtualKey)
    {
        return
        [
            NativeMethods.CreateVirtualKeyInput(virtualKey, keyUp: false),
            NativeMethods.CreateVirtualKeyInput(virtualKey, keyUp: true),
        ];
    }

    private static NativeMethods.INPUT[] BuildModifiedVirtualKeyInputs(ushort modifier, ushort virtualKey)
    {
        return
        [
            NativeMethods.CreateVirtualKeyInput(modifier, keyUp: false),
            NativeMethods.CreateVirtualKeyInput(virtualKey, keyUp: false),
            NativeMethods.CreateVirtualKeyInput(virtualKey, keyUp: true),
            NativeMethods.CreateVirtualKeyInput(modifier, keyUp: true),
        ];
    }

    private static Point ToScreenPoint(WindowSummary summary, double xRatio, double yRatio)
    {
        var bounds = summary.Bounds.ToRectangle();
        var x = bounds.Left + Math.Clamp((int)Math.Round(xRatio * Math.Max(bounds.Width - 1, 1)), 0, Math.Max(bounds.Width - 1, 0));
        var y = bounds.Top + Math.Clamp((int)Math.Round(yRatio * Math.Max(bounds.Height - 1, 1)), 0, Math.Max(bounds.Height - 1, 0));
        return new Point(x, y);
    }

    private static NativeMethods.INPUT BuildAbsoluteMoveInput(int x, int y)
    {
        var virtualLeft = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        var virtualTop = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        var virtualWidth = Math.Max(NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN), 1);
        var virtualHeight = Math.Max(NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN), 1);

        var normalizedX = (int)Math.Round((x - virtualLeft) * 65535d / Math.Max(virtualWidth - 1, 1));
        var normalizedY = (int)Math.Round((y - virtualTop) * 65535d / Math.Max(virtualHeight - 1, 1));

        return NativeMethods.CreateMouseInput(
            NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_VIRTUALDESK,
            dx: normalizedX,
            dy: normalizedY);
    }

    private static MouseButton? NormalizeMouseButton(string? button)
    {
        var normalized = string.IsNullOrWhiteSpace(button) ? "left" : button.Trim().ToLowerInvariant();
        return normalized switch
        {
            "left" => MouseButton.Left,
            "right" => MouseButton.Right,
            "middle" => MouseButton.Middle,
            _ => null,
        };
    }

    private static NativeMethods.INPUT BuildMouseButtonInput(MouseButton button, bool keyDown)
    {
        return button switch
        {
            MouseButton.Left => NativeMethods.CreateMouseInput(keyDown ? NativeMethods.MOUSEEVENTF_LEFTDOWN : NativeMethods.MOUSEEVENTF_LEFTUP),
            MouseButton.Right => NativeMethods.CreateMouseInput(keyDown ? NativeMethods.MOUSEEVENTF_RIGHTDOWN : NativeMethods.MOUSEEVENTF_RIGHTUP),
            MouseButton.Middle => NativeMethods.CreateMouseInput(keyDown ? NativeMethods.MOUSEEVENTF_MIDDLEDOWN : NativeMethods.MOUSEEVENTF_MIDDLEUP),
            _ => throw new ArgumentOutOfRangeException(nameof(button), button, null),
        };
    }
}

internal enum MouseButton
{
    Left,
    Right,
    Middle,
}
