using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;
using WinRtDirect3DDevice = Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice;

namespace SideDock.Host;

internal static partial class Program
{
    [SupportedOSPlatform("windows10.0.19041.0")]
    private sealed class WindowsGraphicsCaptureFrameSource : IGpuFrameSource
    {
        private static readonly Guid IidId3D11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
        private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(5);

        private readonly HostOptions _options;
        private readonly DisplayLayoutProvider _displayLayoutProvider;
        private readonly Action<string> _log;
        private readonly AutoResetEvent _frameArrived = new(false);
        private IDXGIFactory1? _factory;
        private IDXGIAdapter1? _adapter;
        private ID3D11Device? _device;
        private ID3D11DeviceContext? _context;
        private WinRtDirect3DDevice? _winRtDevice;
        private GraphicsCaptureItem? _captureItem;
        private Direct3D11CaptureFramePool? _framePool;
        private GraphicsCaptureSession? _session;
        private int _width;
        private int _height;
        private long _sequence;
        private long _framesCaptured;
        private long _framesDropped;
        private long _lastFrameStopwatchTicks;
        private bool _disposed;

        public WindowsGraphicsCaptureFrameSource(
            HostOptions options,
            DisplayLayoutProvider displayLayoutProvider,
            Action<string> log)
        {
            _options = options;
            _displayLayoutProvider = displayLayoutProvider;
            _log = log;
        }

        public string SourceName => "idd-gpu";

        public string SourceDescription => "Windows Graphics Capture for the SideDock virtual display (system cursor disabled)";

        public ID3D11Device Device => _device ?? throw new InvalidOperationException("Windows Graphics Capture device has not started.");

        public ID3D11DeviceContext Context => _context ?? throw new InvalidOperationException("Windows Graphics Capture device context has not started.");

        public int Width => _width;

        public int Height => _height;

        public int SlotCount => 2;

        public long FramesDropped => Interlocked.Read(ref _framesDropped);

        public double LastFrameAgeMs
        {
            get
            {
                var lastFrameTicks = Interlocked.Read(ref _lastFrameStopwatchTicks);
                return lastFrameTicks == 0
                    ? 0
                    : (Stopwatch.GetTimestamp() - lastFrameTicks) * 1000.0 / Stopwatch.Frequency;
            }
        }

        public void Start(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            {
                throw new PlatformNotSupportedException("Cursor-free Windows Graphics Capture requires Windows 10 version 2004 or later.");
            }

            if (!GraphicsCaptureSession.IsSupported())
            {
                throw new CaptureException("WGC_UNSUPPORTED", "Windows Graphics Capture is not supported on this system.");
            }

            var layout = _displayLayoutProvider.GetLayout(force: true)
                ?? throw new CaptureException("WGC_DISPLAY_NOT_FOUND", "SideDock virtual display layout is unavailable.");
            var monitorRect = new DisplayNative.Rect
            {
                Left = layout.X,
                Top = layout.Y,
                Right = layout.X + layout.Width,
                Bottom = layout.Y + layout.Height
            };
            var monitor = DisplayNative.MonitorFromRect(ref monitorRect, DisplayNative.MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero)
            {
                throw new CaptureException("WGC_MONITOR_NOT_FOUND", $"Unable to resolve HMONITOR for {layout.DeviceName}.");
            }

            CreateD3DDevice(monitor);
            _winRtDevice = CreateWinRtDevice(Device);
            _captureItem = CreateCaptureItemForMonitor(monitor);
            var captureSize = _captureItem.Size;
            if (captureSize.Width != _options.VideoWidth || captureSize.Height != _options.VideoHeight)
            {
                throw new CaptureException(
                    "WGC_FRAME_SIZE_UNSUPPORTED",
                    $"Windows Graphics Capture size {captureSize.Width}x{captureSize.Height} does not match requested {_options.VideoWidth}x{_options.VideoHeight}.");
            }

            _width = captureSize.Width;
            _height = captureSize.Height;
            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _winRtDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                SlotCount,
                captureSize);
            _framePool.FrameArrived += OnFrameArrived;
            _session = _framePool.CreateCaptureSession(_captureItem);
            _session.IsCursorCaptureEnabled = false;
            _session.StartCapture();

            _log(
                $"connected Windows Graphics Capture display={layout.DeviceName} monitor=0x{monitor.ToInt64():X} "
                + $"size={_width}x{_height} buffers={SlotCount} cursorCapture={_session.IsCursorCaptureEnabled} "
                + "frameRepair=off currentFrameOnly=true gpuCopy=single-no-readback");

            var waitHandles = new[] { _frameArrived, cancellationToken.WaitHandle };
            var signaled = WaitHandle.WaitAny(waitHandles, FirstFrameTimeout);
            if (signaled == WaitHandle.WaitTimeout)
            {
                throw new CaptureException("WGC_FIRST_FRAME_TIMEOUT", "Timed out waiting for the first cursor-free display frame.");
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        public GpuFrameLease AcquireLatestFrame(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var waitHandles = new[] { _frameArrived, cancellationToken.WaitHandle };
            var waitMilliseconds = Math.Clamp(1000 / Math.Max(1, _options.VideoFps), 1, 500);
            var signaled = WaitHandle.WaitAny(waitHandles, waitMilliseconds);
            cancellationToken.ThrowIfCancellationRequested();
            if (signaled == WaitHandle.WaitTimeout || !TryAcquireLatestFrame(out var frame) || frame is null)
            {
                throw new CaptureException("WGC_FRAME_TIMEOUT", "Timed out waiting for a cursor-free display frame.");
            }

            return frame;
        }

        public bool TryAcquireLatestFrame(out GpuFrameLease? frame)
        {
            frame = null;
            var framePool = _framePool ?? throw new InvalidOperationException("Windows Graphics Capture frame pool has not started.");
            Direct3D11CaptureFrame? latestFrame = null;
            var superseded = 0;
            try
            {
                while (framePool.TryGetNextFrame() is { } nextFrame)
                {
                    if (latestFrame is not null)
                    {
                        latestFrame.Dispose();
                        superseded++;
                    }

                    latestFrame = nextFrame;
                }

                if (latestFrame is null)
                {
                    return false;
                }

                if (superseded > 0)
                {
                    Interlocked.Add(ref _framesDropped, superseded);
                }

                var contentSize = latestFrame.ContentSize;
                if (contentSize.Width != _width || contentSize.Height != _height)
                {
                    throw new CaptureException(
                        "WGC_FRAME_SIZE_CHANGED",
                        $"Windows Graphics Capture frame changed to {contentSize.Width}x{contentSize.Height}; expected {_width}x{_height}.");
                }

                using var sourceTexture = GetTexture(latestFrame.Surface);
                var description = sourceTexture.Description;
                if (description.Width != _width || description.Height != _height || description.Format != Format.B8G8R8A8_UNorm)
                {
                    throw new CaptureException(
                        "WGC_TEXTURE_LAYOUT_INVALID",
                        $"Unexpected Windows Graphics Capture texture {description.Width}x{description.Height} format={description.Format}.");
                }

                description.Usage = ResourceUsage.Default;
                description.BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget;
                description.CPUAccessFlags = CpuAccessFlags.None;
                description.MiscFlags = ResourceOptionFlags.None;
                var ownedTexture = Device.CreateTexture2D(in description);
                try
                {
                    Context.CopyResource(ownedTexture, sourceTexture);
                }
                catch
                {
                    ownedTexture.Dispose();
                    throw;
                }

                var sequence = Interlocked.Increment(ref _sequence);
                var captured = Interlocked.Increment(ref _framesCaptured);
                var timestampQpc = Stopwatch.GetTimestamp();
                Interlocked.Exchange(ref _lastFrameStopwatchTicks, timestampQpc);
                if ((captured % 300) == 0)
                {
                    _log($"cursor-free frames captured={captured} dropped={FramesDropped} sequence={sequence}");
                }

                frame = new GpuFrameLease(ownedTexture, sequence, timestampQpc, ownedTexture.Dispose);
                return true;
            }
            finally
            {
                latestFrame?.Dispose();
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_framePool is not null)
            {
                _framePool.FrameArrived -= OnFrameArrived;
            }

            _session?.Dispose();
            _session = null;
            _framePool?.Dispose();
            _framePool = null;
            _captureItem = null;
            _winRtDevice?.Dispose();
            _winRtDevice = null;
            _context?.Dispose();
            _context = null;
            _device?.Dispose();
            _device = null;
            _adapter?.Dispose();
            _adapter = null;
            _factory?.Dispose();
            _factory = null;
            _frameArrived.Dispose();
        }

        private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            try
            {
                _frameArrived.Set();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void CreateD3DDevice(IntPtr monitor)
        {
            _factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            _adapter = FindAdapterForMonitor(_factory, monitor, out var exactMonitorMatch);
            var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
            var featureLevels = new[]
            {
                FeatureLevel.Level_11_1,
                FeatureLevel.Level_11_0,
                FeatureLevel.Level_10_1,
                FeatureLevel.Level_10_0
            };
            D3D11.D3D11CreateDevice(
                _adapter,
                DriverType.Unknown,
                flags,
                featureLevels,
                out _device,
                out _,
                out _context).CheckError();
            using var multithread = _device.QueryInterface<ID3D11Multithread>();
            multithread.SetMultithreadProtected(true);

            var adapterDescription = _adapter.Description1;
            _log(
                $"WGC D3D11 adapter={adapterDescription.Description} "
                + $"luid=0x{adapterDescription.Luid.HighPart:X8}{adapterDescription.Luid.LowPart:X8} "
                + $"monitorMatch={exactMonitorMatch} multithread=true");
        }

        private static IDXGIAdapter1 FindAdapterForMonitor(IDXGIFactory1 factory, IntPtr monitor, out bool exactMonitorMatch)
        {
            IDXGIAdapter1? firstAdapter = null;
            for (uint adapterIndex = 0; ; adapterIndex++)
            {
                var result = factory.EnumAdapters1(adapterIndex, out var adapter);
                if (result.Failure)
                {
                    break;
                }

                firstAdapter ??= adapter.QueryInterface<IDXGIAdapter1>();
                for (uint outputIndex = 0; ; outputIndex++)
                {
                    var outputResult = adapter.EnumOutputs(outputIndex, out var output);
                    if (outputResult.Failure)
                    {
                        break;
                    }

                    using (output)
                    {
                        if (output.Description.Monitor == monitor)
                        {
                            firstAdapter?.Dispose();
                            exactMonitorMatch = true;
                            return adapter;
                        }
                    }
                }

                adapter.Dispose();
            }

            exactMonitorMatch = false;
            return firstAdapter ?? throw new CaptureException("WGC_ADAPTER_NOT_FOUND", "No D3D11 adapter is available for display capture.");
        }

        private static WinRtDirect3DDevice CreateWinRtDevice(ID3D11Device device)
        {
            using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
            var hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var inspectable);
            Marshal.ThrowExceptionForHR(hr);
            try
            {
                return MarshalInterface<WinRtDirect3DDevice>.FromAbi(inspectable);
            }
            finally
            {
                Marshal.Release(inspectable);
            }
        }

        private static GraphicsCaptureItem CreateCaptureItemForMonitor(IntPtr monitor)
        {
            var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
            var iid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");

            var itemPointer = interop.CreateForMonitor(monitor, ref iid);
            try
            {
                return MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPointer);
            }
            finally
            {
                Marshal.Release(itemPointer);
            }
        }

        private static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
        {
            var access = WinRT.CastExtensions.As<IDirect3DDxgiInterfaceAccess>(surface);
            var iid = IidId3D11Texture2D;
            var pointer = access.GetInterface(ref iid);
            return new ID3D11Texture2D(pointer);
        }

        [DllImport("d3d11.dll", ExactSpelling = true)]
        private static extern int CreateDirect3D11DeviceFromDXGIDevice(
            IntPtr dxgiDevice,
            out IntPtr graphicsDevice);

        [ComImport]
        [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [ComVisible(true)]
        private interface IGraphicsCaptureItemInterop
        {
            IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);

            IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
        }

        [ComImport]
        [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [ComVisible(true)]
        private interface IDirect3DDxgiInterfaceAccess
        {
            IntPtr GetInterface([In] ref Guid iid);
        }
    }
}
