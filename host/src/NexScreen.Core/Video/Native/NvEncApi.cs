using System.Runtime.InteropServices;

namespace NexScreen.Core.Video.Native;

internal sealed class NvEncException(string message, int status) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// NVENC (nvEncodeAPI64.dll, la instala el driver de NVIDIA). Las estructuras de la API son enormes
/// y llevan versión, así que no se declaran como structs de C#: se arman en memoria no administrada
/// escribiendo solo los campos que importan, en los desplazamientos de nvEncodeAPI.h 11.1 (la API
/// que entienden los drivers desde la serie 470). Los desplazamientos están calculados con las
/// reglas de alineación de C en x64 y se verifican contra el driver al abrir la sesión.
/// </summary>
internal sealed unsafe class NvEncApi
{
    private const uint ApiVersion = 11 | (1u << 24);
    private const int FunctionListSize = 8 + (41 + 277) * 8;

    public static uint StructVersion(uint version) => ApiVersion | (version << 16) | (0x7u << 28);

    // --- GUID de la API (nvEncodeAPI.h)
    public static readonly Guid CodecH264 = new("6bc82762-4e63-4ca4-aa85-1e50f321f6bf");
    public static readonly Guid PresetP1 = new("fc0a8d3e-45f8-4cf8-80c7-298871590ebf");
    public static readonly Guid ProfileH264Main = new("60b5c1d4-67fe-4790-94d5-c4726d7b6e6d");

    // --- Constantes
    public const int StatusSuccess = 0;
    public const int StatusNeedMoreInput = 17;
    public const uint TuningUltraLowLatency = 3;
    public const uint BufferFormatNv12 = 1;
    public const uint RcModeCbr = 2;
    public const uint PicFlagForceIdr = 0x2;
    public const uint PicFlagOutputSpsPps = 0x4;
    public const uint PicTypeIdr = 3;

    // --- Índices en NV_ENCODE_API_FUNCTION_LIST (después de version y reserved)
    private const int FnGetEncodeCaps = 7;
    private const int FnInitializeEncoder = 11;
    private const int FnCreateBitstreamBuffer = 14;
    private const int FnDestroyBitstreamBuffer = 15;
    private const int FnEncodePicture = 16;
    private const int FnLockBitstream = 17;
    private const int FnUnlockBitstream = 18;
    private const int FnGetSequenceParams = 22;
    private const int FnMapInputResource = 25;
    private const int FnUnmapInputResource = 26;
    private const int FnDestroyEncoder = 27;
    private const int FnOpenEncodeSessionEx = 29;
    private const int FnRegisterResource = 30;
    private const int FnUnregisterResource = 31;
    private const int FnReconfigureEncoder = 32;
    private const int FnGetLastErrorString = 37;
    private const int FnGetEncodePresetConfigEx = 39;

    private readonly byte* _list;

    private NvEncApi(byte* list) => _list = list;

    /// <summary>Carga la DLL del driver y pide la lista de funciones. Null si no hay NVENC (otra GPU, driver viejo).</summary>
    public static NvEncApi? TryLoad(out string error)
    {
        error = "";
        if (!NativeLibrary.TryLoad("nvEncodeAPI64.dll", out var library))
        {
            error = "nvEncodeAPI64.dll no está instalada (hace falta el driver de NVIDIA)";
            return null;
        }

        if (!NativeLibrary.TryGetExport(library, "NvEncodeAPIGetMaxSupportedVersion", out var getVersion) ||
            !NativeLibrary.TryGetExport(library, "NvEncodeAPICreateInstance", out var create))
        {
            error = "nvEncodeAPI64.dll no exporta la API esperada";
            return null;
        }

        uint maxVersion = 0;
        ((delegate* unmanaged<uint*, int>)getVersion)(&maxVersion);
        // El driver informa (mayor << 4) | menor.
        if (maxVersion < ((11u << 4) | 1u))
        {
            error = $"El driver de NVIDIA es demasiado viejo para NVENC (API {maxVersion >> 4}.{maxVersion & 0xF})";
            return null;
        }

        var list = (byte*)NativeMemory.AllocZeroed(FunctionListSize);
        *(uint*)list = StructVersion(2);
        var status = ((delegate* unmanaged<byte*, int>)create)(list);
        if (status != StatusSuccess)
        {
            NativeMemory.Free(list);
            error = $"NvEncodeAPICreateInstance falló ({status})";
            return null;
        }

        return new NvEncApi(list);
    }

    private void* Function(int index) => ((void**)(_list + 8))[index];

    public void Check(int status, string what, void* encoder = null)
    {
        if (status == StatusSuccess) return;
        var detail = "";
        if (encoder != null)
        {
            var text = ((delegate* unmanaged<void*, byte*>)Function(FnGetLastErrorString))(encoder);
            if (text != null) detail = ": " + Marshal.PtrToStringAnsi((nint)text);
        }

        throw new NvEncException($"NVENC {what} falló (error {status}){detail}", status);
    }

    public int OpenSessionEx(void* openParams, void** encoder) =>
        ((delegate* unmanaged<void*, void**, int>)Function(FnOpenEncodeSessionEx))(openParams, encoder);

    public int DestroyEncoder(void* encoder) =>
        ((delegate* unmanaged<void*, int>)Function(FnDestroyEncoder))(encoder);

    public int GetEncodeCaps(void* encoder, Guid codec, void* capsParam, int* value) =>
        ((delegate* unmanaged<void*, Guid, void*, int*, int>)Function(FnGetEncodeCaps))(encoder, codec, capsParam, value);

    public int GetPresetConfigEx(void* encoder, Guid codec, Guid preset, uint tuning, void* presetConfig) =>
        ((delegate* unmanaged<void*, Guid, Guid, uint, void*, int>)Function(FnGetEncodePresetConfigEx))(
            encoder, codec, preset, tuning, presetConfig);

    public int InitializeEncoder(void* encoder, void* initParams) =>
        ((delegate* unmanaged<void*, void*, int>)Function(FnInitializeEncoder))(encoder, initParams);

    public int ReconfigureEncoder(void* encoder, void* reconfigureParams) =>
        ((delegate* unmanaged<void*, void*, int>)Function(FnReconfigureEncoder))(encoder, reconfigureParams);

    public int CreateBitstreamBuffer(void* encoder, void* createParams) =>
        ((delegate* unmanaged<void*, void*, int>)Function(FnCreateBitstreamBuffer))(encoder, createParams);

    public int DestroyBitstreamBuffer(void* encoder, void* buffer) =>
        ((delegate* unmanaged<void*, void*, int>)Function(FnDestroyBitstreamBuffer))(encoder, buffer);

    public int RegisterResource(void* encoder, void* registerParams) =>
        ((delegate* unmanaged<void*, void*, int>)Function(FnRegisterResource))(encoder, registerParams);

    public int UnregisterResource(void* encoder, void* registered) =>
        ((delegate* unmanaged<void*, void*, int>)Function(FnUnregisterResource))(encoder, registered);

    public int MapInputResource(void* encoder, void* mapParams) =>
        ((delegate* unmanaged<void*, void*, int>)Function(FnMapInputResource))(encoder, mapParams);

    public int UnmapInputResource(void* encoder, void* mapped) =>
        ((delegate* unmanaged<void*, void*, int>)Function(FnUnmapInputResource))(encoder, mapped);

    public int EncodePicture(void* encoder, void* picParams) =>
        ((delegate* unmanaged<void*, void*, int>)Function(FnEncodePicture))(encoder, picParams);

    public int LockBitstream(void* encoder, void* lockParams) =>
        ((delegate* unmanaged<void*, void*, int>)Function(FnLockBitstream))(encoder, lockParams);

    public int UnlockBitstream(void* encoder, void* buffer) =>
        ((delegate* unmanaged<void*, void*, int>)Function(FnUnlockBitstream))(encoder, buffer);

    public int GetSequenceParams(void* encoder, void* payload) =>
        ((delegate* unmanaged<void*, void*, int>)Function(FnGetSequenceParams))(encoder, payload);
}
