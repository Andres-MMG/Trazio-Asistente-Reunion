using System.Buffers;
using System.Runtime.InteropServices;
using Trazio.AsistenteReunion.App;
using Trazio.AsistenteReunion.Core;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Win32;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Trazio.AsistenteReunion.Tests;

public sealed class D3D11VisualProbeExtractorTests
{
    [Theory]
    [InlineData((int)VisualProbeProfileValidationState.Unsupported)]
    [InlineData((int)VisualProbeProfileValidationState.Unvalidated)]
    [Trait("Area", "VisualCapture")]
    public void Extract_WithNonValidatedProfile_AbstainsBeforeAccessingSurface(
        int validationStateValue)
    {
        var validationState = (VisualProbeProfileValidationState)validationStateValue;
        var factory = new RecordingNativeSurfaceFactory();
        var extractor = new D3D11VisualProbeExtractor(factory);
        var profile = Profile(validationState, width: 16, height: 16, Patch(0, 0));

        using var lease = extractor.Extract(
            surface: null!,
            profile,
            offset: TimeSpan.FromSeconds(3),
            surfaceRevision: 4);

        Assert.Equal(0, factory.OpenCount);
        Assert.Equal(VisualProbeObservationStatus.Unavailable, lease.Observation.Status);
        Assert.Empty(lease.Features.ToArray());
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void Extract_WithPaddedAtlas_AggregatesBgraAndClearsMappedMemoryBeforeUnmap()
    {
        const int rowPitch = D3D11VisualProbeExtractor.AtlasWidth * 4 + 32;
        var nativeSurface = new RecordingNativeSurface(
            new(32, 16, VisualProbeSurfaceFormat.B8G8R8A8Unorm, 1),
            rowPitch);
        nativeSurface.Fill(0xA5);
        nativeSurface.FillPatch(0, blue: 0, green: 0, red: 255);
        nativeSurface.FillPatchRows(1, 0, 8, blue: 255, green: 255, red: 255);
        nativeSurface.FillPatchRows(1, 8, 16, blue: 0, green: 0, red: 0);
        var factory = new RecordingNativeSurfaceFactory(nativeSurface);
        var pool = new TrackingFeaturePool();
        var extractor = new D3D11VisualProbeExtractor(factory, pool);
        var profile = Profile(
            VisualProbeProfileValidationState.Validated,
            width: 32,
            height: 16,
            Patch(0, 0, red: 255),
            Patch(16, 0, blue: 255, green: 255, red: 255));

        var lease = extractor.Extract(
            new StubDirect3DSurface(),
            profile,
            TimeSpan.FromSeconds(1),
            surfaceRevision: 7);

        var features = lease.Features.ToArray();
        Assert.Equal(2, features.Length);
        Assert.Equal(1d, features[0].HighlightMatchRatio);
        Assert.Equal(1d, features[0].NonBlackRatio);
        Assert.Equal(0.2126d, features[0].MeanLuma, precision: 4);
        Assert.Equal(1_000, features[0].ActivityScore);
        Assert.Equal(0.5d, features[1].HighlightMatchRatio);
        Assert.Equal(0.5d, features[1].NonBlackRatio);
        Assert.Equal(0.5d, features[1].MeanLuma, precision: 4);
        Assert.Equal(500, features[1].ActivityScore);
        Assert.Equal([0, 1], nativeSurface.CopiedPatchIndexes);
        Assert.Equal(1, nativeSurface.MapCount);
        Assert.Equal(1, nativeSurface.UnmapCount);
        Assert.True(nativeSurface.WasZeroedAtUnmap);
        Assert.True(nativeSurface.Disposed);

        lease.Dispose();

        Assert.Equal(1, pool.ReturnCount);
        Assert.All(pool.ReturnedSnapshot!, feature => Assert.Equal(default, feature));
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void Extract_WithOutOfBoundsValidatedLayout_RejectsBeforeAccessingSurface()
    {
        var factory = new RecordingNativeSurfaceFactory();
        var extractor = new D3D11VisualProbeExtractor(factory);
        var profile = Profile(
            VisualProbeProfileValidationState.Validated,
            width: 16,
            height: 16,
            Patch(1, 0));

        var exception = Assert.Throws<VisualProbeExtractionException>(() => extractor.Extract(
            surface: null!,
            profile,
            TimeSpan.Zero,
            surfaceRevision: 0));

        Assert.Equal(VisualProbeExtractionFailure.PatchOutOfBounds, exception.Failure);
        Assert.Equal(0, factory.OpenCount);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void Extract_WithInvalidSurface_RejectsBeforeCopyAndDisposesNativeResources()
    {
        foreach (var testCase in InvalidSurfaceCases())
        {
            var nativeSurface = new RecordingNativeSurface(testCase.Description);
            var extractor = new D3D11VisualProbeExtractor(new RecordingNativeSurfaceFactory(nativeSurface));
            var profile = Profile(
                VisualProbeProfileValidationState.Validated,
                testCase.ProfileWidth,
                testCase.ProfileHeight,
                testCase.Patch);

            var exception = Assert.Throws<VisualProbeExtractionException>(() => extractor.Extract(
                new StubDirect3DSurface(),
                profile,
                TimeSpan.Zero,
                surfaceRevision: 0));

            Assert.Equal(testCase.ExpectedFailure, exception.Failure);
            Assert.Empty(nativeSurface.CopiedPatchIndexes);
            Assert.Equal(0, nativeSurface.MapCount);
            Assert.True(nativeSurface.Disposed);
        }
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void Extract_WhenMappedRowPitchIsTooSmall_ZeroesAccessibleMemoryAndAlwaysUnmaps()
    {
        var nativeSurface = new RecordingNativeSurface(
            new(16, 16, VisualProbeSurfaceFormat.B8G8R8A8Unorm, 1),
            rowPitch: 16 * 4 - 1);
        nativeSurface.Fill(0xCC);
        var extractor = new D3D11VisualProbeExtractor(new RecordingNativeSurfaceFactory(nativeSurface));

        var exception = Assert.Throws<VisualProbeExtractionException>(() => extractor.Extract(
            new StubDirect3DSurface(),
            Profile(VisualProbeProfileValidationState.Validated, 16, 16, Patch(0, 0)),
            TimeSpan.Zero,
            surfaceRevision: 0));

        Assert.Equal(VisualProbeExtractionFailure.InvalidMappedMemory, exception.Failure);
        Assert.Equal(1, nativeSurface.MapCount);
        Assert.Equal(1, nativeSurface.UnmapCount);
        Assert.True(nativeSurface.WasZeroedAtUnmap);
        Assert.True(nativeSurface.Disposed);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [Trait("Area", "VisualCapture")]
    public void Extract_WhenNativeOperationThrows_DisposesResourcesAndUnmapsOnlyAfterSuccessfulMap(
        bool throwOnCopy,
        bool throwOnMap)
    {
        var nativeSurface = new RecordingNativeSurface(
            new(16, 16, VisualProbeSurfaceFormat.B8G8R8A8Unorm, 1))
        {
            ThrowOnCopy = throwOnCopy,
            ThrowOnMap = throwOnMap
        };
        var extractor = new D3D11VisualProbeExtractor(new RecordingNativeSurfaceFactory(nativeSurface));

        Assert.Throws<InvalidOperationException>(() => extractor.Extract(
            new StubDirect3DSurface(),
            Profile(VisualProbeProfileValidationState.Validated, 16, 16, Patch(0, 0)),
            TimeSpan.Zero,
            surfaceRevision: 0));

        Assert.Equal(0, nativeSurface.UnmapCount);
        Assert.True(nativeSurface.Disposed);
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public void LayoutProfile_WithMoreThanSixteenPatches_IsRejected()
    {
        var patches = Enumerable.Range(0, D3D11VisualProbeExtractor.MaxPatches + 1)
            .Select(index => Patch(index * VisualProbePatch.Width, 0))
            .ToArray();

        Assert.Throws<ArgumentOutOfRangeException>(() => Profile(
            VisualProbeProfileValidationState.Validated,
            patches.Length * VisualProbePatch.Width,
            VisualProbePatch.Height,
            patches));
    }

    [Fact]
    [Trait("Area", "VisualCapture")]
    public unsafe void Extract_FromWarpBackedDirect3DSurface_ReadsOnlyTheConfiguredPatch()
    {
        using var fixture = WarpSurfaceFixture.Create(
            width: 32,
            height: 16,
            (x, _) => x < 16
                ? (Blue: (byte)10, Green: (byte)20, Red: (byte)200, Alpha: byte.MaxValue)
                : (Blue: (byte)1, Green: (byte)2, Red: (byte)3, Alpha: byte.MaxValue));
        var extractor = new D3D11VisualProbeExtractor();
        var profile = Profile(
            VisualProbeProfileValidationState.Validated,
            32,
            16,
            Patch(0, 0, blue: 10, green: 20, red: 200));

        using var lease = extractor.Extract(
            fixture.Surface,
            profile,
            TimeSpan.Zero,
            surfaceRevision: 1);

        var feature = Assert.Single(lease.Features.ToArray());
        Assert.Equal(1d, feature.HighlightMatchRatio);
        Assert.Equal(1d, feature.NonBlackRatio);
        Assert.Equal((0.2126d * 200 + 0.7152d * 20 + 0.0722d * 10) / 255d, feature.MeanLuma, 6);
    }

    private static IEnumerable<InvalidSurfaceCase> InvalidSurfaceCases()
    {
        yield return new(
                new(16, 16, VisualProbeSurfaceFormat.Unsupported, 1),
                16,
                16,
                Patch(0, 0),
                VisualProbeExtractionFailure.UnsupportedFormat);
        yield return new(
                new(16, 16, VisualProbeSurfaceFormat.B8G8R8A8Unorm, 4),
                16,
                16,
                Patch(0, 0),
                VisualProbeExtractionFailure.UnsupportedSampleCount);
        yield return new(
                new(0, 16, VisualProbeSurfaceFormat.B8G8R8A8Unorm, 1),
                16,
                16,
                Patch(0, 0),
                VisualProbeExtractionFailure.InvalidDimensions);
        yield return new(
                new(32, 16, VisualProbeSurfaceFormat.B8G8R8A8Unorm, 1),
                16,
                16,
                Patch(0, 0),
                VisualProbeExtractionFailure.DimensionMismatch);
    }

    private static VisualProbeLayoutProfile Profile(
        VisualProbeProfileValidationState validationState,
        int width,
        int height,
        params VisualProbePatch[] patches) =>
        new(
            MeetingProvider.GoogleMeet,
            version: 7,
            validationState,
            expectedSurfaceWidth: width,
            expectedSurfaceHeight: height,
            patches,
            validationState == VisualProbeProfileValidationState.Validated
                ? TestPolicy(patches.Length)
                : null);

    private static VisualProbePatch Patch(
        int left,
        int top,
        byte blue = 0,
        byte green = 0,
        byte red = 0) =>
        new(left, top, blue, green, red, highlightTolerance: 0);

    private static VisualProbeDetectionPolicy TestPolicy(int patchCount) => new(
        enterThreshold: 800,
        exitThreshold: 200,
        activationHold: TimeSpan.Zero,
        releaseHold: TimeSpan.Zero,
        maxObservationGap: TimeSpan.FromSeconds(2),
        minimumCoherentPatches: Math.Max(1, patchCount),
        evidenceVersion: 1,
        detectorVersion: 1,
        policyVersion: 1);

    private sealed class StubDirect3DSurface : IDirect3DSurface
    {
        public Direct3DSurfaceDescription Description => default;
        public void Dispose()
        {
        }
    }

    private sealed class RecordingNativeSurfaceFactory : IVisualProbeNativeSurfaceFactory
    {
        private readonly IVisualProbeNativeSurface? _surface;

        public RecordingNativeSurfaceFactory(IVisualProbeNativeSurface? surface = null) =>
            _surface = surface;

        public int OpenCount { get; private set; }

        public IVisualProbeNativeSurface Open(IDirect3DSurface surface)
        {
            OpenCount++;
            return _surface ?? throw new InvalidOperationException("Surface access was not expected.");
        }
    }

    private sealed class RecordingNativeSurface : IVisualProbeNativeSurface
    {
        private readonly int _rowPitch;
        private nint _memory;

        public RecordingNativeSurface(
            VisualProbeSurfaceDescription description,
            int rowPitch = D3D11VisualProbeExtractor.AtlasWidth * 4)
        {
            Description = description;
            _rowPitch = rowPitch;
            _memory = Marshal.AllocHGlobal(checked(rowPitch * D3D11VisualProbeExtractor.AtlasHeight));
        }

        public VisualProbeSurfaceDescription Description { get; }
        public List<int> CopiedPatchIndexes { get; } = [];
        public int MapCount { get; private set; }
        public int UnmapCount { get; private set; }
        public bool WasZeroedAtUnmap { get; private set; }
        public bool Disposed { get; private set; }
        public bool ThrowOnCopy { get; init; }
        public bool ThrowOnMap { get; init; }

        public void CopyPatchToAtlas(int patchIndex, VisualProbePatch patch)
        {
            if (ThrowOnCopy) throw new InvalidOperationException("copy failed");
            CopiedPatchIndexes.Add(patchIndex);
        }

        public VisualProbeMappedAtlas MapAtlas()
        {
            if (ThrowOnMap) throw new InvalidOperationException("map failed");
            MapCount++;
            return new(_memory, checked((uint)_rowPitch));
        }

        public void UnmapAtlas()
        {
            UnmapCount++;
            var snapshot = new byte[checked(_rowPitch * D3D11VisualProbeExtractor.AtlasHeight)];
            Marshal.Copy(_memory, snapshot, 0, snapshot.Length);
            WasZeroedAtUnmap = snapshot.All(value => value == 0);
        }

        public void Fill(byte value)
        {
            for (var index = 0; index < checked(_rowPitch * D3D11VisualProbeExtractor.AtlasHeight); index++)
                Marshal.WriteByte(_memory, index, value);
        }

        public void FillPatch(int patchIndex, byte blue, byte green, byte red) =>
            FillPatchRows(patchIndex, 0, VisualProbePatch.Height, blue, green, red);

        public void FillPatchRows(
            int patchIndex,
            int firstRow,
            int exclusiveLastRow,
            byte blue,
            byte green,
            byte red)
        {
            for (var y = firstRow; y < exclusiveLastRow; y++)
            {
                for (var x = 0; x < VisualProbePatch.Width; x++)
                {
                    var offset = checked(y * _rowPitch + (patchIndex * VisualProbePatch.Width + x) * 4);
                    Marshal.WriteByte(_memory, offset, blue);
                    Marshal.WriteByte(_memory, offset + 1, green);
                    Marshal.WriteByte(_memory, offset + 2, red);
                    Marshal.WriteByte(_memory, offset + 3, byte.MaxValue);
                }
            }
        }

        public void Dispose()
        {
            Disposed = true;
            var memory = Interlocked.Exchange(ref _memory, 0);
            if (memory != 0) Marshal.FreeHGlobal(memory);
        }
    }

    private sealed class TrackingFeaturePool : ArrayPool<VisualProbeFeature>
    {
        public int ReturnCount { get; private set; }
        public VisualProbeFeature[]? ReturnedSnapshot { get; private set; }

        public override VisualProbeFeature[] Rent(int minimumLength) =>
            new VisualProbeFeature[Math.Max(minimumLength, 4)];

        public override void Return(VisualProbeFeature[] array, bool clearArray = false)
        {
            ReturnCount++;
            ReturnedSnapshot = [.. array];
        }
    }

    private sealed record InvalidSurfaceCase(
        VisualProbeSurfaceDescription Description,
        int ProfileWidth,
        int ProfileHeight,
        VisualProbePatch Patch,
        VisualProbeExtractionFailure ExpectedFailure);

    private sealed class WarpSurfaceFixture : IDisposable
    {
        private readonly ID3D11Texture2D _texture;
        private readonly ID3D11DeviceContext _context;
        private readonly ID3D11Device _device;

        private WarpSurfaceFixture(
            IDirect3DSurface surface,
            ID3D11Texture2D texture,
            ID3D11DeviceContext context,
            ID3D11Device device)
        {
            Surface = surface;
            _texture = texture;
            _context = context;
            _device = device;
        }

        public IDirect3DSurface Surface { get; }

        public static unsafe WarpSurfaceFixture Create(
            int width,
            int height,
            Func<int, int, (byte Blue, byte Green, byte Red, byte Alpha)> pixel)
        {
            var result = PInvoke.D3D11CreateDevice(
                default!,
                D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_WARP,
                default,
                D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                ReadOnlySpan<D3D_FEATURE_LEVEL>.Empty,
                PInvoke.D3D11_SDK_VERSION,
                out var device,
                out var context);
            result.ThrowOnFailure();

            ID3D11Texture2D? texture = null;
            Windows.Win32.System.WinRT.IInspectable? inspectable = null;
            try
            {
                var pixels = new byte[checked(width * height * 4)];
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var value = pixel(x, y);
                        var offset = (y * width + x) * 4;
                        pixels[offset] = value.Blue;
                        pixels[offset + 1] = value.Green;
                        pixels[offset + 2] = value.Red;
                        pixels[offset + 3] = value.Alpha;
                    }
                }

                fixed (byte* pixelsPointer = pixels)
                {
                    var description = new D3D11_TEXTURE2D_DESC
                    {
                        Width = checked((uint)width),
                        Height = checked((uint)height),
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                        SampleDesc = new() { Count = 1, Quality = 0 },
                        Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
                        BindFlags = 0,
                        CPUAccessFlags = 0,
                        MiscFlags = 0
                    };
                    var initialData = new D3D11_SUBRESOURCE_DATA
                    {
                        pSysMem = pixelsPointer,
                        SysMemPitch = checked((uint)(width * 4)),
                        SysMemSlicePitch = checked((uint)pixels.Length)
                    };
                    device.CreateTexture2D(description, initialData, out texture);
                }

                var dxgiSurface = (IDXGISurface)texture;
                PInvoke.CreateDirect3D11SurfaceFromDXGISurface(dxgiSurface, out inspectable)
                    .ThrowOnFailure();
                var inspectablePointer = Marshal.GetIUnknownForObject(inspectable);
                try
                {
                    var surface = WinRT.MarshalInterface<IDirect3DSurface>.FromAbi(inspectablePointer);
                    return new(surface, texture, context, device);
                }
                finally
                {
                    Marshal.Release(inspectablePointer);
                }
            }
            catch
            {
                ReleaseComObject(texture);
                ReleaseComObject(context);
                ReleaseComObject(device);
                throw;
            }
            finally
            {
                ReleaseComObject(inspectable);
            }
        }

        public void Dispose()
        {
            Surface.Dispose();
            ReleaseComObject(_texture);
            ReleaseComObject(_context);
            ReleaseComObject(_device);
        }

        private static void ReleaseComObject(object? value)
        {
            if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
        }
    }
}
