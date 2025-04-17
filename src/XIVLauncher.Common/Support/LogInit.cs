using System.Collections.Generic;
using System.Text.RegularExpressions;
using CommandLine;
using Serilog;
using Serilog.Core;
using Serilog.Enrichers.Sensitive;
using Serilog.Events;

namespace XIVLauncher.Common.Support;

public static class LogInit
{
    // ReSharper disable once ClassNeverInstantiated.Local
    private class LogOptions
    {
        [Option('v', "verbose", Required = false, HelpText = "Set output to verbose messages.")]
        public bool Verbose { get; set; }

        [Option("log-file-path", Required = false, HelpText = "Set path for log file.")]
        public string? LogPath { get; set; }
    }

    public static LoggingLevelSwitch LevelSwitch;

    public static void Setup(string defaultLogPath, string[] args)
    {
        ParserResult<LogOptions> result = null;

        try
        {
            var parser = new Parser(c => { c.IgnoreUnknownArguments = true; });
            result = parser.ParseArguments<LogOptions>(args);
        }
        catch
        {
#if DEBUG
            throw;
#endif
        }

        var config = new LoggerConfiguration()
                     .WriteTo.Sink(SerilogEventSink.Instance);

        var parsed = result?.Value ?? new LogOptions();

        if (!string.IsNullOrEmpty(parsed.LogPath))
        {
            config.WriteTo.Async(a =>
            {
                a.File(parsed.LogPath);
            });
        }
        else
        {
            config.WriteTo.Async(a =>
            {
                a.File(defaultLogPath);
            });
        }

#if DEBUG
        config.WriteTo.Debug();
#endif
        //config.MinimumLevel.Verbose();
        LevelSwitch = new LoggingLevelSwitch(GetDefaultLevel());

        config.Enrich.WithSensitiveDataMasking(o =>
        {
            o.MaskingOperators = new List<IMaskingOperator>()
            {
                new SeEncryptedArgsMaskingOperator(),
                new SeTestSidMaskingOperator(),
            };
        });

        config.MinimumLevel.ControlledBy(LevelSwitch);

        if (parsed.Verbose)
            LevelSwitch.MinimumLevel = LogEventLevel.Verbose;

        Log.Logger = config.CreateLogger();
    }
    public static LogEventLevel GetDefaultLevel()
    {
        var logLevel = LogEventLevel.Information;
#if DEBUG
        logLevel = LogEventLevel.Verbose;
#endif
        return logLevel;
    }


    private class SeTestSidMaskingOperator : RegexMaskingOperator
    {
        private const string TEST_SID_PATTERN =
            "(?:DEV\\.TestSID=\\S+)|(ULS21-[a-z0-9]+)";

        public SeTestSidMaskingOperator()
            : base(TEST_SID_PATTERN, RegexOptions.IgnoreCase | RegexOptions.Compiled)
        {
        }

        protected override bool ShouldMaskInput(string input)
        {
            return input != "DEV.TestSID=0";
        }
    }

    private class SeEncryptedArgsMaskingOperator : RegexMaskingOperator
    {
        private const string ENCRYPTED_ARGS_PATTERN =
            "(?:\\/\\/\\*\\*sqex[0-9]+\\S+\\/\\/)";

        public SeEncryptedArgsMaskingOperator()
            : base(ENCRYPTED_ARGS_PATTERN, RegexOptions.IgnoreCase | RegexOptions.Compiled)
        {
        }
    }
}
