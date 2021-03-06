﻿////////////////////////////////////////////////////////////////////////
//
// This file is part of pdn-ddsfiletype-plus, a DDS FileType plugin
// for Paint.NET that adds support for the DX10 and later formats.
//
// Copyright (c) 2017, 2018 Nicholas Hayes
//
// This file is licensed under the MIT License.
// See LICENSE.txt for complete licensing and attribution information.
//
////////////////////////////////////////////////////////////////////////

using PaintDotNet;
using PaintDotNet.IO;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace DdsFileTypePlus
{
    static class DdsFile
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct DDSLoadInfo
        {
            public int width;
            public int height;
            public int stride;
            public IntPtr scan0;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DDSSaveInfo
        {
            public int width;
            public int height;
            public int stride;
            public DdsFileFormat format;
            public DdsErrorMetric errorMetric;
            public BC7CompressionMode compressionMode;
            [MarshalAs(UnmanagedType.U1)]
            public bool generateMipmaps;
            public MipMapSampling mipmapSampling;
            public IntPtr scan0;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void DdsWriteImageCallback(IntPtr image, UIntPtr imageSize);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void DdsProgressCallback(UIntPtr done, UIntPtr total);

        private static class DdsIO_x86
        {
            [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1060:MovePInvokesToNativeMethodsClass")]
            [DllImport("DdsFileTypePlusIO_x86.dll", CallingConvention = CallingConvention.StdCall)]
            internal static unsafe extern int Load([In] byte* input, [In] UIntPtr inputSize, [In, Out] ref DDSLoadInfo info);

            [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1060:MovePInvokesToNativeMethodsClass")]
            [DllImport("DdsFileTypePlusIO_x86.dll", CallingConvention = CallingConvention.StdCall)]
            internal static extern void FreeLoadInfo([In, Out] ref DDSLoadInfo info);

            [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1060:MovePInvokesToNativeMethodsClass")]
            [DllImport("DdsFileTypePlusIO_x86.dll", CallingConvention = CallingConvention.StdCall)]
            internal static unsafe extern int Save(
                [In] ref DDSSaveInfo input,
                [In, MarshalAs(UnmanagedType.FunctionPtr)] DdsWriteImageCallback writeImageCallback,
                [In, MarshalAs(UnmanagedType.FunctionPtr)] DdsProgressCallback progressCallback);
        }

        private static class DdsIO_x64
        {
            [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1060:MovePInvokesToNativeMethodsClass")]
            [DllImport("DdsFileTypePlusIO_x64.dll", CallingConvention = CallingConvention.StdCall)]
            internal static unsafe extern int Load([In] byte* input, [In] UIntPtr inputSize, [In, Out] ref DDSLoadInfo info);

            [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1060:MovePInvokesToNativeMethodsClass")]
            [DllImport("DdsFileTypePlusIO_x64.dll", CallingConvention = CallingConvention.StdCall)]
            internal static extern void FreeLoadInfo([In, Out] ref DDSLoadInfo info);

            [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1060:MovePInvokesToNativeMethodsClass")]
            [DllImport("DdsFileTypePlusIO_x64.dll", CallingConvention = CallingConvention.StdCall)]
            internal static unsafe extern int Save(
                [In] ref DDSSaveInfo input,
                [In, MarshalAs(UnmanagedType.FunctionPtr)] DdsWriteImageCallback writeImageCallback,
                [In, MarshalAs(UnmanagedType.FunctionPtr)] DdsProgressCallback progressCallback);
        }

        private static class HResult
        {
            public const int NotSupported = unchecked((int)0x80070032); // HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)
            public const int InvalidData = unchecked((int)0x8007000D); // HRESULT_FROM_WIN32(ERROR_INVALID_DATA)
        }

        private static bool FAILED(int hr)
        {
            return hr < 0;
        }

        public static unsafe Document Load(Stream input)
        {
            DDSLoadInfo info = new DDSLoadInfo();

            LoadDdsFile(input, ref info);

            Document doc = null;

            try
            {
                doc = new Document(info.width, info.height);

                BitmapLayer layer = Layer.CreateBackgroundLayer(info.width, info.height);

                Surface surface = layer.Surface;

                for (int y = 0; y < surface.Height; y++)
                {
                    byte* src = (byte*)info.scan0 + (y * info.stride);
                    ColorBgra* dst = surface.GetRowAddressUnchecked(y);

                    for (int x = 0; x < surface.Width; x++)
                    {
                        dst->R = src[0];
                        dst->G = src[1];
                        dst->B = src[2];
                        dst->A = src[3];

                        src += 4;
                        dst++;
                    }
                }

                doc.Layers.Add(layer);
            }
            finally
            {
                FreeLoadInfo(ref info);
            }

            return doc;
        }

        public static unsafe void Save(
            Document input,
            Stream output,
            DdsFileFormat format,
            DdsErrorMetric errorMetric,
            BC7CompressionMode compressionMode,
            bool generateMipmaps,
            MipMapSampling sampling,
            Surface scratchSurface,
            ProgressEventHandler progressCallback)
        {
            using (RenderArgs args = new RenderArgs(scratchSurface))
            {
                input.Render(args, true);
            }

            DdsWriteImageCallback ddsWriteImage = delegate (IntPtr image, UIntPtr imageSize)
            {
                ulong size = imageSize.ToUInt64();

                if (image != IntPtr.Zero && size > 0)
                {
                    const int MaxBufferSize = 65536;

                    ulong streamBufferSize = Math.Min(MaxBufferSize, size);
                    byte[] streamBuffer = new byte[streamBufferSize];

                    output.SetLength(checked((long)size));

                    ulong offset = 0;
                    ulong remaining = size;

                    fixed (byte* destPtr = streamBuffer)
                    {
                        byte* srcPtr = (byte*)image.ToPointer();

                        while (remaining > 0)
                        {
                            ulong copySize = Math.Min(MaxBufferSize, remaining);

                            Buffer.MemoryCopy(srcPtr + offset, destPtr, streamBufferSize, copySize);

                            output.Write(streamBuffer, 0, (int)copySize);

                            offset += copySize;
                            remaining -= copySize;
                        }
                    }
                }
            };

            DdsProgressCallback ddsProgress = delegate (UIntPtr done, UIntPtr total)
            {
                double progress = (double)done.ToUInt64() / (double)total.ToUInt64();
                progressCallback(null, new ProgressEventArgs(progress * 100.0, true));
            };

            SaveDdsFile(scratchSurface, format, errorMetric, compressionMode, generateMipmaps, sampling, ddsWriteImage, ddsProgress);

            GC.KeepAlive(ddsWriteImage);
        }

        private static unsafe void LoadDdsFile(Stream stream, ref DDSLoadInfo info)
        {
            byte[] buffer = new byte[stream.Length];
            stream.ProperRead(buffer, 0, buffer.Length);

            int hr;

            fixed (byte* pBytes = buffer)
            {
                if (IntPtr.Size == 8)
                {
                    hr = DdsIO_x64.Load(pBytes, new UIntPtr((ulong)buffer.Length), ref info);
                }
                else
                {
                    hr = DdsIO_x86.Load(pBytes, new UIntPtr((ulong)buffer.Length), ref info);
                }
            }

            if (FAILED(hr))
            {
                switch (hr)
                {
                    case HResult.InvalidData:
                        throw new FormatException("The DDS file is invalid.");
                    case HResult.NotSupported:
                        throw new FormatException("The file is not a supported DDS format.");
                    default:
                        Marshal.ThrowExceptionForHR(hr);
                        break;
                }
            }
        }

        private static void FreeLoadInfo(ref DDSLoadInfo info)
        {
            if (IntPtr.Size == 8)
            {
                DdsIO_x64.FreeLoadInfo(ref info);
            }
            else
            {
                DdsIO_x86.FreeLoadInfo(ref info);
            }
        }

        private static unsafe void SaveDdsFile(
            Surface surface,
            DdsFileFormat format,
            DdsErrorMetric errorMetric,
            BC7CompressionMode compressionMode,
            bool generateMipmaps,
            MipMapSampling mipMapSampling,
            DdsWriteImageCallback writeImageCallback,
            DdsProgressCallback progressCallback)
        {
            DDSSaveInfo info = new DDSSaveInfo
            {
                width = surface.Width,
                height = surface.Height,
                stride = surface.Stride,
                format = format,
                errorMetric = errorMetric,
                compressionMode = compressionMode,
                generateMipmaps = generateMipmaps,
                mipmapSampling = mipMapSampling,
                scan0 = surface.Scan0.Pointer
            };

            int hr;

            if (IntPtr.Size == 8)
            {
                hr = DdsIO_x64.Save(ref info, writeImageCallback, progressCallback);
            }
            else
            {
                hr = DdsIO_x86.Save(ref info, writeImageCallback, progressCallback);
            }

            if (FAILED(hr))
            {
                Marshal.ThrowExceptionForHR(hr);
            }
        }
    }
}
