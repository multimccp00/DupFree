using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;

namespace DupFree.Services
{
    /// <summary>
    /// GPU-accelerated SSIM computation using Direct3D 11 compute shaders.
    /// Thread-safe: guards D3D11 immediate-context calls with a lock.
    /// </summary>
    public class GpuSsim : IDisposable
    {
        public const int MaxBatchPairs = 1024;

        private ID3D11Device? _device;
        private ID3D11DeviceContext? _ctx;
        private ID3D11ComputeShader? _cs;
        private ID3D11ComputeShader? _batchedCs;
        private bool _initialized;
        private readonly object _gpuLock = new();

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

        private const string HlslBatchedSource = @"
cbuffer Params : register(b0)
{
    uint PixelCount;
    uint PairCount;
};

StructuredBuffer<float>   bufA   : register(t0);
StructuredBuffer<float>   bufB   : register(t1);
RWStructuredBuffer<float> outBuf : register(u0);

[numthreads(64, 1, 1)]
void CSMain(uint3 DTid : SV_DispatchThreadID)
{
    uint tid = DTid.x;
    uint pair = DTid.y;
    if (pair >= PairCount || tid >= 64) return;

    float sA  = 0.0f;
    float sB  = 0.0f;
    float sA2 = 0.0f;
    float sB2 = 0.0f;
    float sAB = 0.0f;
    uint baseOffset = pair * PixelCount;

    for (uint i = tid; i < PixelCount; i += 64)
    {
        float a = bufA[baseOffset + i];
        float b = bufB[baseOffset + i];
        sA  += a;
        sB  += b;
        sA2 += a * a;
        sB2 += b * b;
        sAB += a * b;
    }

    uint o = (pair * 64u + tid) * 5u;
    outBuf[o     ] = sA;
    outBuf[o + 1u] = sB;
    outBuf[o + 2u] = sA2;
    outBuf[o + 3u] = sB2;
    outBuf[o + 4u] = sAB;
}
";

        public bool Init()
        {
            try
            {
                var hr = D3D11CreateDevice(
                    null!,
                    DriverType.Hardware,
                    DeviceCreationFlags.None,
                    null!,
                    out _device,
                    out _ctx);
                if (hr.Failure)
                {
                    Log.Error("GpuSsim: D3D11CreateDevice failed");
                    return false;
                }

                ReadOnlyMemory<byte> compiled = Compiler.Compile(HlslSource, "CSMain", "ssim.hlsl", "cs_5_0");
                _cs = _device.CreateComputeShader(compiled.ToArray());

                try
                {
                    ReadOnlyMemory<byte> compiledBatched = Compiler.Compile(HlslBatchedSource, "CSMain", "ssim_batched.hlsl", "cs_5_0");
                    _batchedCs = _device.CreateComputeShader(compiledBatched.ToArray());
                }
                catch (Exception ex)
                {
                    _batchedCs = null;
                    Log.Error($"GpuSsim: batched shader compile failed – {ex.Message}");
                }

                _initialized = true;
                Log.Info("GpuSsim: ready");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"GpuSsim: Init – {ex.Message}");
                return false;
            }
        }

        public double ComputeSsimGpu(float[] a, float[] b, int w, int h)
        {
            if (!_initialized) return ComputeSsimCpuFallback(a, b, w, h);
            if (a == null || b == null) return 0.0;
            int n = w * h;
            if (n == 0 || a.Length < n || b.Length < n) return 0.0;

            const int kGroup = 64;
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
                    using var gpuA = CreateStructuredInputBuffer(device, sizeof(float) * n, pinA.AddrOfPinnedObject());
                    using var gpuB = CreateStructuredInputBuffer(device, sizeof(float) * n, pinB.AddrOfPinnedObject());
                    using var srvA = device.CreateShaderResourceView(gpuA);
                    using var srvB = device.CreateShaderResourceView(gpuB);
                    using var outBuf = CreateStructuredOutputBuffer(device, outCount);
                    using var uav = device.CreateUnorderedAccessView(outBuf);
                    using var cb = CreateParamsBuffer(device, (uint)n, (uint)numThreads);
                    using var staging = CreateReadbackBuffer(device, outCount);

                    ctx.CSSetShader(cs);
                    ctx.CSSetShaderResources(0, new ID3D11ShaderResourceView[] { srvA!, srvB! });
                    ctx.CSSetUnorderedAccessViews(0, new ID3D11UnorderedAccessView[] { uav! });
                    ctx.CSSetConstantBuffer(0, cb);
                    ctx.Dispatch(numGroups, 1, 1);

                    ctx.CopyResource(staging, outBuf);
                    float[] vals = ReadBackFloatBuffer(ctx, staging, outCount);
                    ClearBindings(ctx);
                    return ReduceSsim(vals, numThreads, n);
                }
                finally
                {
                    if (pinA.IsAllocated) pinA.Free();
                    if (pinB.IsAllocated) pinB.Free();
                }
            }
        }

        public double[] ComputeSsimGpuBatched(List<(float[] a, float[] b)> pairs, int w, int h)
        {
            if (pairs.Count == 0)
                return Array.Empty<double>();

            if (!_initialized || _batchedCs == null || pairs.Count > MaxBatchPairs)
            {
                double[] fallback = new double[pairs.Count];
                for (int index = 0; index < pairs.Count; index++)
                {
                    fallback[index] = ComputeSsimGpu(pairs[index].a, pairs[index].b, w, h);
                }
                return fallback;
            }

            int pixelCount = w * h;
            for (int index = 0; index < pairs.Count; index++)
            {
                if (pairs[index].a.Length < pixelCount || pairs[index].b.Length < pixelCount)
                    return BuildFallbackBatch(pairs, w, h);
            }

            int pairCount = pairs.Count;
            int totalPixelCount = pixelCount * pairCount;
            float[] mergedA = new float[totalPixelCount];
            float[] mergedB = new float[totalPixelCount];
            for (int index = 0; index < pairCount; index++)
            {
                Array.Copy(pairs[index].a, 0, mergedA, index * pixelCount, pixelCount);
                Array.Copy(pairs[index].b, 0, mergedB, index * pixelCount, pixelCount);
            }

            lock (_gpuLock)
            {
                var device = _device!;
                var ctx = _ctx!;
                var pinA = GCHandle.Alloc(mergedA, GCHandleType.Pinned);
                var pinB = GCHandle.Alloc(mergedB, GCHandleType.Pinned);
                try
                {
                    int threadsPerPair = 64;
                    int outCount = pairCount * threadsPerPair * 5;
                    using var gpuA = CreateStructuredInputBuffer(device, sizeof(float) * totalPixelCount, pinA.AddrOfPinnedObject());
                    using var gpuB = CreateStructuredInputBuffer(device, sizeof(float) * totalPixelCount, pinB.AddrOfPinnedObject());
                    using var srvA = device.CreateShaderResourceView(gpuA);
                    using var srvB = device.CreateShaderResourceView(gpuB);
                    using var outBuf = CreateStructuredOutputBuffer(device, outCount);
                    using var uav = device.CreateUnorderedAccessView(outBuf);
                    using var cb = CreateParamsBuffer(device, (uint)pixelCount, (uint)pairCount);
                    using var staging = CreateReadbackBuffer(device, outCount);

                    ctx.CSSetShader(_batchedCs);
                    ctx.CSSetShaderResources(0, new ID3D11ShaderResourceView[] { srvA!, srvB! });
                    ctx.CSSetUnorderedAccessViews(0, new ID3D11UnorderedAccessView[] { uav! });
                    ctx.CSSetConstantBuffer(0, cb);
                    ctx.Dispatch(1, pairCount, 1);

                    ctx.CopyResource(staging, outBuf);
                    float[] vals = ReadBackFloatBuffer(ctx, staging, outCount);
                    ClearBindings(ctx);

                    double[] results = new double[pairCount];
                    for (int index = 0; index < pairCount; index++)
                    {
                        results[index] = ReduceSsim(vals, threadsPerPair, pixelCount, index * threadsPerPair * 5);
                    }
                    return results;
                }
                finally
                {
                    if (pinA.IsAllocated) pinA.Free();
                    if (pinB.IsAllocated) pinB.Free();
                }
            }
        }

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

        private static ID3D11Buffer CreateStructuredInputBuffer(ID3D11Device device, int sizeInBytes, IntPtr dataPointer)
        {
            var desc = new BufferDescription(
                sizeInBytes,
                BindFlags.ShaderResource,
                ResourceUsage.Default,
                CpuAccessFlags.None,
                ResourceOptionFlags.BufferStructured,
                sizeof(float));
            return device.CreateBuffer(desc, new SubresourceData(dataPointer));
        }

        private static ID3D11Buffer CreateStructuredOutputBuffer(ID3D11Device device, int floatCount)
        {
            var desc = new BufferDescription(
                sizeof(float) * floatCount,
                BindFlags.UnorderedAccess,
                ResourceUsage.Default,
                CpuAccessFlags.None,
                ResourceOptionFlags.BufferStructured,
                sizeof(float));
            return device.CreateBuffer(desc);
        }

        private static ID3D11Buffer CreateReadbackBuffer(ID3D11Device device, int floatCount)
        {
            return device.CreateBuffer(new BufferDescription(
                sizeof(float) * floatCount,
                BindFlags.None,
                ResourceUsage.Staging,
                CpuAccessFlags.Read,
                ResourceOptionFlags.None,
                0));
        }

        private static ID3D11Buffer CreateParamsBuffer(ID3D11Device device, uint firstValue, uint secondValue)
        {
            byte[] cbBytes = new byte[16];
            BitConverter.TryWriteBytes(cbBytes.AsSpan(0, 4), firstValue);
            BitConverter.TryWriteBytes(cbBytes.AsSpan(4, 4), secondValue);
            var pin = GCHandle.Alloc(cbBytes, GCHandleType.Pinned);
            try
            {
                return device.CreateBuffer(
                    new BufferDescription(16, BindFlags.ConstantBuffer, ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.None, 0),
                    new SubresourceData(pin.AddrOfPinnedObject()));
            }
            finally
            {
                pin.Free();
            }
        }

        private static float[] ReadBackFloatBuffer(ID3D11DeviceContext ctx, ID3D11Buffer staging, int floatCount)
        {
            var mapped = ctx.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                float[] vals = new float[floatCount];
                Marshal.Copy(mapped.DataPointer, vals, 0, floatCount);
                return vals;
            }
            finally
            {
                ctx.Unmap(staging, 0);
            }
        }

        private static void ClearBindings(ID3D11DeviceContext ctx)
        {
            ctx.CSSetShader((ID3D11ComputeShader?)null);
            ctx.CSSetShaderResources(0, new ID3D11ShaderResourceView[] { null!, null! });
            ctx.CSSetUnorderedAccessViews(0, new ID3D11UnorderedAccessView[] { null! });
        }

        private static double ReduceSsim(float[] vals, int numThreads, int pixelCount, int startOffset = 0)
        {
            double sA = 0, sB = 0, sA2 = 0, sB2 = 0, sAB = 0;
            for (int thread = 0; thread < numThreads; thread++)
            {
                int offset = startOffset + (thread * 5);
                sA += vals[offset];
                sB += vals[offset + 1];
                sA2 += vals[offset + 2];
                sB2 += vals[offset + 3];
                sAB += vals[offset + 4];
            }

            double muA = sA / pixelCount;
            double muB = sB / pixelCount;
            double varA = sA2 / pixelCount - muA * muA;
            double varB = sB2 / pixelCount - muB * muB;
            double cov = sAB / pixelCount - muA * muB;

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

        private double[] BuildFallbackBatch(List<(float[] a, float[] b)> pairs, int w, int h)
        {
            double[] fallback = new double[pairs.Count];
            for (int index = 0; index < pairs.Count; index++)
            {
                fallback[index] = ComputeSsimGpu(pairs[index].a, pairs[index].b, w, h);
            }
            return fallback;
        }

        public void Dispose()
        {
            try { _cs?.Dispose(); } catch { }
            try { _batchedCs?.Dispose(); } catch { }
            try { _ctx?.Dispose(); } catch { }
            try { _device?.Dispose(); } catch { }
        }
    }
}
