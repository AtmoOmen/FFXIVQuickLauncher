using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Iced.Intel;
using Reloaded.Memory;
using Reloaded.Memory.Buffers;
using Reloaded.Memory.Buffers.Structs;
using Reloaded.Memory.Buffers.Structs.Params;
using Reloaded.Memory.Enums;
using Reloaded.Memory.Interfaces;
using Reloaded.Memory.Sigscan;
using Serilog;
using static Iced.Intel.AssemblerRegisters;
using Decoder = Iced.Intel.Decoder;

namespace XIVLauncher.Common.Game;

public static class GameArgumentInterop
{
    public sealed class LoginData : IEquatable<LoginData>
    {
        public string[] Args { get; set; } = [];

        public string SessionId { get; set; } = string.Empty;

        public string SndaID { get; set; } = string.Empty;

        public string CommandLine { get; set; } = string.Empty;

        public bool Equals(LoginData? other)
        {
            if (other is null)
                return false;

            return string.Equals(SessionId, other.SessionId, StringComparison.Ordinal)
                   && string.Equals(SndaID, other.SndaID,    StringComparison.Ordinal);
        }

        public override bool Equals(object? obj) =>
            obj is LoginData other && Equals(other);

        public override int GetHashCode() =>
            HashCode.Combine(SessionId, SndaID);

        public bool IsWeGame() =>
            Args.Contains("rail_zone_state=1", StringComparer.Ordinal);
    }

    public sealed class Reader
    {
        private readonly ExternalMemory externalMemory;
        private readonly Scanner        scanner;
        private readonly Process        targetProcess;

        private readonly nuint gameWindowPointer;

        public Reader(Process targetProcess)
        {
            this.targetProcess = targetProcess ?? throw new ArgumentNullException(nameof(targetProcess));

            var mainModule = this.targetProcess.MainModule ?? throw new InvalidOperationException("无法获取游戏主模块");
            externalMemory = new ExternalMemory(this.targetProcess.Handle);
            var executableData = externalMemory.ReadRaw((nuint)mainModule.BaseAddress, mainModule.ModuleMemorySize);
            scanner           = new Scanner(executableData);
            gameWindowPointer = GetGameWindowPointer(mainModule.BaseAddress);
        }

        public LoginData ReadLoginData()
        {
            Thread.Sleep(1000);

            var   data          = new LoginData();
            ulong argumentCount = 0;
            var   retries       = 20;

            while (retries-- > 0 && argumentCount == 0)
            {
                externalMemory.Read(gameWindowPointer, out argumentCount);
                if (argumentCount > 0)
                    break;

                Thread.Sleep(1000);
            }

            externalMemory.Read(gameWindowPointer, out argumentCount);
            data.Args = new string[argumentCount];

            externalMemory.Read(gameWindowPointer + 8, out nuint argumentListPointer);

            for (var index = 0; index < (int)argumentCount; index++)
            {
                externalMemory.Read(argumentListPointer + (nuint)(8 * index), out nuint argumentPointer);
                var argument = ReadString(argumentPointer, Encoding.UTF8);
                Log.Information("{ArgumentPointer:X},{Argument}", argumentPointer, argument);
                data.Args[index] = argument;
            }

            if (!data.IsWeGame())
            {
                Log.Information("{ProcessId} is not WeGame", targetProcess.Id);
                return data;
            }

            for (var index = 0; index < 20; index++)
            {
                externalMemory.Read(gameWindowPointer + 0xA0, out nuint sessionIdPointer);
                externalMemory.Read(gameWindowPointer + 0xA8, out nuint sndaIdPointer);
                externalMemory.Read(gameWindowPointer + 0xB8, out nuint commandLinePointer);

                if (sessionIdPointer == 0 || sndaIdPointer == 0)
                {
                    Thread.Sleep(500);
                    Log.Information("try {Index}: sidPtr:{SessionIdPointer:X}, sndaIdPtr:{SndaIdPointer:X}", index, sessionIdPointer, sndaIdPointer);
                    continue;
                }

                data.SessionId   = ReadString(sessionIdPointer,   Encoding.UTF8);
                data.SndaID      = ReadString(sndaIdPointer,      Encoding.UTF8);
                data.CommandLine = ReadString(commandLinePointer, Encoding.UTF8);

                Log.Information("SessionId:{SessionIdPointer:X},{SessionId}",       sessionIdPointer,   MaskString(data.SessionId));
                Log.Information("SndaId:{SndaIdPointer:X},{SndaId}",                sndaIdPointer,      MaskString(data.SndaID));
                Log.Information("CommandLine:{CommandLinePointer:X},{CommandLine}", commandLinePointer, data.CommandLine);
                break;
            }

            return data;
        }

        public void KillProcess()
        {
            if (!targetProcess.HasExited)
                targetProcess.Kill();
        }

        private static string MaskString(string input)
        {
            if (string.IsNullOrEmpty(input) || input.Length <= 2)
                return input.Length == 1 ? "*" : new string('*', input.Length);

            return $"{input[0]}{new string('*', input.Length - 2)}{input[^1]}";
        }

        private string ReadString(nuint pointer, Encoding encoding, int maxLength = 256)
        {
            var bytes       = externalMemory.ReadRaw(pointer, maxLength);
            var data        = encoding.GetString(bytes);
            var endOfString = data.IndexOf('\0');
            return endOfString < 0 ? data : data[..endOfString];
        }

        private nuint GetGameWindowPointer(IntPtr baseAddress)
        {
            const string SIGNATURE = "48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 44 38 64 24";

            var scan = scanner.FindPattern(SIGNATURE);
            if (!scan.Found)
                throw new InvalidOperationException($"无法定位 GameWindow: {SIGNATURE}");

            var address = (nuint)baseAddress + (nuint)scan.Offset;
            var originalBytes = externalMemory.ReadRaw(address, 20);

            var codeReader = new ByteArrayCodeReader(originalBytes);
            var decoder    = Decoder.Create(64, codeReader);
            decoder.IP = address;

            var endAddress = address + (nuint)originalBytes.Length;

            while (decoder.IP < endAddress)
            {
                var instruction = decoder.Decode();
                if (instruction.Code == Code.INVALID)
                    break;

                if (instruction.Code == Code.Lea_r64_m)
                    return (nuint)instruction.IPRelativeMemoryAddress;
            }

            throw new InvalidOperationException("无法解析 GameWindow 指针");
        }
    }

    public sealed class Fixer
    {
        private readonly ExternalMemory         externalMemory;
        private readonly List<PrivateAllocation> privateAllocations = [];
        private readonly Scanner                scanner;
        private readonly Process                targetProcess;

        private nint  mainModuleRegionSize;
        private nuint mainModuleBaseAddress;
        private nuint argFixFunctionAddress;

        public Fixer(Process targetProcess)
        {
            this.targetProcess = targetProcess ?? throw new ArgumentNullException(nameof(targetProcess));

            externalMemory = new ExternalMemory(this.targetProcess);
            GetMainModuleAddress();
            var executableData = externalMemory.ReadRaw(mainModuleBaseAddress, (int)mainModuleRegionSize);
            scanner = new Scanner(executableData);
        }

        public void Fix()
        {
            SetupArgFixFunction();

            const string SIGNATURE = "E8 ?? ?? ?? ?? 44 38 64 24";

            var address = scanner.FindPattern(SIGNATURE);
            if (!address.Found)
                throw new InvalidOperationException($"无法定位 sdologin: {SIGNATURE}");

            var callAddress = mainModuleBaseAddress + (nuint)address.Offset;
            externalMemory.Read(callAddress         + 1, out int functionOffset);
            var functionAddress = callAddress       + (nuint)functionOffset + 5;
            Log.Verbose("Found sdoLogin Address:{FunctionAddress:X} ({CallAddress:X} + {FunctionOffset:X} + 5)", functionAddress, callAddress, functionOffset);
            SetupHook(functionAddress);
        }

        private static byte[] Assemble(Assembler assembler, ulong rip = 0)
        {
            using var stream = new MemoryStream();
            assembler.Assemble(new StreamCodeWriter(stream), rip);
            return stream.ToArray();
        }

        private void SetupArgFixFunction()
        {
            const string TEST_SID_PREFIX = "DEV.TestSID=";
            const string SNDA_ID_PREFIX  = "XL.SndaId=";

            var testSidPrefixBytes = Encoding.ASCII.GetBytes(TEST_SID_PREFIX + '\0');
            var testSidPrefixAllocation = Buffers.AllocatePrivateMemory
            (
                new BufferAllocatorSettings
                {
                    MinAddress = 0,
                    MaxAddress = nuint.MaxValue,
                    Size = (uint)testSidPrefixBytes.Length,
                    TargetProcess = targetProcess,
                }
            );
            privateAllocations.Add(testSidPrefixAllocation);
            externalMemory.WriteRaw(testSidPrefixAllocation.BaseAddress, testSidPrefixBytes);

            var sndaIdPrefixBytes = Encoding.ASCII.GetBytes(SNDA_ID_PREFIX + '\0');
            var sndaIdPrefixAllocation = Buffers.AllocatePrivateMemory
            (
                new BufferAllocatorSettings
                {
                    MinAddress = 0,
                    MaxAddress = nuint.MaxValue,
                    Size = (uint)sndaIdPrefixBytes.Length,
                    TargetProcess = targetProcess,
                }
            );
            privateAllocations.Add(sndaIdPrefixAllocation);
            externalMemory.WriteRaw(sndaIdPrefixAllocation.BaseAddress, sndaIdPrefixBytes);

            var assembler = new Assembler(64);
            var exit      = assembler.CreateLabel();
            var testSid   = assembler.CreateLabel();
            var sndaId    = assembler.CreateLabel();
            var loop      = assembler.CreateLabel();
            var strncmp   = assembler.CreateLabel();

            assembler.mov(__[rsp + 0x08], rbx);
            assembler.mov(__[rsp + 0x10], rbp);
            assembler.mov(__[rsp + 0x18], rsi);
            assembler.mov(__[rsp + 0x20], rdi);
            assembler.push(r14);
            assembler.sub(rsp, 0x20);

            assembler.xor(r14, r14);
            assembler.mov(__qword_ptr[rcx + 0x61], 1);

            assembler.mov(rdi, rcx);
            assembler.mov(rbx, __[rcx + 8]);
            assembler.mov(ebp, r14d);
            assembler.mov(esi, r14d);
            assembler.cmp(__[rcx], r14d);
            assembler.jle(exit);

            assembler.Label(ref testSid);
            assembler.mov(rcx, __[rbx]);
            assembler.lea(rdx, __[testSidPrefixAllocation.BaseAddress]);
            assembler.mov(r8d, TEST_SID_PREFIX.Length);
            assembler.call(strncmp);
            assembler.test(eax, eax);
            assembler.jnz(sndaId);
            assembler.mov(rax,  __[rbx]);
            assembler.mov(r14d, 1);
            assembler.add(rax, TEST_SID_PREFIX.Length);
            assembler.mov(__[rdi + 0xA0], rax);
            assembler.jmp(loop);

            assembler.Label(ref sndaId);
            assembler.mov(rcx, __[rbx]);
            assembler.lea(rdx, __qword_ptr[sndaIdPrefixAllocation.BaseAddress]);
            assembler.mov(r8d, SNDA_ID_PREFIX.Length);
            assembler.call(strncmp);
            assembler.test(eax, eax);
            assembler.jnz(loop);
            assembler.mov(rax, __[rbx]);
            assembler.mov(ebp, 1);
            assembler.add(rax, SNDA_ID_PREFIX.Length);
            assembler.mov(__[rdi + 0xA8], rax);

            assembler.Label(ref loop);
            assembler.inc(esi);
            assembler.add(rbx, 8);
            assembler.cmp(esi, __[rdi]);
            assembler.jl(testSid);

            assembler.Label(ref exit);
            assembler.mov(rbx, __[rsp + 0x28 + 0x08]);
            assembler.xor(eax, 218105633);
            assembler.mov(rsi, __[rsp + 0x28 + 0x18]);
            assembler.cdq();
            assembler.mov(rdi, __[rsp + 0x28 + 0x20]);
            assembler.imul(ebp, r14d);
            assembler.idiv(ebp);
            assembler.mov(rbp, __[rsp + 0x28 + 0x10]);
            assembler.add(rsp, 0x20);
            assembler.pop(r14);
            assembler.ret();

            var returnZero  = assembler.CreateLabel();
            var returnValue = assembler.CreateLabel();

            assembler.Label(ref strncmp);
            assembler.test(r8, r8);
            assembler.jz(returnZero);
            assembler.sub(rcx, rdx);

            assembler.AnonymousLabel();
            assembler.movzx(eax, __byte_ptr[rcx + rdx]);
            assembler.dec(r8);
            assembler.movzx(r9d, __byte_ptr[rdx]);
            assembler.lea(rdx, __[rdx + 1]);
            assembler.cmp(al, r9b);
            assembler.jnz(returnValue);
            assembler.test(al, al);
            assembler.jz(returnZero);
            assembler.test(r8, r8);
            assembler.jnz(assembler.B);

            assembler.Label(ref returnZero);
            assembler.xor(eax, eax);
            assembler.ret();

            assembler.Label(ref returnValue);
            assembler.sub(eax, r9d);
            assembler.ret();

            var bytes = Assemble(assembler);
            var argFixFunctionAllocation = Buffers.AllocatePrivateMemory
            (
                new BufferAllocatorSettings
                {
                    MinAddress = 0,
                    MaxAddress = nuint.MaxValue,
                    Size = (uint)bytes.Length,
                    TargetProcess = targetProcess,
                }
            );
            privateAllocations.Add(argFixFunctionAllocation);
            argFixFunctionAddress = argFixFunctionAllocation.BaseAddress;
            externalMemory.WriteRaw(argFixFunctionAddress, bytes);
            Log.Information("ArgFixFunctionAddress: 0x{ArgFixFunctionAddress:X}", argFixFunctionAddress);

            if (argFixFunctionAddress == 0)
                throw new InvalidOperationException("无法分配 ArgFixFunction");

            externalMemory.ChangeProtection(argFixFunctionAddress, bytes.Length, MemoryProtection.ReadWriteExecute);
        }

        private void SetupHook(nuint sdoLoginAddress)
        {
            var assembler = new Assembler(64);
            assembler.mov(rax, argFixFunctionAddress);
            assembler.jmp(rax);

            var bytes = Assemble(assembler);
            var originalBytes = externalMemory.ReadRaw(sdoLoginAddress, bytes.Length + 0x20);

            var codeReader = new ByteArrayCodeReader(originalBytes);
            var decoder    = Decoder.Create(64, codeReader);
            decoder.IP = sdoLoginAddress;

            var endAddress = sdoLoginAddress + (nuint)originalBytes.Length;
            var bytesCount = 0;

            while (decoder.IP < endAddress)
            {
                var instruction = decoder.Decode();
                if (instruction.Code == Code.INVALID)
                    break;

                if (instruction.IP - sdoLoginAddress < (ulong)bytes.Length)
                    continue;

                bytesCount = (int)(instruction.IP - sdoLoginAddress);
                break;
            }

            if (bytesCount == 0)
                throw new InvalidOperationException("无法解析 Hook 覆盖长度");

            Log.Information("Nops Num:{NopCount}", bytesCount - bytes.Length - 1);
            assembler.nop(bytesCount                          - bytes.Length - 1);
            bytes = Assemble(assembler);
            externalMemory.WriteRaw(sdoLoginAddress, bytes);
        }

        private void GetMainModuleAddress()
        {
            mainModuleRegionSize = 0;

            for (var memoryInfo = new MemoryBasicInformation();
                 VirtualQueryEx(targetProcess.Handle, memoryInfo.BaseAddress, out memoryInfo, Marshal.SizeOf<MemoryBasicInformation>());
                 memoryInfo.BaseAddress = memoryInfo.BaseAddress + memoryInfo.RegionSize)
            {
                var fileNameBuilder = new StringBuilder(1024);
                var result          = GetMappedFileNameW(targetProcess.Handle, memoryInfo.BaseAddress, fileNameBuilder, fileNameBuilder.Capacity);
                if (result <= 0)
                    continue;

                var fileName = fileNameBuilder.ToString();
                Log.Verbose
                (
                    "Mapped File: {FileName}, Base Address: {BaseAddress:X}, AllocationBase Address: {AllocationBase:X}, Size: {RegionSize}",
                    fileName,
                    memoryInfo.BaseAddress,
                    memoryInfo.AllocationBase,
                    memoryInfo.RegionSize
                );

                if (!string.Equals(Path.GetFileName(fileName), "ffxiv_dx11.exe", StringComparison.OrdinalIgnoreCase))
                    continue;

                mainModuleBaseAddress =  (nuint)memoryInfo.AllocationBase;
                mainModuleRegionSize  += memoryInfo.RegionSize;
            }

            if (mainModuleBaseAddress == 0)
                throw new InvalidOperationException("无法定位主模块");

            Log.Verbose("AllocationBase Address: {MainModuleBaseAddress:X}, Size: {MainModuleRegionSize}", mainModuleBaseAddress, mainModuleRegionSize);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryBasicInformation
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public uint   AllocationProtect;
            public IntPtr RegionSize;
            public uint   State;
            public uint   Protect;
            public uint   Type;
        }

        [DllImport("psapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int GetMappedFileNameW(IntPtr processHandle, IntPtr baseAddress, StringBuilder fileName, int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualQueryEx(IntPtr processHandle, IntPtr address, out MemoryBasicInformation buffer, int length);
    }
}
