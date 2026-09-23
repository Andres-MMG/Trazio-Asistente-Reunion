using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Win32;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Trazio.AsistenteReunion.App;

internal interface IVisualProbeExtractor
{
    VisualProbeFeatureLease Extract(
        IDirect3DSurface surface,
        VisualProbeLayoutProfile profile,
        TimeSpan offset,
        long surfaceRevision);
}

internal sealed class D3D11VisualProbeExtractor : IVisualProbeExtractor
{
    public const int MaxPatches = 16;
    public const int AtlasWidth = MaxPatches * VisualProbePatch.Width;
    public const int AtlasHeight = VisualProbePatch.Height;

    private const int BytesPerPixel = 4;
    private const int PixelsPerPatch = VisualProbePatch.Width * VisualProbePatch.Height;

    private readonly object _gate = new();
    private readonly IVisualProbeNativeSurfaceFactory _nativeSurfaceFactory;
    private readonly ArrayPool<VisualProbeFeature> _featurePool;

    public D3D11VisualProbeExtractor()
        : this(new D3D11VisualProbeNativeSurfaceFactory(), ArrayPool<VisualProbeFeature>.Shared)
    {
    }

    internal D3D11VisualProbeExtractor(
        IVisualProbeNativeSurfaceFactory nativeSurfaceFactory,
        ArrayPool<VisualProbeFeature>? featurePool = null)
    {
        _nativeSurfaceFactory = nativeSurfaceFactory ?? throw new ArgumentNullException(nameof(nativeSurfaceFactory));
        _featurePool = featurePool ?? ArrayPool<VisualProbeFeature>.Shared;
    }

    public VisualProbeFeatureLease Extract(
        IDirect3DSurface surface,
        VisualProbeLayoutProfile profile,
        TimeSpan offset,
        long surfaceRevision)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (profile.ValidationState != VisualProbeProfileValidationState.Validated)
        {
            return VisualProbeFeatureLease.Create(
                VisualProbeObservation.Unavailable(offset, surfaceRevision, profile),
                [],
                _featurePool);
        }

        ValidateProfileLayout(profile);
        ArgumentNullException.ThrowIfNull(surface);
        lock (_gate)
        {
            return ExtractValidated(surface, profile, offset, surfaceRevision);
        }
    }

    private VisualProbeFeatureLease ExtractValidated(
        IDirect3DSurface surface,
        VisualProbeLayoutProfile profile,
        TimeSpan offset,
        long surfaceRevision)
    {
        using var nativeSurface = _nativeSurfaceFactory.Open(surface);
        ValidateSurface(nativeSurface.Description, profile);

        for (var index = 0; index < profile.Patches.Count; index++)
            nativeSurface.CopyPatchToAtlas(index, profile.Patches[index]);

        VisualProbeMappedAtlas mappedAtlas = default;
        var mapped = false;
        try
        {
            mappedAtlas = nativeSurface.MapAtlas();
            mapped = true;
            ValidateMappedAtlas(mappedAtlas);

            Span<VisualProbeFeature> features = stackalloc VisualProbeFeature[profile.Patches.Count];
            for (var index = 0; index < features.Length; index++)
                features[index] = ReducePatch(mappedAtlas, index, profile.Patches[index]);

            return VisualProbeFeatureLease.Create(
                VisualProbeObservation.Available(offset, surfaceRevision, profile),
                features,
                _featurePool);
        }
        finally
        {
            if (mapped)
            {
                try
                {
                    ZeroMappedAtlas(mappedAtlas);
                }
                finally
                {
                    nativeSurface.UnmapAtlas();
                }
            }
        }
    }

    private static void ValidateSurface(
        VisualProbeSurfaceDescription surface,
        VisualProbeLayoutProfile profile)
    {
        if (surface.Format != VisualProbeSurfaceFormat.B8G8R8A8Unorm)
            throw new VisualProbeExtractionException(VisualProbeExtractionFailure.UnsupportedFormat);
        if (surface.SampleCount != 1)
            throw new VisualProbeExtractionException(VisualProbeExtractionFailure.UnsupportedSampleCount);
        if (surface.Width == 0 || surface.Height == 0 ||
            surface.Width > int.MaxValue || surface.Height > int.MaxValue)
        {
            throw new VisualProbeExtractionException(VisualProbeExtractionFailure.InvalidDimensions);
        }
        if (surface.Width != profile.ExpectedSurfaceWidth ||
            surface.Height != profile.ExpectedSurfaceHeight)
        {
            throw new VisualProbeExtractionException(VisualProbeExtractionFailure.DimensionMismatch);
        }
    }

    private static void ValidateProfileLayout(VisualProbeLayoutProfile profile)
    {
        if (profile.Patches.Count is 0 or > MaxPatches)
            throw new VisualProbeExtractionException(VisualProbeExtractionFailure.InvalidLayout);

        foreach (var patch in profile.Patches)
        {
            if (patch.Left > profile.ExpectedSurfaceWidth - VisualProbePatch.Width ||
                patch.Top > profile.ExpectedSurfaceHeight - VisualProbePatch.Height)
            {
                throw new VisualProbeExtractionException(VisualProbeExtractionFailure.PatchOutOfBounds);
            }
        }
    }

    private static void ValidateMappedAtlas(VisualProbeMappedAtlas mappedAtlas)
    {
        if (mappedAtlas.Data == 0 || mappedAtlas.RowPitch < AtlasWidth * BytesPerPixel)
            throw new VisualProbeExtractionException(VisualProbeExtractionFailure.InvalidMappedMemory);

        var byteCount = checked((ulong)mappedAtlas.RowPitch * AtlasHeight);
        if (byteCount > int.MaxValue)
            throw new VisualProbeExtractionException(VisualProbeExtractionFailure.InvalidMappedMemory);
    }

    private static unsafe VisualProbeFeature ReducePatch(
        VisualProbeMappedAtlas atlas,
        int patchIndex,
        VisualProbePatch patch)
    {
        var highlightMatches = 0;
        var nonBlackPixels = 0;
        var lumaSum = 0d;
        var patchLeft = patchIndex * VisualProbePatch.Width;

        for (var y = 0; y < VisualProbePatch.Height; y++)
        {
            var row = (byte*)atlas.Data + checked((nuint)y * atlas.RowPitch);
            for (var x = 0; x < VisualProbePatch.Width; x++)
            {
                var pixel = row + (patchLeft + x) * BytesPerPixel;
                var blue = pixel[0];
                var green = pixel[1];
                var red = pixel[2];

                if (MatchesHighlight(blue, green, red, patch)) highlightMatches++;
                if ((blue | green | red) != 0) nonBlackPixels++;
                lumaSum += 0.2126d * red + 0.7152d * green + 0.0722d * blue;
            }
        }

        return new VisualProbeFeature(
            highlightMatches / (double)PixelsPerPatch,
            nonBlackPixels / (double)PixelsPerPatch,
            lumaSum / (PixelsPerPatch * byte.MaxValue));
    }

    private static bool MatchesHighlight(
        byte blue,
        byte green,
        byte red,
        VisualProbePatch patch) =>
        Math.Abs(blue - patch.HighlightBlue) <= patch.HighlightTolerance &&
        Math.Abs(green - patch.HighlightGreen) <= patch.HighlightTolerance &&
        Math.Abs(red - patch.HighlightRed) <= patch.HighlightTolerance;

    private static unsafe void ZeroMappedAtlas(VisualProbeMappedAtlas mappedAtlas)
    {
        if (mappedAtlas.Data == 0 || mappedAtlas.RowPitch == 0) return;

        var byteCount = (ulong)mappedAtlas.RowPitch * AtlasHeight;
        if (byteCount > int.MaxValue) return;
        CryptographicOperations.ZeroMemory(new Span<byte>((void*)mappedAtlas.Data, (int)byteCount));
    }
}

internal enum VisualProbeExtractionFailure
{
    UnsupportedFormat = 0,
    UnsupportedSampleCount = 1,
    InvalidDimensions = 2,
    DimensionMismatch = 3,
    InvalidLayout = 4,
    PatchOutOfBounds = 5,
    InvalidMappedMemory = 6
}

internal sealed class VisualProbeExtractionException : InvalidOperationException
{
    public VisualProbeExtractionException(VisualProbeExtractionFailure failure)
        : base($"The visual probe surface was rejected: {failure}.") => Failure = failure;

    public VisualProbeExtractionFailure Failure { get; }
}

internal enum VisualProbeSurfaceFormat
{
    Unsupported = 0,
    B8G8R8A8Unorm = 1
}

internal readonly record struct VisualProbeSurfaceDescription(
    uint Width,
    uint Height,
    VisualProbeSurfaceFormat Format,
    uint SampleCount);

internal readonly record struct VisualProbeMappedAtlas(nint Data, uint RowPitch);

internal interface IVisualProbeNativeSurfaceFactory
{
    IVisualProbeNativeSurface Open(IDirect3DSurface surface);
}

internal interface IVisualProbeNativeSurface : IDisposable
{
    VisualProbeSurfaceDescription Description { get; }
    void CopyPatchToAtlas(int patchIndex, VisualProbePatch patch);
    VisualProbeMappedAtlas MapAtlas();
    void UnmapAtlas();
}

internal sealed class D3D11VisualProbeNativeSurfaceFactory : IVisualProbeNativeSurfaceFactory
{
    private static readonly Guid Texture2DInterfaceId =
        new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    public IVisualProbeNativeSurface Open(IDirect3DSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);

        IDirect3DDxgiInterfaceAccess? access = null;
        ID3D11Texture2D? texture = null;
        nint accessPointer = 0;
        nint texturePointer = 0;
        try
        {
            var winRtObject = (WinRT.IWinRTObject)surface;
            var accessInterfaceId = typeof(IDirect3DDxgiInterfaceAccess).GUID;
            Marshal.ThrowExceptionForHR(winRtObject.NativeObject.TryAs(
                accessInterfaceId,
                out accessPointer));
            access = (IDirect3DDxgiInterfaceAccess)Marshal.GetTypedObjectForIUnknown(
                accessPointer,
                typeof(IDirect3DDxgiInterfaceAccess));
            var interfaceId = Texture2DInterfaceId;
            texturePointer = access.GetInterface(ref interfaceId);
            if (texturePointer == 0) throw new InvalidOperationException("The Direct3D surface returned no texture.");

            texture = (ID3D11Texture2D)Marshal.GetObjectForIUnknown(texturePointer);
            var nativeSurface = D3D11VisualProbeNativeSurface.Create(texture);
            texture = null;
            return nativeSurface;
        }
        finally
        {
            if (texturePointer != 0) Marshal.Release(texturePointer);
            if (accessPointer != 0) Marshal.Release(accessPointer);
            ReleaseComReference(texture);
            ReleaseComReference(access);
        }
    }

    private static void ReleaseComReference(object? value)
    {
        try
        {
            if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
        }
        catch
        {
        }
    }
}

internal sealed class D3D11VisualProbeNativeSurface : IVisualProbeNativeSurface
{
    private readonly ID3D11Texture2D _sourceTexture;
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private ID3D11Texture2D? _stagingAtlas;
    private int _disposed;
    private bool _mapped;

    private D3D11VisualProbeNativeSurface(
        ID3D11Texture2D sourceTexture,
        ID3D11Device device,
        ID3D11DeviceContext context,
        D3D11_TEXTURE2D_DESC sourceDescription)
    {
        _sourceTexture = sourceTexture;
        _device = device;
        _context = context;
        Description = new(
            sourceDescription.Width,
            sourceDescription.Height,
            sourceDescription.Format == DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM
                ? VisualProbeSurfaceFormat.B8G8R8A8Unorm
                : VisualProbeSurfaceFormat.Unsupported,
            sourceDescription.SampleDesc.Count);
    }

    public VisualProbeSurfaceDescription Description { get; }

    public static D3D11VisualProbeNativeSurface Create(ID3D11Texture2D sourceTexture)
    {
        ID3D11Device? device = null;
        ID3D11DeviceContext? context = null;
        try
        {
            sourceTexture.GetDesc(out var description);
            sourceTexture.GetDevice(out device);
            device.GetImmediateContext(out context);
            var surface = new D3D11VisualProbeNativeSurface(sourceTexture, device, context, description);
            device = null;
            context = null;
            return surface;
        }
        finally
        {
            ReleaseComReference(context);
            ReleaseComReference(device);
        }
    }

    public void CopyPatchToAtlas(int patchIndex, VisualProbePatch patch)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (patchIndex is < 0 or >= D3D11VisualProbeExtractor.MaxPatches)
            throw new ArgumentOutOfRangeException(nameof(patchIndex));

        EnsureStagingAtlas();
        var sourceBox = new D3D11_BOX
        {
            left = checked((uint)patch.Left),
            top = checked((uint)patch.Top),
            front = 0,
            right = checked((uint)(patch.Left + VisualProbePatch.Width)),
            bottom = checked((uint)(patch.Top + VisualProbePatch.Height)),
            back = 1
        };
        _context.CopySubresourceRegion(
            (ID3D11Resource)_stagingAtlas!,
            0,
            checked((uint)(patchIndex * VisualProbePatch.Width)),
            0,
            0,
            (ID3D11Resource)_sourceTexture,
            0,
            sourceBox);
    }

    public unsafe VisualProbeMappedAtlas MapAtlas()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_mapped) throw new InvalidOperationException("The staging atlas is already mapped.");
        if (_stagingAtlas is null) throw new InvalidOperationException("No patches were copied to the staging atlas.");

        _context.Map(
            (ID3D11Resource)_stagingAtlas,
            0,
            D3D11_MAP.D3D11_MAP_READ_WRITE,
            0,
            out var mapped);
        _mapped = true;
        return new((nint)mapped.pData, mapped.RowPitch);
    }

    public void UnmapAtlas()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_mapped || _stagingAtlas is null)
            throw new InvalidOperationException("The staging atlas is not mapped.");

        try
        {
            _context.Unmap((ID3D11Resource)_stagingAtlas, 0);
        }
        finally
        {
            _mapped = false;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        if (_mapped && _stagingAtlas is not null)
        {
            try
            {
                _context.Unmap((ID3D11Resource)_stagingAtlas, 0);
            }
            catch
            {
            }
            _mapped = false;
        }

        ReleaseComReference(_stagingAtlas);
        _stagingAtlas = null;
        ReleaseComReference(_context);
        ReleaseComReference(_device);
        ReleaseComReference(_sourceTexture);
    }

    private void EnsureStagingAtlas()
    {
        if (_stagingAtlas is not null) return;

        var description = new D3D11_TEXTURE2D_DESC
        {
            Width = D3D11VisualProbeExtractor.AtlasWidth,
            Height = D3D11VisualProbeExtractor.AtlasHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new() { Count = 1, Quality = 0 },
            Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
            BindFlags = 0,
            CPUAccessFlags =
                D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ |
                D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE,
            MiscFlags = 0
        };
        _device.CreateTexture2D(description, null, out var stagingAtlas);
        _stagingAtlas = stagingAtlas;
    }

    private static void ReleaseComReference(object? value)
    {
        try
        {
            if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
        }
        catch
        {
        }
    }
}

[ComImport]
[Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[ComVisible(true)]
internal interface IDirect3DDxgiInterfaceAccess
{
    nint GetInterface([In] ref Guid interfaceId);
}
