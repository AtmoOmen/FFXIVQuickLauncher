using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Serilog;
using XIVLauncher.Startup.Steps;

namespace XIVLauncher.Startup;

public class StartupOrchestrator
{
    private readonly List<IStartupStep> steps = [];
    private readonly StartupContext     context;

    public StartupOrchestrator(Application application, Dispatcher dispatcher)
    {
        context = new StartupContext
        {
            Dispatcher = dispatcher
        };

        RegisterSteps(application);
    }

    private void RegisterSteps(Application application)
    {
        var commandLineStep = new CommandLineStep();
        var updateCheckStep = new UpdateCheckStep(commandLineStep);

        steps.Add(new RenderModeStep());
        steps.Add(new LoggingStep());
        steps.Add(commandLineStep);
        steps.Add(new SettingsStep(commandLineStep));
        steps.Add(new LocalizationStep());
        steps.Add(new VelopackStep());
        steps.Add(updateCheckStep);
        steps.Add(new ThemeOverrideStep(application));
        steps.Add(new DalamudInitStep(updateCheckStep));

        steps.Sort((a, b) => a.Order.CompareTo(b.Order));
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        Log.Information("开始启动流程, 共 {Count} 个步骤", steps.Count);

        foreach (var step in steps)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                Log.Verbose("执行启动步骤: {Name} (Order: {Order})", step.Name, step.Order);
                await step.ExecuteAsync(context, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "启动步骤 {Name} 执行失败", step.Name);
                throw;
            }
        }

        Log.Information("启动流程完成");
    }

    public StartupContext GetContext() => context;
}
