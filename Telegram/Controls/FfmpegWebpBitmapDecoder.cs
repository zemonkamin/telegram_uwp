using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.UI.Xaml.Media.Imaging;

namespace Telegram.Controls
{
    internal static class FfmpegWebpBitmapDecoder
    {
        private const int AV_PIX_FMT_BGRA = 28;
        private const int SWS_BILINEAR = 2;

        public static async Task<WriteableBitmap> TryDecodeWebpAsync(StorageFile file)
        {
            if (file == null) return null;
            try
            {
                var bytes = await FileIO.ReadBufferAsync(file);
                if (bytes == null || bytes.Length == 0) return null;

                var managed = bytes.ToArray();
                return DecodeWebp(managed);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("TG_WEBP_DECODER read failed " + ex.GetType().Name);
                return null;
            }
        }

        private static WriteableBitmap DecodeWebp(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return null;
            var libWebpBitmap = DecodeWebpWithLibWebp(bytes);
            if (libWebpBitmap != null) return libWebpBitmap;

            IntPtr codec = IntPtr.Zero;
            IntPtr codecContext = IntPtr.Zero;
            IntPtr packet = IntPtr.Zero;
            IntPtr frame = IntPtr.Zero;
            IntPtr swsContext = IntPtr.Zero;
            IntPtr dstBuffer = IntPtr.Zero;

            try
            {
                TryRegisterAll();

                codec = avcodec_find_decoder_by_name("webp");
                if (codec == IntPtr.Zero)
                {
                    Debug.WriteLine("TG_WEBP_DECODER webp codec not found");
                    return null;
                }

                codecContext = avcodec_alloc_context3(codec);
                if (codecContext == IntPtr.Zero) return null;

                if (avcodec_open2(codecContext, codec, IntPtr.Zero) < 0)
                    return null;

                packet = av_packet_alloc();
                if (packet == IntPtr.Zero) return null;
                if (av_new_packet(packet, bytes.Length) < 0) return null;

                var packetNative = (AVPacketNative)Marshal.PtrToStructure(packet, typeof(AVPacketNative));
                if (packetNative.data == IntPtr.Zero || packetNative.size < bytes.Length) return null;
                Marshal.Copy(bytes, 0, packetNative.data, bytes.Length);

                frame = av_frame_alloc();
                if (frame == IntPtr.Zero) return null;

                var sendResult = avcodec_send_packet(codecContext, packet);
                if (sendResult < 0) return null;

                var receiveResult = avcodec_receive_frame(codecContext, frame);
                if (receiveResult < 0) return null;

                var frameNative = (AVFrameNative)Marshal.PtrToStructure(frame, typeof(AVFrameNative));
                var width = frameNative.width;
                var height = frameNative.height;
                var sourceFormat = frameNative.format;
                if (width <= 0 || height <= 0 || width > 4096 || height > 4096) return null;

                var stride = width * 4;
                var bufferSize = stride * height;
                dstBuffer = Marshal.AllocHGlobal(bufferSize);
                if (dstBuffer == IntPtr.Zero) return null;

                var dstData = new IntPtr[4];
                var dstLinesize = new int[4];
                dstData[0] = dstBuffer;
                dstLinesize[0] = stride;

                swsContext = sws_getContext(width, height, sourceFormat, width, height, AV_PIX_FMT_BGRA, SWS_BILINEAR, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (swsContext == IntPtr.Zero) return null;

                var scaleResult = sws_scale(swsContext, frameNative.data, frameNative.linesize, 0, height, dstData, dstLinesize);
                if (scaleResult <= 0) return null;

                var pixels = new byte[bufferSize];
                Marshal.Copy(dstBuffer, pixels, 0, pixels.Length);
                PremultiplyBgra(pixels);

                var bitmap = new WriteableBitmap(width, height);
                using (var output = bitmap.PixelBuffer.AsStream())
                {
                    output.Write(pixels, 0, pixels.Length);
                    output.Flush();
                }
                bitmap.Invalidate();
                return bitmap;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("TG_WEBP_DECODER decode failed " + ex.GetType().Name);
                return null;
            }
            finally
            {
                if (dstBuffer != IntPtr.Zero) Marshal.FreeHGlobal(dstBuffer);
                if (swsContext != IntPtr.Zero) sws_freeContext(swsContext);
                if (frame != IntPtr.Zero) av_frame_free(ref frame);
                if (packet != IntPtr.Zero) av_packet_free(ref packet);
                if (codecContext != IntPtr.Zero) avcodec_free_context(ref codecContext);
            }
        }

        private static WriteableBitmap DecodeWebpWithLibWebp(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return null;
            try
            {
                int width;
                int height;
                if (WebPGetInfo(bytes, new UIntPtr((uint)bytes.Length), out width, out height) == 0)
                    return null;
                if (width <= 0 || height <= 0 || width > 4096 || height > 4096) return null;

                var stride = width * 4;
                var bufferSize = stride * height;
                var pixels = new byte[bufferSize];
                var decoded = WebPDecodeBGRAInto(bytes, new UIntPtr((uint)bytes.Length), pixels, new UIntPtr((uint)pixels.Length), stride);
                if (decoded == IntPtr.Zero) return null;

                PremultiplyBgra(pixels);
                var bitmap = new WriteableBitmap(width, height);
                using (var output = bitmap.PixelBuffer.AsStream())
                {
                    output.Write(pixels, 0, pixels.Length);
                    output.Flush();
                }
                bitmap.Invalidate();
                Debug.WriteLine("TG_WEBP_DECODER libwebp decoded " + width.ToString() + "x" + height.ToString());
                return bitmap;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("TG_WEBP_DECODER libwebp failed " + ex.GetType().Name);
                return null;
            }
        }

        private static void PremultiplyBgra(byte[] pixels)
        {
            if (pixels == null) return;
            for (var i = 0; i + 3 < pixels.Length; i += 4)
            {
                var alpha = pixels[i + 3];
                if (alpha <= 8)
                {
                    // Some WEBP decoders leave non-zero RGB in fully transparent/near-transparent pixels.
                    // UWP WriteableBitmap can expose those stale colors as single-pixel garbage on W10M.
                    pixels[i] = 0;
                    pixels[i + 1] = 0;
                    pixels[i + 2] = 0;
                    pixels[i + 3] = 0;
                }
                else if (alpha < 255)
                {
                    pixels[i] = (byte)((pixels[i] * alpha + 127) / 255);
                    pixels[i + 1] = (byte)((pixels[i + 1] * alpha + 127) / 255);
                    pixels[i + 2] = (byte)((pixels[i + 2] * alpha + 127) / 255);
                }
            }
        }

        private static void TryRegisterAll()
        {
            try { avcodec_register_all(); }
            catch { }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AVPacketNative
        {
            public IntPtr buf;
            public long pts;
            public long dts;
            public IntPtr data;
            public int size;
            public int stream_index;
            public int flags;
            public IntPtr side_data;
            public int side_data_elems;
            public long duration;
            public long pos;
            public long convergence_duration;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AVFrameNative
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public IntPtr[] data;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public int[] linesize;
            public IntPtr extended_data;
            public int width;
            public int height;
            public int nb_samples;
            public int format;
        }

        [DllImport("avcodec-57", CallingConvention = CallingConvention.Cdecl)]
        private static extern void avcodec_register_all();

        [DllImport("avcodec-57", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr avcodec_find_decoder_by_name([MarshalAs(UnmanagedType.LPStr)] string name);

        [DllImport("avcodec-57", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr avcodec_alloc_context3(IntPtr codec);

        [DllImport("avcodec-57", CallingConvention = CallingConvention.Cdecl)]
        private static extern int avcodec_open2(IntPtr avctx, IntPtr codec, IntPtr options);

        [DllImport("avcodec-57", CallingConvention = CallingConvention.Cdecl)]
        private static extern void avcodec_free_context(ref IntPtr avctx);

        [DllImport("avcodec-57", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr av_packet_alloc();

        [DllImport("avcodec-57", CallingConvention = CallingConvention.Cdecl)]
        private static extern int av_new_packet(IntPtr pkt, int size);

        [DllImport("avcodec-57", CallingConvention = CallingConvention.Cdecl)]
        private static extern void av_packet_free(ref IntPtr pkt);

        [DllImport("avcodec-57", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr av_frame_alloc();

        [DllImport("avcodec-57", CallingConvention = CallingConvention.Cdecl)]
        private static extern void av_frame_free(ref IntPtr frame);

        [DllImport("avcodec-57", CallingConvention = CallingConvention.Cdecl)]
        private static extern int avcodec_send_packet(IntPtr avctx, IntPtr avpkt);

        [DllImport("avcodec-57", CallingConvention = CallingConvention.Cdecl)]
        private static extern int avcodec_receive_frame(IntPtr avctx, IntPtr frame);

        [DllImport("swscale-4", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sws_getContext(int srcW, int srcH, int srcFormat, int dstW, int dstH, int dstFormat, int flags, IntPtr srcFilter, IntPtr dstFilter, IntPtr param);

        [DllImport("swscale-4", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sws_scale(IntPtr c, IntPtr[] srcSlice, int[] srcStride, int srcSliceY, int srcSliceH, IntPtr[] dst, int[] dstStride);

        [DllImport("swscale-4", CallingConvention = CallingConvention.Cdecl)]
        private static extern void sws_freeContext(IntPtr c);

        [DllImport("libwebp", CallingConvention = CallingConvention.Cdecl)]
        private static extern int WebPGetInfo(byte[] data, UIntPtr data_size, out int width, out int height);

        [DllImport("libwebp", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr WebPDecodeBGRAInto(byte[] data, UIntPtr data_size, byte[] output_buffer, UIntPtr output_buffer_size, int output_stride);
    }
}
