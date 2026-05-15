using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Exception = System.Exception;

namespace XIVLauncher.Common.Util;

public static class FileLockDetector
{
    private const int CCH_RM_SESSION_KEY  = RM_SESSION_KEY_LEN * 2;
    private const int CCH_RM_MAX_APP_NAME = 255;
    private const int CCH_RM_MAX_SVC_NAME = 63;
    private const int ERROR_MORE_DATA     = 234;
    private const int RM_SESSION_KEY_LEN  = 16;

    public static List<FileLockingProcess> GetLockingProcesses(IEnumerable<FileInfo> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var filePaths = files.Select(file => file.FullName).ToArray();
        if (filePaths.Length == 0)
            return [];

        var sessionKey = new StringBuilder(CCH_RM_SESSION_KEY + 1);
        ThrowOnFailure(RmStartSession(out var sessionHandle, 0, sessionKey));

        try
        {
            ThrowOnFailure
            (
                RmRegisterResources
                (
                    sessionHandle,
                    filePaths.Length,
                    filePaths,
                    0,
                    Array.Empty<RmUniqueProcess>(),
                    0,
                    Array.Empty<string>()
                )
            );

            return GetLockingProcesses(sessionHandle);
        }
        finally
        {
            RmEndSession(sessionHandle);
        }
    }

    [DllImport("rstrtmgr", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out int dwSessionHandle, int sessionFlags, StringBuilder strSessionKey);

    [DllImport("rstrtmgr")]
    private static extern int RmEndSession(int dwSessionHandle);

    [DllImport("rstrtmgr")]
    private static extern int RmGetList(int dwSessionHandle, out int nProcInfoNeeded, ref int nProcInfo, [In] [Out] RmProcessInfo[] rgAffectedApps, out uint dwRebootReasons);

    [DllImport("rstrtmgr", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources
    (
        int               dwSessionHandle,
        int               nFiles,
        string[]          rgsFileNames,
        int               nApplications,
        RmUniqueProcess[] rgApplications,
        int               nServices,
        string[]          rgsServiceNames
    );

    private static List<FileLockingProcess> GetLockingProcesses(int sessionHandle)
    {
        var count = 0;
        var infos = Array.Empty<RmProcessInfo>();
        var err   = 0;

        for (var i = 0; i < 16; i++)
        {
            err = RmGetList(sessionHandle, out var needed, ref count, infos, out _);

            switch (err)
            {
                case 0:
                    return infos.Take(count).Select(info => info.ToFileLockingProcess()).ToList();

                case ERROR_MORE_DATA:
                    infos = new RmProcessInfo[count = needed];
                    break;

                default:
                    ThrowOnFailure(err);
                    break;
            }
        }

        ThrowOnFailure(err);
        throw new InvalidOperationException();
    }

    private static void ThrowOnFailure(int err)
    {
        if (err != 0)
            throw new Win32Exception(err);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int      dwProcessId;
        public FILETIME ProcessStartTime;
    }

    private enum RmAppType
    {
        RmUnknownApp  = 0,
        RmMainWindow  = 1,
        RmOtherWindow = 2,
        RmService     = 3,
        RmExplorer    = 4,
        RmConsole     = 5,
        RmCritical    = 1000
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess UniqueProcess;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_APP_NAME + 1)]
        public string AppName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_SVC_NAME + 1)]
        public string serviceShortName;

        public RmAppType applicationType;
        public int       appStatus;
        public int       tsSessionId;

        [MarshalAs(UnmanagedType.Bool)]
        public bool restartable;

        public FileLockingProcess ToFileLockingProcess() =>
            new(UniqueProcess.dwProcessId, AppName, UniqueProcess.ProcessStartTime);
    }
}

public sealed class FileLockingProcess
(
    int      processID,
    string   appName,
    FILETIME processStartTime
)
{
    public int ProcessID { get; } = processID;

    public string AppName { get; } = appName;

    public Process? Process
    {
        get
        {
            try
            {
                var process  = Process.GetProcessById(ProcessID);
                var fileTime = process.StartTime.ToFileTime();

                if ((uint)processStartTime.dwLowDateTime != (uint)(fileTime & uint.MaxValue))
                    return null;

                if ((uint)processStartTime.dwHighDateTime != (uint)(fileTime >> 32))
                    return null;

                return process;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
