using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Direct3D;
using Vortice.D3DCompiler;
using static Vortice.Direct3D11.D3D11;

namespace DupFree.Services
{
    /// <summary>
    /// GPU-accelerated SSIM computation using Direct3D 11 compute shaders.
    /// Thread-safe: guards D3D11 immediate-context calls with a lock.
    /// </summary>
    public class GpuSsim : IDisposable
    {
        private ID3D11Device? _device;
        private ID3D11DeviceContext? _ctx;
        private ID3D11ComputeShader? _cs;
        private bool _initialized;
        private readonly object _gpuLock = new();

        // ── HLSL compute shader ──────────────────────────────────────────
        // Each thread accumulates partial sums for a strided chunk of
        // pixels.  Output is 5 floats per thread: sumA, sumB, sumA²,
        // sumB², sumAB.  The host reduces them and computes global SSIM.
        private const string HlslSource = @"
cbuffer Params : register(b0)
{
    uint PixelCount;
    uint ThreadCount;
};

StructuredBuffer<float>   bufA   : register(t0);
StructuredBuffer<float>   bufB   : register(t1);
RWStructuredBuffer<float> outBuf : register(u0);

[numthreads(64, 1, 1)]
void CSMain(uint3 DTid : SV_DispatchThreadID)
{
    uint tid = DTid.x;
    if (tid >= ThreadCount) return;

    float sA  = 0.0f;
    float sB  = 0.0f;
    float sA2 = 0.0f;
    float sB2 = 0.0f;
    float sAB = 0.0f;

    for (uint i = tid; i < PixelCount; i += ThreadCount)
    {
        float a = bufA[i];
        float b = bufB[i];
        sA  += a;
        sB  += b;
        sA2 += a * a;
        sB2 += b * b;
        sAB += a * b;
    }

    uint o = tid * 5u;
    outBuf[o     ] = sA;
    outBuf[o + 1u] = sB;
    outBuf[o + 2u] = sA2;
    outBuf[o + 3u] = sB2;
    outBuf[o + 4u] = sAB;
}
";

        /// <summary>Create D3D11 device and compile compute shader.</summary>
        public bool Init()
        {
            try
            {
                // 1. Create D3D11 hardware device
                var hr = D3D11CreateDevice(
                    null!, DriverType.Hardware,
                    DeviceCreationFlags.None, null!,
                    out _device, out _ctx);
                if (hr.Failure)
                {
                    Log.Error("GpuSsim: D3D11CreateDevice failed");
                    return false;
                }

                // 2. Compile HLSL → bytecode via Vortice.D3DCompiler
                byte[] bytecode;
                try
                {
                    ReadOnlyMemory<byte> compiled = Compiler.Compile(
                        HlslSource, "CSMain", "ssim.hlsl", "cs_5_0");
                    bytecode = compiled.ToArray();
                }
                catch (Exception ex)
                {
                    Log.Error($"GpuSsim: shader compile failed – {ex.Message}");
                    return false;
                }

                // 3. Create compute shader
                _cs = _device.CreateComputeShader(bytecode);
                _initialized = true;
                Log.Info($"GpuSsim: ready ({bytecode.Length} B shader)");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"GpuSsim: Init – {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Compute SSIM between two grayscale float[] images on the GPU.
        /// Falls back to CPU SIMD when the GPU path is unavailable.
        /// Thread-safe.
        /// </summary>
        public double ComputeSsimGpu(float[] a, float[] b, int w, int h)
        {
            if (!_initialized) return ComputeSsimCpuFallback(a, b, w, h);
            if (a == null || b == null) return 0.0;
            int n = w * h;
            if (n == 0 || a.Length < n || b.Length < n) return 0.0;

            const int kGroup = 64;
            // Use enough groups so each thread handles ~64 pixels
            int numGroups = Math.Clamp((n + kGroup * 64 - 1) / (kGroup * 64), 1, 256);
            int numThreads = numGroups * kGroup;
            int outCount = numThreads * 5;

            lock (_gpuLock)
            {
                var device = _device!;
                var ctx = _ctx!;
                var cs = _cs!;

                var pinA = GCHandle.Alloc(a, GCHandleType.Pinned);
                var pinB = GCHandle.Alloc(b, GCHandleType.Pinned);
                try
                {
                    // ── Input structured buffers ─────────────────────────
                    var inDesc = new BufferDescription(
                        sizeof(float) * n,
                        BindFlags.ShaderResource,
                        ResourceUsage.Default,
                        CpuAccessFlags.None,
                        ResourceOptionFlags.BufferStructured,
                        sizeof(float));

                    using var gpuA = device.CreateBuffer(inDesc,
                                        new SubresourceData(pinA.AddrOfPinnedObject()));
                    using var gpuB = device.CreateBuffer(inDesc,
                                        new SubresourceData(pinB.AddrOfPinnedObject()));

                    using var srvA = device.CreateShaderResourceView(gpuA);
                    using var srvB = device.CreateShaderResourceView(gpuB);

                    // ── Output buffer (5 floats per thread) ──────────────
                    var outDesc = new BufferDescription(
                        sizeof(float) * outCount,
                        BindFlags.UnorderedAccess,
                        ResourceUsage.Default,
                        CpuAccessFlags.None,
                        ResourceOptionFlags.BufferStructured,
                        sizeof(float));
                    using var outBuf = device.CreateBuffer(outDesc);
                    using var uav = device.CreateUnorderedAccessView(outBuf);

                    // ── Constant buffer (16-byte aligned) ────────────────
                    var cbBytes = new byte[16];
                    BitConverter.TryWriteBytes(cbBytes.AsSpan(0, 4), (uint)n);
                    BitConverter.TryWriteBytes(cbBytes.AsSpan(4, 4), (uint)numThreads);
                    var cbPin = GCHandle.Alloc(cbBytes, GCHandleType.Pinned);
                    using var cb = device.CreateBuffer(
                        new BufferDescription(16, BindFlags.ConstantBuffer,
                            ResourceUsage.Default, CpuAccessFlags.None,
                            ResourceOptionFlags.None, 0),
                        new SubresourceData(cbPin.AddrOfPinnedObject()));
                    cbPin.Free();

                    // ── Staging buffer for readback ──────────────────────
                    using var staging = device.CreateBuffer(
                        new BufferDescription(
                            sizeof(float) * outCount,
                            BindFlags.None,
                            ResourceUsage.Staging,
                            CpuAccessFlags.Read,
                            ResourceOptionFlags.None, 0));

                    // ── Dispatch ─────────────────────────────────────────
                    ctx.CSSetShader(cs);
                    ctx.CSSetShaderResources(0, new ID3D11ShaderResourceView[] { srvA!, srvB! });
                    ctx.CSSetUnorderedAccessViews(0, new ID3D11UnorderedAccessView[] { uav! });
                    ctx.CSSetConstantBuffer(0, cb);
                    ctx.Dispatch(numGroups, 1, 1);

                    // ── Readback ─────────────────────────────────────────
                    ctx.CopyResource(staging, outBuf);
                    var mapped = ctx.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                    var vals = new float[outCount];
                    Marshal.Copy(mapped.DataPointer, vals, 0, outCount);
                    ctx.Unmap(staging, 0);

                    // ── Unbind ───────────────────────────────────────────
                    ctx.CSSetShader((ID3D11ComputeShader?)null);
                    ctx.CSSetShaderResources(0, new ID3D11ShaderResourceView[] { (ID3D11ShaderResourceView)null!, (ID3D11ShaderResourceView)null! });
                    ctx.CSSetUnorderedAccessViews(0, new ID3D11UnorderedAccessView[] { (ID3D11UnorderedAccessView)null! });

                    // ── CPU reduce + SSIM ────────────────────────────────
                    double sA = 0, sB = 0, sA2 = 0, sB2 = 0, sAB = 0;
                    for (int t = 0; t < numThreads; t++)
                    {
                        int o = t * 5;
                        sA += vals[o];
                        sB += vals[o + 1];
                        sA2 += vals[o + 2];
                        sB2 += vals[o + 3];
                        sAB += vals[o + 4];
                    }

                    double muA = sA / n, muB = sB / n;
                    double varA = sA2 / n - muA * muA;
                    double varB = sB2 / n - muB * muB;
                    double cov = sAB / n - muA * muB;

                    const double K1 = 0.01, K2 = 0.03, L = 255.0;
                    double C1 = (K1 * L) * (K1 * L);
                    double C2 = (K2 * L) * (K2 * L);
                    double num = (2.0 * muA * muB + C1) * (2.0 * cov + C2);
                    double den = (muA * muA + muB * muB + C1) * (varA + varB + C2);
                    if (den <= 0) return 0.0;
                    double ssim = num / den;
                    if (double.IsNaN(ssim) || double.IsInfinity(ssim)) return 0.0;
                    return Math.Clamp(ssim, 0.0, 1.0);
                }
                finally
                {
                    if (pinA.IsAllocated) pinA.Free();
                    if (pinB.IsAllocated) pinB.Free();
                }
            }
        }

        /// <summary>CPU SIMD fallback for SSIM.</summary>
        public double ComputeSsimCpuFallback(float[] a, float[] b, int w, int h)
        {
            if (a == null || b == null) return 0.0;
            int n = w * h;
            if (a.Length != n || b.Length != n) return 0.0;

            int vecSz = System.Numerics.Vector<float>.Count;
            var vSumA = System.Numerics.Vector<float>.Zero;
            var vSumB = System.Numerics.Vector<float>.Zero;
            int i = 0;
            for (; i + vecSz <= n; i += vecSz)
            {
                vSumA += new System.Numerics.Vector<float>(a, i);
                vSumB += new System.Numerics.Vector<float>(b, i);
            }
            double sumA = 0, sumB = 0;
            for (int k = 0; k < vecSz; k++) { sumA += vSumA[k]; sumB += vSumB[k]; }
            for (; i < n; i++) { sumA += a[i]; sumB += b[i]; }
            double muA = sumA / n, muB = sumB / n;

            var vMuA = new System.Numerics.Vector<float>((float)muA);
            var vMuB = new System.Numerics.Vector<float>((float)muB);
            var vVarA = System.Numerics.Vector<float>.Zero;
            var vVarB = System.Numerics.Vector<float>.Zero;
            var vCov = System.Numerics.Vector<float>.Zero;
            i = 0;
            for (; i + vecSz <= n; i += vecSz)
            {
                var da = new System.Numerics.Vector<float>(a, i) - vMuA;
                var db = new System.Numerics.Vector<float>(b, i) - vMuB;
                vVarA += da * da;
                vVarB += db * db;
                vCov += da * db;
            }
            double varA = 0, varB = 0, cov = 0;
            for (int k = 0; k < vecSz; k++) { varA += vVarA[k]; varB += vVarB[k]; cov += vCov[k]; }
            for (; i < n; i++)
            {
                double da = a[i] - muA, db = b[i] - muB;
                varA += da * da; varB += db * db; cov += da * db;
            }
            varA /= n; varB /= n; cov /= n;

            const double K1 = 0.01, K2 = 0.03, L = 255.0;
            double C1 = (K1 * L) * (K1 * L);
            double C2 = (K2 * L) * (K2 * L);
            double num = (2.0 * muA * muB + C1) * (2.0 * cov + C2);
            double den = (muA * muA + muB * muB + C1) * (varA + varB + C2);
            if (den <= 0) return 0.0;
            double ssim = num / den;
            if (double.IsNaN(ssim) || double.IsInfinity(ssim)) return 0.0;
            return Math.Clamp(ssim, 0.0, 1.0);
        }

        public void Dispose()
        {
            try { _cs?.Dispose(); } catch { }
            try { _ctx?.Dispose(); } catch { }
            try { _device?.Dispose(); } catch { }
        }
    }
}
