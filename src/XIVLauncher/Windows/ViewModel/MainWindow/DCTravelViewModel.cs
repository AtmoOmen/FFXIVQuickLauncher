using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Serilog;
using XIVLauncher.DCTravel;
using XIVLauncher.Xaml;

namespace XIVLauncher.Windows.ViewModel.MainWindow;

public sealed class DCTravelViewModel : INotifyPropertyChanged
{
    private readonly Action               requestBackToDashboardAction;
    private readonly Action               requestOpenHistoryAction;
    private readonly Action               requestBackToTravelAction;
    private readonly Action               requestShowProgressAction;
    private readonly Action               requestOpenReturnAction;
    private readonly Action<string>       setCurrentAreaAction;
    private readonly Action               activateAction;
    private readonly Func<DCTravelClient> getDcTravelClientFunc;

    private readonly AsyncCommand travelOrderCommand;
    private readonly AsyncCommand travelBackCommand;
    private readonly AsyncCommand refreshOrdersCommand;
    private readonly AsyncCommand confirmTravelBackCommand;
    private readonly SyncCommand  backToDashboardCommand;
    private readonly SyncCommand  openHistoryCommand;
    private readonly SyncCommand  backToTravelCommand;
    private readonly SyncCommand  returnFromProgressCommand;
    private readonly SyncCommand  cancelReturnCommand;

    private bool                     isLoading;
    private bool                     isUnderMaintenance;
    private string                   maintenanceMessage = string.Empty;
    private CancellationTokenSource? pollCts;

    public DCTravelViewModel
    (
        Action               requestBackToDashboardAction,
        Action               requestOpenHistoryAction,
        Action               requestBackToTravelAction,
        Action               requestShowProgressAction,
        Action               requestOpenReturnAction,
        Action<string>       setCurrentAreaAction,
        Action               activateAction,
        Func<DCTravelClient> getDcTravelClientFunc
    )
    {
        this.requestBackToDashboardAction = requestBackToDashboardAction;
        this.requestOpenHistoryAction     = requestOpenHistoryAction;
        this.requestBackToTravelAction    = requestBackToTravelAction;
        this.requestShowProgressAction    = requestShowProgressAction;
        this.requestOpenReturnAction      = requestOpenReturnAction;
        this.setCurrentAreaAction         = setCurrentAreaAction;
        this.activateAction               = activateAction;
        this.getDcTravelClientFunc        = getDcTravelClientFunc;

        travelOrderCommand = new AsyncCommand
            (async _ => await StartTravelAsync(), () => SelectedTargetGroup != null && SelectedCharacter != null && !isLoading && !isUnderMaintenance);
        travelBackCommand    = new AsyncCommand(async _ => await OpenReturnPageAsync(), () => SelectedOrder != null && !isLoading && !isUnderMaintenance);
        refreshOrdersCommand = new AsyncCommand(async _ => await RefreshOrdersAsync());
        confirmTravelBackCommand = new AsyncCommand
            (async _ => await ConfirmTravelBackAsync(), () => ReturnSelectedCurrentGroup != null && !isLoading && !isUnderMaintenance);
        backToDashboardCommand    = new SyncCommand(_ => this.requestBackToDashboardAction());
        openHistoryCommand        = new SyncCommand(_ => this.requestOpenHistoryAction());
        backToTravelCommand       = new SyncCommand(_ => this.requestBackToTravelAction());
        returnFromProgressCommand = new SyncCommand(_ => CancelPollAndReturn());
        cancelReturnCommand       = new SyncCommand(_ => this.requestOpenHistoryAction());

        SourceAreas     = [];
        TargetAreas     = [];
        TargetGroups    = [];
        Characters      = [];
        MigrationOrders = [];

        MigrationOrders.CollectionChanged += (s, e) =>
        {
            OnPropertyChanged(nameof(HasMigrationOrders));
            OnPropertyChanged(nameof(HasNoMigrationOrders));
        };
    }

    public ICommand TravelOrderCommand        => travelOrderCommand;
    public ICommand TravelBackCommand         => travelBackCommand;
    public ICommand RefreshOrdersCommand      => refreshOrdersCommand;
    public ICommand BackToDashboardCommand    => backToDashboardCommand;
    public ICommand OpenHistoryCommand        => openHistoryCommand;
    public ICommand BackToTravelCommand       => backToTravelCommand;
    public ICommand ReturnFromProgressCommand => returnFromProgressCommand;
    public ICommand ConfirmTravelBackCommand  => confirmTravelBackCommand;
    public ICommand CancelReturnCommand       => cancelReturnCommand;

    public ObservableCollection<DCTravelArea> SourceAreas { get; }

    public DCTravelArea? SelectedSourceArea
    {
        get;
        set
        {
            if (!SetProperty(ref field, value))
                return;

            TargetAreas.Clear();
            TargetGroups.Clear();
            Characters.Clear();
            OnPropertyChanged(nameof(IsCharacterVisible));
            OnPropertyChanged(nameof(IsCharacterEnabled));
            OnPropertyChanged(nameof(CharacterHint));
            OnPropertyChanged(nameof(IsTargetAreaVisible));
            OnPropertyChanged(nameof(IsTargetGroupVisible));
            OnPropertyChanged(nameof(CanTravelOrder));
            _ = LoadCharactersAsync();
        }
    }

    public ObservableCollection<DCTravelCharacter> Characters { get; }

    public DCTravelCharacter? SelectedCharacter
    {
        get;
        set
        {
            if (!SetProperty(ref field, value))
                return;

            OnPropertyChanged(nameof(IsTargetAreaVisible));
            OnPropertyChanged(nameof(IsTargetGroupVisible));
            OnPropertyChanged(nameof(CanTravelOrder));

            if (value != null && SelectedSourceArea != null)
                _ = LoadTargetAreasAsync();

            travelOrderCommand.RaiseCanExecuteChanged();
        }
    }

    public ObservableCollection<DCTravelArea> TargetAreas { get; }

    public DCTravelArea? SelectedTargetArea
    {
        get;
        set
        {
            if (!SetProperty(ref field, value))
                return;

            TargetGroups.Clear();
            OnPropertyChanged(nameof(IsTargetGroupVisible));
            OnPropertyChanged(nameof(CanTravelOrder));
            if (value != null)
                _ = LoadTargetGroupsAsync();
        }
    }

    public ObservableCollection<DCTravelGroup> TargetGroups { get; }

    public DCTravelGroup? SelectedTargetGroup
    {
        get;
        set
        {
            if (!SetProperty(ref field, value))
                return;

            OnPropertyChanged(nameof(CanTravelOrder));
            travelOrderCommand.RaiseCanExecuteChanged();
        }
    }

    public ObservableCollection<DCTravelMigrationOrder> MigrationOrders { get; }

    public bool HasMigrationOrders   => MigrationOrders.Count > 0;
    public bool HasNoMigrationOrders => MigrationOrders.Count == 0;

    public DCTravelMigrationOrder? SelectedOrder
    {
        get;
        set
        {
            if (!SetProperty(ref field, value))
                return;

            travelBackCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsLoading
    {
        get => isLoading;
        set
        {
            if (!SetProperty(ref isLoading, value))
                return;

            OnPropertyChanged(nameof(IsNotLoading));
            OnPropertyChanged(nameof(IsCharacterEnabled));
            OnPropertyChanged(nameof(CharacterHint));
            OnPropertyChanged(nameof(CanTravelOrder));
            travelOrderCommand.RaiseCanExecuteChanged();
            travelBackCommand.RaiseCanExecuteChanged();
            confirmTravelBackCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsNotLoading => !isLoading;

    public bool IsUnderMaintenance
    {
        get => isUnderMaintenance;
        set
        {
            if (!SetProperty(ref isUnderMaintenance, value))
                return;

            OnPropertyChanged(nameof(IsNotUnderMaintenance));
            OnPropertyChanged(nameof(CanTravelOrder));
            travelOrderCommand.RaiseCanExecuteChanged();
            travelBackCommand.RaiseCanExecuteChanged();
            confirmTravelBackCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsNotUnderMaintenance => !isUnderMaintenance;

    public string MaintenanceMessage
    {
        get => maintenanceMessage;
        set => SetProperty(ref maintenanceMessage, value);
    }

    public bool IsCharacterVisible   => SelectedSourceArea != null;
    public bool IsCharacterEnabled   => SelectedSourceArea != null && !isLoading;
    public bool IsTargetAreaVisible  => SelectedCharacter  != null;
    public bool IsTargetGroupVisible => SelectedTargetArea != null;

    public string CharacterHint => isLoading && SelectedSourceArea != null ? "(获取角色信息中)" : "选择角色";

    public bool CanTravelOrder =>
        SelectedSourceArea != null && SelectedCharacter != null && SelectedTargetArea != null && SelectedTargetGroup != null && !isLoading && !isUnderMaintenance;

    // 超域返回页属性
    public ObservableCollection<DCTravelArea> ReturnSourceAreas { get; } = [];

    public DCTravelArea? ReturnSelectedSourceArea
    {
        get;
        set
        {
            if (!SetProperty(ref field, value))
                return;

            ReturnCurrentGroups.Clear();
            if (value != null)
                _ = LoadReturnCurrentGroupsAsync();
        }
    }

    public ObservableCollection<DCTravelGroup> ReturnCurrentGroups { get; } = [];

    public DCTravelGroup? ReturnSelectedCurrentGroup
    {
        get;
        set
        {
            if (!SetProperty(ref field, value))
                return;

            confirmTravelBackCommand.RaiseCanExecuteChanged();
        }
    }

    public string ReturnOrderInfo
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    private DCTravelMigrationOrder? pendingReturnOrder;
    private string?                 pendingTargetGroupName;

    // 进度页属性
    public string TravelProgressText
    {
        get;
        set => SetProperty(ref field, value);
    } = "正在提交传送请求…";

    public bool IsTravelInProgress
    {
        get;
        set => SetProperty(ref field, value);
    } = true;

    public async Task InitializeAsync(string? currentAreaName = null)
    {
        await RefreshTravelDataAsync();

        // 首次打开时预填充当前账号所在大区，触发后续角色与目标列表加载
        if (SelectedSourceArea == null && !string.IsNullOrWhiteSpace(currentAreaName))
            SelectedSourceArea = SourceAreas.FirstOrDefault(a => string.Equals(a.AreaName, currentAreaName, StringComparison.Ordinal));

        await RefreshOrdersAsync();
    }

    private async Task StartTravelAsync()
    {
        if (SelectedSourceArea == null || SelectedTargetGroup == null || SelectedCharacter == null)
            return;

        pollCts?.Cancel();
        pollCts = new CancellationTokenSource();

        IsTravelInProgress = true;
        TravelProgressText = "正在提交传送请求…";
        requestShowProgressAction();

        try
        {
            var            client      = getDcTravelClientFunc();
            DCTravelGroup? sourceGroup = null;

            foreach (var g in SelectedSourceArea.GroupList)
            {
                if (g.AreaID == SelectedCharacter.AreaID && g.GroupID == SelectedCharacter.GroupID)
                {
                    sourceGroup = g;
                    break;
                }
            }

            sourceGroup ??= SelectedSourceArea.GroupList.FirstOrDefault();

            if (sourceGroup == null)
            {
                TravelProgressText = "无法确定源服务器";
                IsTravelInProgress = false;
                return;
            }

            var targetGroup = SelectedTargetGroup;
            var orderId     = await client.TravelOrder(targetGroup, sourceGroup, SelectedCharacter);

            await PollOrderStatusAsync(orderId, pollCts.Token);

            if (SelectedTargetArea != null)
                UpdateCurrentArea(SelectedTargetArea.AreaName);

            activateAction();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "[DCTravelVM] 传送失败");
            TravelProgressText = $"传送失败: {ex.Message}";
            IsTravelInProgress = false;
        }
    }

    private async Task PollOrderStatusAsync(string orderId, CancellationToken ct)
    {
        var client = getDcTravelClientFunc();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var status = await client.QueryOrderStatus(orderId);

                TravelProgressText = status.Status switch
                {
                    DCTravelStatusType.Checking or DCTravelStatusType.CheckingAlt     => "检查目标大区角色信息中…",
                    DCTravelStatusType.NeedConfirmation                               => "等待确认传送…",
                    DCTravelStatusType.Processing or DCTravelStatusType.ProcessingAlt => "超域传送排队中…",
                    DCTravelStatusType.Success                                        => "超域传送完成",
                    DCTravelStatusType.TravelFailed or DCTravelStatusType.PreCheckFailed =>
                        $"传送失败: {status.CheckMessage} {status.MigrationMessage}".Trim(),
                    _ => "未知状态"
                };

                if (status.Status is DCTravelStatusType.TravelFailed or DCTravelStatusType.PreCheckFailed)
                {
                    IsTravelInProgress = false;
                    return;
                }

                if (status.Status == DCTravelStatusType.Success)
                {
                    IsTravelInProgress = false;
                    await RefreshOrdersAsync();
                    return;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warning(ex, "[DCTravelVM] 查询订单状态失败");
                TravelProgressText = $"状态查询异常: {ex.Message}";
            }

            try
            {
                await Task.Delay(1000, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void UpdateCurrentArea(string areaName) =>
        setCurrentAreaAction(areaName);

    private void CancelPollAndReturn()
    {
        pollCts?.Cancel();
        requestBackToDashboardAction();
    }

    private async Task RefreshTravelDataAsync()
    {
        if (isLoading) return;
        IsLoading = true;

        try
        {
            var client = getDcTravelClientFunc();
            var areas  = await client.QueryGroupListTravelSource();

            SourceAreas.Clear();
            foreach (var a in areas)
                SourceAreas.Add(a);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DCTravelVM] 加载源大区失败");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadCharactersAsync()
    {
        if (SelectedSourceArea == null || isLoading) return;
        IsLoading = true;

        try
        {
            var client = getDcTravelClientFunc();
            Characters.Clear();

            foreach (var g in SelectedSourceArea.GroupList)
            {
                try
                {
                    var chars = await client.QueryRoleList(SelectedSourceArea.AreaID, g.GroupID);

                    foreach (var c in chars)
                    {
                        c.ServerName = g.GroupName;
                        Characters.Add(c);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[DCTravelVM] 加载角色失败 A={AreaID} G={GroupID}", SelectedSourceArea.AreaID, g.GroupID);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DCTravelVM] 加载角色失败");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadTargetAreasAsync()
    {
        if (SelectedSourceArea == null || SelectedCharacter == null || isLoading) return;
        IsLoading = true;

        try
        {
            var client = getDcTravelClientFunc();
            TargetAreas.Clear();

            foreach (var g in SelectedSourceArea.GroupList)
            {
                if (g.AreaID != SelectedCharacter.AreaID || g.GroupID != SelectedCharacter.GroupID)
                    continue;

                try
                {
                    var targets = await client.QueryGroupListTravelTarget(SelectedSourceArea.AreaID, g.GroupID);
                    foreach (var a in targets) TargetAreas.Add(a);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[DCTravelVM] 加载目标大区失败");
                }

                break;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DCTravelVM] 加载目标大区失败");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadTargetGroupsAsync()
    {
        if (SelectedTargetArea == null || isLoading) return;
        IsLoading = true;

        try
        {
            await Task.Delay(1);

            TargetGroups.Clear();

            foreach (var g in SelectedTargetArea.GroupList)
                TargetGroups.Add(g);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DCTravelVM] 加载目标服务器失败");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task OpenReturnPageAsync()
    {
        if (SelectedOrder == null || isLoading) return;

        await Task.Delay(1);

        pendingReturnOrder     = SelectedOrder;
        pendingTargetGroupName = pendingReturnOrder.TargetGroupName;

        ReturnOrderInfo = $"{pendingReturnOrder.GroupName}  |  {pendingReturnOrder.CreateTime}";

        ReturnSourceAreas.Clear();
        foreach (var a in SourceAreas)
            ReturnSourceAreas.Add(a);

        var targetArea = ReturnSourceAreas.FirstOrDefault(a => a.AreaName == pendingReturnOrder.TargetAreaName);
        ReturnSelectedSourceArea = targetArea;

        requestOpenReturnAction();
    }

    private async Task ConfirmTravelBackAsync()
    {
        if (pendingReturnOrder == null || ReturnSelectedCurrentGroup == null || isLoading) return;

        var group = ReturnSelectedCurrentGroup;

        pollCts?.Cancel();
        pollCts = new CancellationTokenSource();

        IsTravelInProgress = true;
        TravelProgressText = "正在提交超域返回请求…";
        requestShowProgressAction();

        try
        {
            var client  = getDcTravelClientFunc();
            var orderId = await client.TravelBack(pendingReturnOrder.OrderID, group.GroupID, group.GroupCode, group.GroupName);

            await PollOrderStatusAsync(orderId, pollCts.Token);

            await RefreshOrdersAsync();

            if (pendingReturnOrder != null && !string.IsNullOrWhiteSpace(pendingReturnOrder.SourceAreaName))
                UpdateCurrentArea(pendingReturnOrder.SourceAreaName);

            activateAction();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "[DCTravelVM] 超域返回失败");
            TravelProgressText = $"超域返回失败: {ex.Message}";
            IsTravelInProgress = false;
        }
    }

    private async Task LoadReturnCurrentGroupsAsync()
    {
        if (ReturnSelectedSourceArea == null || isLoading) return;
        IsLoading = true;

        try
        {
            await Task.Delay(1);

            ReturnCurrentGroups.Clear();

            foreach (var g in ReturnSelectedSourceArea.GroupList)
                ReturnCurrentGroups.Add(g);

            if (!string.IsNullOrEmpty(pendingTargetGroupName))
            {
                var targetGroupName = pendingTargetGroupName;
                pendingTargetGroupName = null;

                // 延迟 50ms 释放 UI 线程，以确保 ComboBox 已经在 UI 上认领并刷新了 ItemsSource 数据源，避免选中项被强行重设为 null
                await Task.Delay(50);

                var targetGroup = ReturnCurrentGroups.FirstOrDefault(g => g.GroupName == targetGroupName);
                if (targetGroup != null)
                    ReturnSelectedCurrentGroup = targetGroup;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DCTravelVM] 加载返回服务器列表失败");
        }
        finally
        {
            IsLoading = false;
            confirmTravelBackCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task RefreshOrdersAsync()
    {
        try
        {
            var client = getDcTravelClientFunc();
            var result = await client.QueryMigrationOrders();

            MigrationOrders.Clear();
            var addedRoles = new HashSet<string>();

            foreach (var o in result.Orders)
            {
                // 源大区与服务器直接采用订单响应自带字段，与目标侧保持一致，避免按 groupId 反查命中错误服务器
                if (!string.IsNullOrEmpty(o.ContentID) && addedRoles.Add(o.ContentID))
                    MigrationOrders.Add(o);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[DCTravelVM] 加载历史订单失败");
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
