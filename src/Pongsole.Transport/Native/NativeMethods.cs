using System.Runtime.InteropServices;

namespace Pongsole.Transport.Native;

internal static class NativeMethods
{
    private const string Library = "datachannel";
    private const CallingConvention C = CallingConvention.Cdecl;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Configuration
    {
        public nint Servers;
        public int ServerCount;
        public nint Proxy;
        public nint BindAddress;
        public int Certificate;
        public int Policy;
        public byte Tcp, UdpMux, ManualNegotiation, ForceMedia;
        public ushort PortBegin, PortEnd;
        public int Mtu, MaxMessage;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Reliability
    {
        public byte Unordered, Unreliable;
        public uint Lifetime, Retransmits;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ChannelInit
    {
        public Reliability Reliability;
        public nint Protocol;
        public byte Negotiated, ManualStream;
        public ushort Stream;
    }

    [UnmanagedFunctionPointer(C)] internal delegate void State(int id, int state, nint context);
    [UnmanagedFunctionPointer(C)] internal delegate void Simple(int id, nint context);
    [UnmanagedFunctionPointer(C)] internal delegate void Message(int id, nint data, int size, nint context);

    [DllImport(Library, CallingConvention = C)] internal static extern void rtcInitLogger(int level, nint callback);
    [DllImport(Library, CallingConvention = C)] internal static extern void rtcCleanup();
    [DllImport(Library, CallingConvention = C)] internal static extern void rtcSetUserPointer(int id, nint owner);
    [DllImport(Library, CallingConvention = C)] internal static extern int rtcCreatePeerConnection(in Configuration config);
    [DllImport(Library, CallingConvention = C)] internal static extern int rtcDeletePeerConnection(int peer);
    [DllImport(Library, CallingConvention = C)] internal static extern int rtcSetGatheringStateChangeCallback(int peer, State callback);
    [DllImport(Library, CallingConvention = C)] internal static extern int rtcSetStateChangeCallback(int peer, State callback);
    [DllImport(Library, CallingConvention = C)] internal static extern int rtcSetDataChannelCallback(int peer, State callback);
    [DllImport(Library, CallingConvention = C)] internal static extern int rtcSetOpenCallback(int channel, Simple callback);
    [DllImport(Library, CallingConvention = C)] internal static extern int rtcSetClosedCallback(int channel, Simple callback);
    [DllImport(Library, CallingConvention = C)] internal static extern int rtcSetBufferedAmountLowCallback(int channel, Simple callback);
    [DllImport(Library, CallingConvention = C)] internal static extern int rtcSetBufferedAmountLowThreshold(int channel, int amount);
    [DllImport(Library, CallingConvention = C)] internal static extern int rtcSetMessageCallback(int channel, Message callback);
    [DllImport(Library, CallingConvention = C)] internal static extern int rtcSetLocalDescription(int peer, [MarshalAs(UnmanagedType.LPUTF8Str)] string type);
    [DllImport(Library, CallingConvention = C)] internal static extern int rtcSetRemoteDescription(int peer, [MarshalAs(UnmanagedType.LPUTF8Str)] string sdp, [MarshalAs(UnmanagedType.LPUTF8Str)] string type);
    [DllImport(Library, CallingConvention = C)] internal static extern int rtcGetLocalDescription(int peer, [Out] byte[] buffer, int size);
    [DllImport(Library, CallingConvention = C)] internal static extern int rtcCreateDataChannelEx(int peer, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, in ChannelInit init);
    [DllImport(Library, CallingConvention = C)] internal static extern int rtcGetDataChannelLabel(int channel, [Out] byte[] buffer, int size);
    [DllImport(Library, CallingConvention = C)] internal static extern int rtcGetDataChannelReliability(int channel, out Reliability reliability);
    [DllImport(Library, CallingConvention = C)] internal static extern int rtcSendMessage(int channel, byte[] bytes, int size);
    [DllImport(Library, CallingConvention = C)] internal static extern int rtcGetBufferedAmount(int channel);
    [DllImport(Library, CallingConvention = C)] internal static extern int rtcDeleteDataChannel(int channel);
    [DllImport(Library, CallingConvention = C)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool rtcIsOpen(int channel);

    internal static int Check(int result)
    {
        if (result < 0) throw new IOException($"Native WebRTC operation failed ({result}).");
        return result;
    }

    internal static string Decode(byte[] buffer) =>
        System.Text.Encoding.UTF8.GetString(buffer.AsSpan(0, Array.IndexOf(buffer, (byte)0)));
}
