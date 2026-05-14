using System.Runtime.InteropServices;
using Microsoft.Win32;
using Serilog;

namespace XIVLauncher.Dalamud;

public class DalamudCompatibilityCheck : IDalamudCompatibilityCheck
{
    public void EnsureCompatibility()
    {
        if (!CheckVcRedists())
            throw new IDalamudCompatibilityCheck.NoRedistsException();

        EnsureArchitecture();
    }

    private static void EnsureArchitecture()
    {
        var arch = RuntimeInformation.ProcessArchitecture;

        switch (arch)
        {
            case Architecture.X86:
                throw new IDalamudCompatibilityCheck.ArchitectureNotSupportedException("Dalamud is not supported on x86 architecture.");

            case Architecture.X64:
                break;

            case Architecture.Arm:
                throw new IDalamudCompatibilityCheck.ArchitectureNotSupportedException("Dalamud is not supported on ARM32.");

            case Architecture.Arm64:
                throw new IDalamudCompatibilityCheck.ArchitectureNotSupportedException("x64 emulation was not detected. Please make sure to run XIVLauncher with x64 emulation.");
        }
    }

    [DllImport("kernel32", SetLastError = true)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    private static bool CheckLibrary(string fileName)
    {
        if (LoadLibrary(fileName) != IntPtr.Zero)
        {
            Log.Debug("Found " + fileName);
            return true;
        }

        Log.Error("Could not find " + fileName);
        return false;
    }

    private static bool CheckVcRedists()
    {
        var vc2022Paths = new List<string>
        {
            @"SOFTWARE\Microsoft\DevDiv\VC\Servicing\14.0\RuntimeMinimum",
            @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64",
            @"SOFTWARE\Classes\Installer\Dependencies\Microsoft.VS.VC_RuntimeMinimumVSU_amd64,v14",
            @"SOFTWARE\Classes\Installer\Dependencies\VC,redist.x64,amd64,14.38,bundle",
            @"SOFTWARE\Classes\Installer\Dependencies\VC,redist.x64,amd64,14.37,bundle",
            @"SOFTWARE\Classes\Installer\Dependencies\VC,redist.x64,amd64,14.36,bundle",
            @"SOFTWARE\Classes\Installer\Dependencies\VC,redist.x64,amd64,14.35,bundle",
            @"SOFTWARE\Classes\Installer\Dependencies\VC,redist.x64,amd64,14.34,bundle",
            @"SOFTWARE\Classes\Installer\Dependencies\VC,redist.x64,amd64,14.33,bundle",
            @"SOFTWARE\Classes\Installer\Dependencies\VC,redist.x64,amd64,14.32,bundle",
            @"SOFTWARE\Classes\Installer\Dependencies\VC,redist.x64,amd64,14.31,bundle",
            @"SOFTWARE\Classes\Installer\Dependencies\VC,redist.x64,amd64,14.30,bundle",
            @"SOFTWARE\Classes\Installer\Dependencies\VC,redist.x64,amd64,14.29,bundle",
            @"SOFTWARE\Classes\Installer\Dependencies\VC,redist.x64,amd64,14.28,bundle",
            @"Installer\Dependencies\,,amd64,14.0,bundle",
            @"SOFTWARE\Classes\Installer\Dependencies\{d992c12e-cab2-426f-bde3-fb8c53950b0d}"
        };

        var dllPaths = new List<string>
        {
            "ucrtbase_clr0400",
            "vcruntime140_clr0400",
            "vcruntime140",
            "vcruntime140_1"
        };

        var passedRegistry  = false;
        var passedDllChecks = true;

        foreach (var path in vc2022Paths)
        {
            Log.Debug("Checking Registry key: " + path);
            var vcregcheck = Registry.LocalMachine.OpenSubKey(path, false);
            if (vcregcheck == null) continue;

            var vcVersioncheck = vcregcheck.GetValue("Version") ?? "";

            if (((string)vcVersioncheck).StartsWith("14",     StringComparison.Ordinal)
                || ((string)vcVersioncheck).StartsWith("v14", StringComparison.Ordinal))
            {
                passedRegistry = true;
                Log.Debug("Passed Registry Check with: " + path);
                break;
            }
        }

        foreach (var path in dllPaths)
        {
            Log.Debug("Checking for DLL: " + path);
            passedDllChecks = passedDllChecks && CheckLibrary(path);
            if (!CheckLibrary(path))
                Log.Error("Cound not find " + path);
        }

        if (!passedRegistry)
            Log.Error("Failed all registry checks to find any Visual C++ 2015-2022 Runtimes.");

        if (!passedDllChecks)
            Log.Error("Missing DLL files required by Dalamud. Please try installing vcredist bundle again.");

        return passedRegistry && passedDllChecks;
    }
}
