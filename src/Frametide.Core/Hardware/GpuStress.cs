using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Frametide.Core.Hardware;

/// <summary>
/// Built-in GPU stress test: a Direct3D 11 compute shader with result verification.
/// 1. At stock settings the shader runs a long chain of FMA math per thread and stores the results as a reference.
/// 2. Every following dispatch recomputes the same math and compares bit for bit. A mismatch is a computation error
///    ("artifact"): the GPU is not stable.
/// 3. A driver reset (TDR) shows up as DXGI_ERROR_DEVICE_REMOVED.
/// The reference is also kept on the CPU, so the test can restart after a driver reset. All D3D calls run on one
/// worker thread (the immediate context is not thread-safe); COM methods are called by their vtable index (d3d11.h order).
/// </summary>
public sealed partial class GpuStress : IDisposable
{
    [LibraryImport("d3d11.dll")]
    private static partial int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
        [In] int[] levels, uint numLevels, uint sdkVersion, out IntPtr device, out int level, out IntPtr context);

    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi)]
    private static extern int D3DCompile(byte[] src, UIntPtr size, string name, IntPtr defines, IntPtr include,
        string entry, string target, uint flags1, uint flags2, out IntPtr code, out IntPtr errors);

    [LibraryImport("dxgi.dll")]
    private static partial int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    [StructLayout(LayoutKind.Sequential)]
    private struct BufferDesc { public uint ByteWidth; public int Usage; public uint BindFlags; public uint CpuAccess; public uint Misc; public uint Stride; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Mapped { public IntPtr Data; public uint RowPitch; public uint DepthPitch; }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint FnRelease(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr FnBlobPtr(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate UIntPtr FnBlobSize(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int FnEnumAdapters1(IntPtr self, uint index, out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int FnGetDesc1(IntPtr self, IntPtr desc);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int FnCreateBuffer(IntPtr self, ref BufferDesc desc, IntPtr init, out IntPtr buffer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int FnCreateUav(IntPtr self, IntPtr resource, IntPtr desc, out IntPtr uav);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int FnCreateCs(IntPtr self, IntPtr code, UIntPtr length, IntPtr linkage, out IntPtr shader);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int FnRemovedReason(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void FnSetUavs(IntPtr self, uint start, uint count, IntPtr[] uavs, IntPtr counts);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void FnSetCs(IntPtr self, IntPtr shader, IntPtr instances, uint count);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void FnDispatch(IntPtr self, uint x, uint y, uint z);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void FnCopy(IntPtr self, IntPtr dst, IntPtr src);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void FnUpdate(IntPtr self, IntPtr dst, uint sub, IntPtr box, IntPtr src, uint rowPitch, uint depthPitch);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int FnMap(IntPtr self, IntPtr res, uint sub, int type, uint flags, out Mapped mapped);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void FnUnmap(IntPtr self, IntPtr res, uint sub);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void FnVoid(IntPtr self);

    private static T Fn<T>(IntPtr obj, int index) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), index * IntPtr.Size));

    private static void Release(ref IntPtr p)
    {
        if (p == IntPtr.Zero) return;
        Fn<FnRelease>(p, 2)(p);
        p = IntPtr.Zero;
    }

    // d3d11.h vtable indices
    private const int DevCreateBuffer = 3, DevCreateUav = 8, DevCreateCs = 18, DevRemovedReason = 39;
    private const int CtxMap = 14, CtxUnmap = 15, CtxDispatch = 41, CtxCopy = 47, CtxUpdate = 48,
                      CtxCsSetUavs = 68, CtxCsSetShader = 69, CtxClearState = 110, CtxFlush = 111;

    private const int Dim = 1024;          // 1024 x 1024 threads per dispatch
    private const int Count = Dim * Dim;
    private const string ThreadName = "Frametide GPU stress";

    private const string Hlsl = """
        RWStructuredBuffer<float> Ref : register(u0);
        RWStructuredBuffer<uint> Err : register(u1);
        RWStructuredBuffer<uint> Params : register(u2);

        [numthreads(16, 16, 1)]
        void CSMain(uint3 id : SV_DispatchThreadID)
        {
            uint index = id.y * 1024 + id.x;
            uint iters = Params[1];
            float4 x = float4(index & 1023, index >> 10, 3, 5) * 0.0009765625 + float4(0.13, 0.29, 0.31, 0.47);
            float4 y = x.wzyx + float4(0.59, 0.61, 0.73, 0.83);
            [loop]
            for (uint k = 0; k < iters; k++)
            {
                x = mad(x, float4(0.99913, 0.99931, 0.99947, 0.99971), y * 0.000131 + 0.000173);
                y = mad(y, float4(0.99971, 0.99947, 0.99931, 0.99913), x.wzyx * 0.000173 + 0.000131);
                x = mad(x, float4(0.99967, 0.99919, 0.99937, 0.99953), y.yzwx * 0.000191 + 0.000113);
                y = mad(y, float4(0.99937, 0.99967, 0.99913, 0.99959), x.zwxy * 0.000113 + 0.000191);
            }
            float sum = dot(x, float4(1.0, 0.5, 0.25, 0.125)) + dot(y, float4(0.125, 0.25, 0.5, 1.0));
            if (Params[0] == 0)
                Ref[index] = sum;
            else if (!isfinite(sum) || asuint(sum) != asuint(Ref[index]))
                InterlockedAdd(Err[0], 1);
        }
        """;

    private volatile bool _running;
    private volatile bool _deviceLost;
    private volatile string _status = "idle";
    private long _errors;
    private long _dispatches;
    private float[]? _reference;           // CPU copy of the stock reference
    private int _refIterations;
    private volatile bool _stopRequested;
    private volatile bool _resetRequested;
    private Thread? _worker;
    private readonly ManualResetEventSlim _ready = new();
    private string? _startError;
    private IntPtr _device, _context, _shader, _refBuf, _errBuf, _parBuf, _refUav, _errUav, _parUav, _errStage, _refStage;

    public bool Running => _running;
    public bool DeviceLost => _deviceLost;
    public string Status => _status;

    /// <summary>Mismatching results since the last <see cref="ResetErrors"/>.</summary>
    public long Errors => Interlocked.Read(ref _errors);

    public long Dispatches => Interlocked.Read(ref _dispatches);

    public bool HasReference => _reference is not null;

    /// <summary>Starts the load; the first start creates the reference (call it at stock settings). Returns an error or null.</summary>
    public string? Start(TimeSpan timeout)
    {
        if (_running) return null;
        _stopRequested = false;
        _deviceLost = false;
        _startError = null;
        _ready.Reset();
        _worker = new Thread(Worker) { IsBackground = true, Name = ThreadName };
        _worker.Start();
        return _ready.Wait(timeout) ? _startError : "The GPU stress test did not start in time.";
    }

    public void Stop()
    {
        _stopRequested = true;
        if (_worker is { IsAlive: true }) _worker.Join(5000);
        _worker = null;
    }

    public void ResetErrors()
    {
        _resetRequested = true;
        Interlocked.Exchange(ref _errors, 0);
    }

    private void Worker()
    {
        try
        {
            Init();
            _running = true;
            _status = "running";
            _ready.Set();
            Loop();
        }
        catch (Exception e)   // anything escaping a worker thread would end the whole app
        {
            if (!_ready.IsSet) { _startError = e.Message; _ready.Set(); }
            _status = "error: " + e.Message;
        }
        finally
        {
            _running = false;
            Cleanup();
            if (_status == "running") _status = "stopped";
        }
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new InvalidOperationException($"{what} failed (0x{hr:X8})");
    }

    private static IntPtr FindNvidiaAdapter()
    {
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");   // IDXGIFactory1
        if (CreateDXGIFactory1(ref iid, out var factory) < 0) return IntPtr.Zero;
        var found = IntPtr.Zero;
        var desc = Marshal.AllocHGlobal(512);
        try
        {
            for (uint i = 0; i < 16; i++)
            {
                if (Fn<FnEnumAdapters1>(factory, 12)(factory, i, out var adapter) < 0) break;
                Fn<FnGetDesc1>(adapter, 10)(adapter, desc);
                var vendor = (uint)Marshal.ReadInt32(desc, 256);
                if (vendor == 0x10DE && found == IntPtr.Zero) found = adapter;
                else Release(ref adapter);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(desc);
            Release(ref factory);
        }
        return found;
    }

    private IntPtr MakeBuffer(uint bytes, int usage, uint bind, uint cpu, uint misc, uint stride)
    {
        var d = new BufferDesc { ByteWidth = bytes, Usage = usage, BindFlags = bind, CpuAccess = cpu, Misc = misc, Stride = stride };
        Check(Fn<FnCreateBuffer>(_device, DevCreateBuffer)(_device, ref d, IntPtr.Zero, out var b), "CreateBuffer");
        return b;
    }

    private IntPtr MakeUav(IntPtr resource)
    {
        Check(Fn<FnCreateUav>(_device, DevCreateUav)(_device, resource, IntPtr.Zero, out var u), "CreateUnorderedAccessView");
        return u;
    }

    private void Init()
    {
        var adapter = FindNvidiaAdapter();
        int[] levels = [0xb000];                      // D3D_FEATURE_LEVEL_11_0
        var hr = D3D11CreateDevice(adapter, adapter == IntPtr.Zero ? 1 : 0, IntPtr.Zero, 0, levels, 1, 7, out _device, out _, out _context);
        Release(ref adapter);
        Check(hr, "D3D11CreateDevice");

        var src = Encoding.ASCII.GetBytes(Hlsl);
        hr = D3DCompile(src, (UIntPtr)src.Length, "frametide_stress", IntPtr.Zero, IntPtr.Zero, "CSMain", "cs_5_0", 1 << 15, 0, out var code, out var errors);
        if (hr < 0)
        {
            var msg = errors != IntPtr.Zero ? Marshal.PtrToStringAnsi(Fn<FnBlobPtr>(errors, 3)(errors)) : "";
            Release(ref errors);
            throw new InvalidOperationException("Shader compile failed: " + msg);
        }
        Release(ref errors);
        Check(Fn<FnCreateCs>(_device, DevCreateCs)(_device, Fn<FnBlobPtr>(code, 3)(code), Fn<FnBlobSize>(code, 4)(code), IntPtr.Zero, out _shader), "CreateComputeShader");
        Release(ref code);

        const uint uav = 0x80, structured = 0x40, read = 0x20000;
        _refBuf = MakeBuffer(Count * 4, 0, uav, 0, structured, 4);
        _errBuf = MakeBuffer(16, 0, uav, 0, structured, 4);
        _parBuf = MakeBuffer(16, 0, uav, 0, structured, 4);
        _errStage = MakeBuffer(16, 3, 0, read, 0, 0);
        _refStage = MakeBuffer(Count * 4, 3, 0, read, 0, 0);
        _refUav = MakeUav(_refBuf);
        _errUav = MakeUav(_errBuf);
        _parUav = MakeUav(_parBuf);

        Fn<FnSetCs>(_context, CtxCsSetShader)(_context, _shader, IntPtr.Zero, 0);
        Fn<FnSetUavs>(_context, CtxCsSetUavs)(_context, 0, 3, [_refUav, _errUav, _parUav], IntPtr.Zero);
        WriteUints(_errBuf, 0, 0, 0, 0);

        if (_reference is null)
        {
            // Calibrate to about 40 ms per dispatch so the desktop stays responsive.
            var iters = 64;
            for (var attempt = 0; attempt < 8; attempt++)
            {
                WriteUints(_parBuf, 0, (uint)iters, 0, 0);
                var sw = Stopwatch.StartNew();
                Dispatch(1);
                Sync();
                var ms = sw.Elapsed.TotalMilliseconds;
                if (ms >= 25 || iters >= 65536) break;
                iters = (int)Math.Min(65536, iters * Math.Max(2.0, 40.0 / Math.Max(ms, 0.5)));
            }
            _refIterations = iters;
            WriteUints(_parBuf, 0, (uint)iters, 0, 0);    // mode 0 = write the reference
            Dispatch(1);
            Sync();
            Fn<FnCopy>(_context, CtxCopy)(_context, _refStage, _refBuf);
            Check(Fn<FnMap>(_context, CtxMap)(_context, _refStage, 0, 1, 0, out var m), "Map reference");
            var data = new float[Count];
            Marshal.Copy(m.Data, data, 0, Count);
            Fn<FnUnmap>(_context, CtxUnmap)(_context, _refStage, 0);
            _reference = data;
        }
        else
        {
            // Restart after a driver reset: upload the stored stock reference.
            var h = GCHandle.Alloc(_reference, GCHandleType.Pinned);
            try { Fn<FnUpdate>(_context, CtxUpdate)(_context, _refBuf, 0, IntPtr.Zero, h.AddrOfPinnedObject(), 0, 0); }
            finally { h.Free(); }
        }
        WriteUints(_parBuf, 1, (uint)_refIterations, 0, 0);  // mode 1 = verify against the reference
    }

    private void WriteUints(IntPtr buffer, uint a, uint b, uint c, uint d)
    {
        uint[] v = [a, b, c, d];
        var h = GCHandle.Alloc(v, GCHandleType.Pinned);
        try { Fn<FnUpdate>(_context, CtxUpdate)(_context, buffer, 0, IntPtr.Zero, h.AddrOfPinnedObject(), 0, 0); }
        finally { h.Free(); }
    }

    private void Dispatch(int n)
    {
        var d = Fn<FnDispatch>(_context, CtxDispatch);
        for (var i = 0; i < n; i++) d(_context, Dim / 16, Dim / 16, 1);
    }

    /// <summary>Copies the error counter to the CPU (this also waits for the GPU to finish).</summary>
    private uint Sync()
    {
        Fn<FnCopy>(_context, CtxCopy)(_context, _errStage, _errBuf);
        var hr = Fn<FnMap>(_context, CtxMap)(_context, _errStage, 0, 1, 0, out var m);
        if (hr < 0) { _deviceLost = true; throw new InvalidOperationException($"GPU device lost (0x{hr:X8})"); }
        var e = (uint)Marshal.ReadInt32(m.Data);
        Fn<FnUnmap>(_context, CtxUnmap)(_context, _errStage, 0);
        return e;
    }

    private void Loop()
    {
        var batch = 4;
        while (!_stopRequested)
        {
            if (_resetRequested) { WriteUints(_errBuf, 0, 0, 0, 0); _resetRequested = false; }
            var sw = Stopwatch.StartNew();
            Dispatch(batch);
            Fn<FnVoid>(_context, CtxFlush)(_context);
            var e = Sync();
            var ms = sw.Elapsed.TotalMilliseconds;
            Interlocked.Add(ref _dispatches, batch);
            if (!_resetRequested) Interlocked.Exchange(ref _errors, e);
            var removed = Fn<FnRemovedReason>(_device, DevRemovedReason)(_device);
            if (removed < 0) { _deviceLost = true; throw new InvalidOperationException($"GPU device removed (0x{removed:X8})"); }
            // Keep one batch around 250 ms.
            if (ms < 150 && batch < 64) batch++;
            else if (ms > 400 && batch > 1) batch--;
        }
    }

    private void Cleanup()
    {
        try { if (_context != IntPtr.Zero) Fn<FnVoid>(_context, CtxClearState)(_context); }
        catch (SEHException) { }
        Release(ref _refUav); Release(ref _errUav); Release(ref _parUav);
        Release(ref _refBuf); Release(ref _errBuf); Release(ref _parBuf); Release(ref _errStage); Release(ref _refStage);
        Release(ref _shader); Release(ref _context); Release(ref _device);
    }

    public void Dispose()
    {
        Stop();
        _ready.Dispose();
    }
}
