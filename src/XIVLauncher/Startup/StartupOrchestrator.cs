using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Serilog;
using XIVLauncher.Startup.Steps;

namespace XIVLauncher.Startup;

public class StartupOrchestrator
{
    private readonly List<IStartupStep> bootstrapSteps = [];
    private readonly List<IStartupStep> normalSteps    = [];
    private readonly StartupContext     context;

    public StartupOrchestrator(Dispatcher dispatcher)
    {
        context = new StartupContext
        {
            Dispatcher = dispatcher
        };

        RegisterSteps();
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await ExecuteBootstrapStepsAsync(cancellationToken);

        Log.Information("开始启动流程, 共 {Count} 个正常步骤", normalSteps.Count);

        foreach (var step in normalSteps)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                Log.Information("执行启动步骤: {Name} (Order: {Order})", step.Name, step.Order);
                await step.ExecuteAsync(context, cancellationToken);
                ValidateStepResult(step);
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

    private void RegisterSteps()
    {
        var commandLineStep = new CommandLineStep();
        var updateCheckStep = new UpdateCheckStep(commandLineStep);

        bootstrapSteps.Add(new LoggingStep());
        bootstrapSteps.Add(new RenderModeStep());
        bootstrapSteps.Add(commandLineStep);

        normalSteps.Add(new SettingsStep(commandLineStep));
        normalSteps.Add(new VelopackStep());
        normalSteps.Add(updateCheckStep);
        normalSteps.Add(new DalamudInitStep(updateCheckStep));

        bootstrapSteps.Sort((a, b) => a.Order.CompareTo(b.Order));
        normalSteps.Sort((a,    b) => a.Order.CompareTo(b.Order));
    }

    private async Task ExecuteBootstrapStepsAsync(CancellationToken cancellationToken)
    {
        foreach (var step in bootstrapSteps)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await step.ExecuteAsync(context, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"预初始化步骤执行失败: {step.Name}", ex);
            }
        }
    }

    private void ValidateStepResult(IStartupStep step)
    {
        if (step is SettingsStep && context.Settings == null)
            throw new InvalidOperationException("设置初始化完成后 StartupContext.Settings 仍为空");
    }
}
