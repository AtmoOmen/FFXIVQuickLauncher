using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Exception = System.Exception;

namespace XIVLauncher.Common.Util;

public class RestartManager : IDisposable
{
    private const int CCH_RM_SESSION_KEY  = RM_SESSION_KEY_LEN * 2;
    private const int CCH_RM_MAX_APP_NAME = 255;
    private const int CCH_RM_MAX_SVC_NAME = 63;
    private const int ERROR_MORE_DATA     = 234;
    private const int RM_SESSION_KEY_LEN  = 16;

    private readonly int    sessionHandle;
    private readonly string sessionKey;

    public RestartManager()
    {
        var sessKey = new StringBuilder(CCH_RM_SESSION_KEY + 1);
        ThrowOnFailure(RmStartSession(out sessionHandle, 0, sessKey));
        sessionKey = sessKey.ToString();
    }

    ~RestartManager() =>
        ReleaseUnmanagedResources();

    public void Dispose()
    {
        ReleaseUnmanagedResources();
        GC.SuppressFinalize(this);
    }

    public void Register(IEnumerable<FileInfo> files = null, IEnumerable<Process> processes = null, IEnumerable<string> serviceNames = null)
    {
        var filesArray = files?.Select(f => f.FullName).ToArray() ?? Array.Empty<string>();
        var processesArray = processes?.Select
                             (f => new RmUniqueProcess
                                 {
                                     dwProcessId = f.Id,
                                     ProcessStartTime = new FILETIME
                                     {
                                         dwLowDateTime  = (int)(f.StartTime.ToFileTime() & uint.MaxValue),
                                         dwHighDateTime = (int)(f.StartTime.ToFileTime() >> 32)
                                     }
                                 }
                             ).ToArray()
                             ?? Array.Empty<RmUniqueProcess>();
        var servicesArray = serviceNames?.ToArray() ?? Array.Empty<string>();
        ThrowOnFailure
        (
            RmRegisterResources
            (
                sessionHandle,
                filesArray.Length,
                filesArray,
                processesArray.Length,
                processesArray,
                servicesArray.Length,
                servicesArray
            )
        );
    }

    public void Shutdown(bool forceShutdown = true, bool shutdownOnlyRegistered = false, RmWriteStatusCallback cb = null) =>
        ThrowOnFailure(RmShutdown(sessionHandle, (forceShutdown ? RmShutdownType.RmForceShutdown : 0) | (shutdownOnlyRegistered ? RmShutdownType.RmShutdownOnlyRegistered : 0), cb));

    public void Restart(RmWriteStatusCallback cb = null) =>
        ThrowOnFailure(RmRestart(sessionHandle, 0, cb));

    public List<RmProcessInfo> GetInterferingProcesses(out RmRebootReason rebootReason)
    {
        var count = 0;
        var infos = new RmProcessInfo[count];
        var err   = 0;

        for (var i = 0; i < 16; i++)
        {
            err = RmGetList(sessionHandle, out var needed, ref count, infos, out rebootReason);

            switch (err)
            {
                case 0:
                    return infos.Take(count).ToList();

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

    [DllImport("rstrtmgr", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out int dwSessionHandle, int sessionFlags, StringBuilder strSessionKey);

    [DllImport("rstrtmgr")]
    private static extern int RmEndSession(int dwSessionHandle);

    [DllImport("rstrtmgr")]
    private static extern int RmShutdown(int dwSessionHandle, RmShutdownType lAtionFlags, RmWriteStatusCallback fnStatus);

    [DllImport("rstrtmgr")]
    private static extern int RmRestart(int dwSessionHandle, int dwRestartFlags, RmWriteStatusCallback fnStatus);

    [DllImport("rstrtmgr")]
    private static extern int RmGetList(int dwSessionHandle, out int nProcInfoNeeded, ref int nProcInfo, [In] [Out] RmProcessInfo[] rgAffectedApps, out RmRebootReason dwRebootReasons);

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

    private void ReleaseUnmanagedResources() =>
        ThrowOnFailure(RmEndSession(sessionHandle));

    private void ThrowOnFailure(int err)
    {
        if (err != 0)
            throw new Win32Exception(err);
    }

    public delegate void RmWriteStatusCallback(uint percentageCompleted);

    [StructLayout(LayoutKind.Sequential)]
    public struct RmUniqueProcess
    {
        public int      dwProcessId;
        public FILETIME ProcessStartTime;
    }

    public enum RmAppType
    {
        RmUnknownApp = 0,
        RmMainWindow = 1,
        RmOtherWindow = 2,
        RmService = 3,
        RmExplorer = 4,
        RmConsole = 5,
        RmCritical = 1000
    }

    [Flags]
    public enum RmRebootReason
    {
        RmRebootReasonNone = 0x0,
        RmRebootReasonPermissionDenied = 0x1,
        RmRebootReasonSessionMismatch = 0x2,
        RmRebootReasonCriticalProcess = 0x4,
        RmRebootReasonCriticalService = 0x8,
        RmRebootReasonDetectedSelf = 0x10
    }

    [Flags]
    private enum RmShutdownType
    {
        RmForceShutdown          = 0x1,
        RmShutdownOnlyRegistered = 0x10
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct RmProcessInfo
    {
        public RmUniqueProcess UniqueProcess;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_APP_NAME + 1)]
        public string AppName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_SVC_NAME + 1)]
        public string ServiceShortName;

        public RmAppType ApplicationType;
        public int       AppStatus;
        public int       TSSessionId;

        [MarshalAs(UnmanagedType.Bool)]
        public bool bRestartable;

        public Process Process
        {
            get
            {
                try
                {
                    var process  = Process.GetProcessById(UniqueProcess.dwProcessId);
                    var fileTime = process.StartTime.ToFileTime();

                    if ((uint)UniqueProcess.ProcessStartTime.dwLowDateTime != (uint)(fileTime & uint.MaxValue))
                        return null;

                    if ((uint)UniqueProcess.ProcessStartTime.dwHighDateTime != (uint)(fileTime >> 32))
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
}
