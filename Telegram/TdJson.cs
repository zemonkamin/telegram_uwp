using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Telegram
{
    public static class TdJson
    {
        [DllImport("tdjson.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr td_json_client_create();

        [DllImport("tdjson.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void td_json_client_send(IntPtr client, IntPtr request);

        [DllImport("tdjson.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr td_json_client_receive(IntPtr client, double timeout);

        public static void SendUtf8(IntPtr client, string request)
        {
            if (client == IntPtr.Zero || string.IsNullOrEmpty(request)) return;

            var bytes = Encoding.UTF8.GetBytes(request + "\0");
            var ptr = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, ptr, bytes.Length);
                td_json_client_send(client, ptr);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        public static string IntPtrToStringUtf8(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero) return null;

            var len = 0;
            while (Marshal.ReadByte(ptr, len) != 0) len++;
            if (len == 0) return string.Empty;

            var buffer = new byte[len];
            Marshal.Copy(ptr, buffer, 0, len);
            return Encoding.UTF8.GetString(buffer, 0, buffer.Length);
        }
    }
}
