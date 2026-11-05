using System;
using System.Runtime.InteropServices;

namespace RDM.Shared.Common;

/// <summary>Đánh giá khả năng bảo mật của hệ điều hành hiện tại (Phần 8.10). Không hạ cấp, chỉ báo đúng thực tế.</summary>
public sealed class PlatformInfo
{
    public Version OsVersion { get; private set; }
    public bool IsServer { get; private set; }
    public bool Is64BitOs { get; private set; }
    public bool Is64BitProcess { get; private set; }
    public bool IsEndOfLifeOs { get; private set; }
    public bool SupportsTls13 { get; private set; }
    public bool MeetsNet48Minimum { get; private set; }
    public string Note { get; private set; }

    /// <summary>Chỉ gọi trên Windows (Controller, Client).</summary>
    public static PlatformInfo Detect() => Evaluate(Environment.OSVersion.Version, IsWindowsServer(), Environment.Is64BitOperatingSystem, Environment.Is64BitProcess);

    /// <summary>Tách khỏi Detect để kiểm thử được mọi phiên bản Windows mà không cần máy tương ứng.</summary>
    public static PlatformInfo Evaluate(Version os, bool isServer, bool is64Os, bool is64Process)
    {
        var info = new PlatformInfo
        {
            OsVersion = os, IsServer = isServer, Is64BitOs = is64Os, Is64BitProcess = is64Process,
            // .NET 4.8: Windows 7 SP1 (7601) trở lên. Windows 8 RTM (6.2 bản Client) không được hỗ trợ; Server 2012 cũng là 6.2 nhưng được hỗ trợ.
            MeetsNet48Minimum = os >= new Version(6, 1, 7601) && !(os.Major == 6 && os.Minor == 2 && !isServer),
            IsEndOfLifeOs = os < new Version(10, 0),
            // Schannel có TLS 1.3 từ Windows Server 2022 (build 20348) và Windows 11 (22000).
            SupportsTls13 = os.Major >= 10 && os.Build >= 20348
        };
        info.Note = !info.MeetsNet48Minimum
            ? "This operating system is not supported by .NET Framework 4.8."
            : info.IsEndOfLifeOs
                ? "This operating system is no longer supported by Microsoft. Internet connections are refused if it cannot meet the minimum TLS level."
                : info.SupportsTls13 ? "Supports TLS 1.3 and TLS 1.2." : "Supports TLS 1.2; this operating system has no TLS 1.3.";
        return info;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OsVersionInfoEx
    {
        public int Size, Major, Minor, Build, PlatformId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string CsdVersion;
        public ushort SpMajor, SpMinor, SuiteMask;
        public byte ProductType, Reserved;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetVersionExW")]
    private static extern bool GetVersionEx(ref OsVersionInfoEx info);

    private static bool IsWindowsServer()
    {
        const byte workstation = 1;
        var info = new OsVersionInfoEx { Size = Marshal.SizeOf(typeof(OsVersionInfoEx)) };
        return GetVersionEx(ref info) && info.ProductType != workstation;
    }
}
