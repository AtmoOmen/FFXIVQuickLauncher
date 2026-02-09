#nullable enable
using System;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using DeviceId;
using Serilog;

namespace XIVLauncher.Common;

internal static class SdoUtils
{
    private static readonly Lazy<string> DeviceID = 
        new(() => string.Join(":", GetMacAddress(), GetCPUID(), GetDiskSerialNumber()));

    public static string GetDeviceID() => DeviceID.Value;

    public static string GetMD5(byte[] payload)
    {
        var md5Bytes = MD5.HashData(payload);
        return Convert.ToHexString(md5Bytes).ToUpper();
    }

    private static string GetCPUID()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var processorID = new DeviceIdBuilder().OnLinux(linux => linux.AddCpuInfo()).ToString();
            return GetMD5(Encoding.ASCII.GetBytes(processorID));
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // return String.Empty;
            var processorID = new DeviceIdBuilder().OnMac(mac => mac.AddPlatformSerialNumber()).ToString();
            return GetMD5(Encoding.ASCII.GetBytes(processorID));
        }

        var result = string.Empty;

        try
        {
            var asmCode       = new CpuIdAssemblyCode();
            var cpuinfoBuffer = new byte[0x30];

            for (var i = 0; i < 3; i++)
            {
                var info = new CpuIdAssemblyCode.CpuIdInfo();
                asmCode.Call(i                                                  - 0x7FFFFFFE, ref info);
                BitConverter.GetBytes(info.Eax).CopyTo(cpuinfoBuffer, 4 * 4 * i + 0);
                BitConverter.GetBytes(info.Ebx).CopyTo(cpuinfoBuffer, 4 * 4 * i + 4);
                BitConverter.GetBytes(info.Ecx).CopyTo(cpuinfoBuffer, 4 * 4 * i + 8);
                BitConverter.GetBytes(info.Edx).CopyTo(cpuinfoBuffer, 4 * 4 * i + 12);
            }

            asmCode.Dispose();
            var validLength = cpuinfoBuffer.Length;
            while (cpuinfoBuffer[validLength - 1] == '\0' && validLength > 1)
                validLength--;
            return GetMD5(cpuinfoBuffer.Take(validLength).ToArray());
        }
        catch
        {
            Log.Error("Failed to get CPU ID");
        }

        return result;
    }

    public static string GetMacAddress()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var macId = new DeviceIdBuilder().OnLinux(linux => linux.AddMachineId()).ToString();
            return GetMD5(Encoding.ASCII.GetBytes(macId));
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var macId = new DeviceIdBuilder().OnMac(mac => mac.AddPlatformSerialNumber().AddSystemDriveSerialNumber()).ToString();
            return GetMD5(Encoding.ASCII.GetBytes(macId));
        }

        var result = string.Empty;

        try
        {
            var macAddress = WINGetAdaptersInfo.GetAdapters();
            return GetMD5(Encoding.ASCII.GetBytes(macAddress));
        }
        catch
        {
            Log.Error("Failed to get MacAddress");
        }

        return result;
    }

    public static string GetMac()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return new DeviceIdBuilder().OnLinux(linux => linux.AddMachineId()).ToString();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return new DeviceIdBuilder().OnMac(mac => mac.AddPlatformSerialNumber().AddSystemDriveSerialNumber()).ToString();

        var result = string.Empty;

        try
        {
            return WINGetAdaptersInfo.GetAdapters();
        }
        catch
        {
            Log.Error("Failed to get MacAddress");
        }

        return result;
    }

    public static string GetHostName() =>
        // 给盛趣一些MacBook和SteamDick震撼
        // 不会返回一个 张二狗的MacBook吧？实名上网？
        Environment.MachineName;

    private static string GetDiskSerialNumber()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var diskId = new DeviceIdBuilder().OnLinux(linux => linux.AddSystemDriveSerialNumber()).ToString();
            return GetMD5(Encoding.ASCII.GetBytes(diskId));
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var diskId = new DeviceIdBuilder().OnMac(mac => mac.AddSystemDriveSerialNumber()).ToString();
            return GetMD5(Encoding.ASCII.GetBytes(diskId));
        }

        var result = string.Empty;

        try
        {
            var getPartitionsOnDisk = new
                ManagementObjectSearcher("select * from Win32_DiskDrive");

            var hardDiskID = "";

            foreach (var partition in getPartitionsOnDisk.Get())
            {
                var mo = (ManagementObject?)partition;
                if (mo == null) continue;
                
                if (mo["Index"].ToString() != "0") continue;
                hardDiskID = mo["SerialNumber"].ToString();
                break;
            }

            if (hardDiskID != null)
                result = GetMD5(Encoding.ASCII.GetBytes(hardDiskID));
        }
        catch
        {
            Log.Error("Failed to get DiskSerialNumber");
        }

        return result;
    }
}

internal class WINGetAdaptersInfo
{
    [DllImport("iphlpapi.dll")]
    private static extern int GetAdaptersInfo(IntPtr pAdapterInfo, ref long pBufOutLen);

    [StructLayout(LayoutKind.Sequential)]
    private struct IPAddressString
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)]
        public string Address;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IPAddrString
    {
        public IntPtr            Next;
        public IPAddressString IpAddress;
        public IPAddressString IpMask;
        public Int32             Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IPAdapterInfo
    {
        public IntPtr Next;
        public Int32  ComboIndex;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256 + 4)]
        public string AdapterName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128 + 4)]
        public string AdapterDescription;

        public UInt32 AddressLength;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public byte[] Address;

        public Int32          Index;
        public UInt32         Type;
        public UInt32         DhcpEnabled;
        public IntPtr         CurrentIpAddress;
        public IPAddrString IpAddressList;
        public IPAddrString GatewayList;
        public IPAddrString DhcpServer;
        public bool           HaveWins;
        public IPAddrString PrimaryWinsServer;
        public IPAddrString SecondaryWinsServer;
        public Int32          LeaseObtained;
        public Int32          LeaseExpires;
    }

    public static string GetAdapters()
    {
        long structSize = Marshal.SizeOf<IPAdapterInfo>();
        var  pArray     = Marshal.AllocHGlobal((int)new IntPtr(structSize));
        var  macAddress = string.Empty;
        var  ret        = GetAdaptersInfo(pArray, ref structSize);

        if (ret == 111) // ERROR_BUFFER_OVERFLOW == 111
        {
            pArray = Marshal.ReAllocHGlobal(pArray, new IntPtr(structSize));
            ret    = GetAdaptersInfo(pArray, ref structSize);
        } // if

        if (ret == 0)
        {
            // Call Succeeded
            var entry = Marshal.PtrToStructure<IPAdapterInfo>(pArray);
            var mac   = new string[entry.AddressLength];
            for (var i = 0; i < entry.AddressLength; i++)
                mac[i] = $"{entry.Address[i]:X2}";
            macAddress = string.Join("-", mac);
            Marshal.FreeHGlobal(pArray);
        } // if
        else
        {
            Marshal.FreeHGlobal(pArray);
            throw new InvalidOperationException("GetAdaptersInfo failed: " + ret);
        }

        return macAddress;
    } // GetAdapters
}

internal sealed class CpuIdAssemblyCode
    : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    internal ref struct CpuIdInfo
    {
        public uint Eax;
        public uint Ebx;
        public uint Ecx;
        public uint Edx;
    }

    [DllImport("kernel32.dll", EntryPoint = "VirtualAlloc")]
    internal static extern IntPtr VirtualAlloc(IntPtr lpAddress, UIntPtr dwSize, uint flAllocationType, uint flProtect);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CpuIDDelegate(int level, ref CpuIdInfo cpuId);

    [DllImport("kernel32.dll", EntryPoint = "VirtualFree")]
    internal static extern bool VirtualFree(IntPtr lpAddress, uint dwSize, int dwFreeType);

    private readonly IntPtr        codePointer;
    private readonly uint          size;
    private readonly CpuIDDelegate cpuIDDelegate;

    public CpuIdAssemblyCode()
    {
        var codeBytes = IntPtr.Size == 4 ? X86CodeBytes : X64CodeBytes;

        size        = (uint)codeBytes.Length;
        codePointer = VirtualAlloc(IntPtr.Zero, new UIntPtr(size), 0x1000 | 0x2000, 0x40);

        Marshal.Copy(codeBytes, 0, codePointer, codeBytes.Length);
        cpuIDDelegate = Marshal.GetDelegateForFunctionPointer<CpuIDDelegate>(codePointer);
    }

    ~CpuIdAssemblyCode()
    {
        Dispose(false);
    }

    public void Call(int level, ref CpuIdInfo cpuInfo)
    {
        cpuIDDelegate(level, ref cpuInfo);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        VirtualFree(codePointer, size, 0x8000);
    }

    private static readonly byte[] X86CodeBytes =
    [
        0x55,       // push        ebp  
        0x8B, 0xEC, // mov         ebp,esp
        0x53,       // push        ebx  
        0x57,       // push        edi

        0x8B, 0x45, 0x08, // mov         eax, dword ptr [ebp+8] (move level into eax)
        0x0F, 0xA2,       // cpuid

        0x8B, 0x7D, 0x0C, // mov         edi, dword ptr [ebp+12] (move address of buffer into edi)
        0x89, 0x07,       // mov         dword ptr [edi+0], eax  (write eax, ... to buffer)
        0x89, 0x5F, 0x04, // mov         dword ptr [edi+4], ebx 
        0x89, 0x4F, 0x08, // mov         dword ptr [edi+8], ecx 
        0x89, 0x57, 0x0C, // mov         dword ptr [edi+12],edx 

        0x5F,       // pop         edi  
        0x5B,       // pop         ebx  
        0x8B, 0xE5, // mov         esp,ebp  
        0x5D,       // pop         ebp 
        0xc3        // ret
    ];

    private static readonly byte[] X64CodeBytes =
    [
        0x53, // push rbx    this gets clobbered by cpuid

        // rcx is level
        // rdx is buffer.
        // Need to save buffer elsewhere, cpuid overwrites rdx
        // Put buffer in r8, use r8 to reference buffer later.

        // Save rdx (buffer addy) to r8
        0x49, 0x89, 0xd0, // mov r8,  rdx

        // Move ecx (level) to eax to call cpuid, call cpuid
        0x89, 0xc8, // mov eax, ecx
        0x0F, 0xA2, // cpuid

        // Write eax et al to buffer
        0x41, 0x89, 0x40, 0x00, // mov    dword ptr [r8+0],  eax
        0x41, 0x89, 0x58, 0x04, // mov    dword ptr [r8+4],  ebx
        0x41, 0x89, 0x48, 0x08, // mov    dword ptr [r8+8],  ecx
        0x41, 0x89, 0x50, 0x0c, // mov    dword ptr [r8+12], edx

        0x5b, // pop rbx
        0xc3  // ret
    ];
}
