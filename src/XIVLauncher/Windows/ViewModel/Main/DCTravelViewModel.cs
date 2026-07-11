using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using XIVLauncher.DCTravel;

namespace XIVLauncher.Windows.ViewModel.Main;

public sealed partial class DCTravelViewModel : ObservableObject
{
    private readonly Action               requestBackToDashboardAction;
    private readonly Action               requestOpenHistoryAction;
    private readonly Action               requestBackToTravelAction;
    private readonly Action               requestShowProgressAction;
    private readonly Action               requestOpenReturnAction;
    private readonly Action<string>       setCurrentAreaAction;
    private readonly Action               activateAction;
    private readonly Action               onTravelCompleteAction;
    private readonly Func<DCTravelClient> getDcTravelClientFunc;

    private CancellationTokenSource? pollCts;
    private CancellationTokenSource? charactersLoadCts;

    public DCTravelViewModel
    (
        Action               requestBackToDashboardAction,
        Action               requestOpenHistoryAction,
        Action               requestBackToTravelAction,
        Action               requestShowProgressAction,
        Action               requestOpenReturnAction,
        Action<string>       setCurrentAreaAction,
        Action               activateAction,
        Action               onTravelCompleteAction,
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
        this.onTravelCompleteAction       = onTravelCompleteAction;
        this.getDcTravelClientFunc        = getDcTravelClientFunc;

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

    public ObservableCollection<DCTravelArea> SourceAreas { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCharacterVisible))]
    [NotifyPropertyChangedFor(nameof(IsCharacterEnabled))]
    [NotifyPropertyChangedFor(nameof(CharacterHint))]
    [NotifyPropertyChangedFor(nameof(IsTargetAreaVisible))]
    [NotifyPropertyChangedFor(nameof(IsTargetGroupVisible))]
    [NotifyPropertyChangedFor(nameof(CanTravelOrder))]
    [NotifyCanExecuteChangedFor(nameof(TravelOrderCommand))]
    public partial DCTravelArea? SelectedSourceArea { get; set; }

    partial void OnSelectedSourceAreaChanged(DCTravelArea? value)
    {
        charactersLoadCts?.Cancel();
        charactersLoadCts = new CancellationTokenSource();

        TargetAreas.Clear();
        TargetGroups.Clear();
        Characters.Clear();
        SelectedCharacter = null;
        _                 = LoadCharactersAsync(charactersLoadCts.Token);
    }

    public ObservableCollection<DCTravelCharacter> Characters { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTargetAreaVisible))]
    [NotifyPropertyChangedFor(nameof(IsTargetGroupVisible))]
    [NotifyPropertyChangedFor(nameof(CanTravelOrder))]
    [NotifyCanExecuteChangedFor(nameof(TravelOrderCommand))]
    public partial DCTravelCharacter? SelectedCharacter { get; set; }

    partial void OnSelectedCharacterChanged(DCTravelCharacter? value)
    {
        if (value != null && SelectedSourceArea != null)
            _ = LoadTargetAreasAsync();

        TravelOrderCommand.NotifyCanExecuteChanged();
    }

    public ObservableCollection<DCTravelArea> TargetAreas { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTargetGroupVisible))]
    [NotifyPropertyChangedFor(nameof(CanTravelOrder))]
    public partial DCTravelArea? SelectedTargetArea { get; set; }

    partial void OnSelectedTargetAreaChanged(DCTravelArea? value)
    {
        TargetGroups.Clear();
        if (value != null)
            _ = LoadTargetGroupsAsync();
    }

    public ObservableCollection<DCTravelGroup> TargetGroups { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanTravelOrder))]
    [NotifyCanExecuteChangedFor(nameof(TravelOrderCommand))]
    public partial DCTravelGroup? SelectedTargetGroup { get; set; }

    public ObservableCollection<DCTravelMigrationOrder> MigrationOrders { get; }

    public bool HasMigrationOrders   => MigrationOrders.Count > 0;
    public bool HasNoMigrationOrders => MigrationOrders.Count == 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TravelBackCommand))]
    public partial DCTravelMigrationOrder? SelectedOrder { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotLoading))]
    [NotifyPropertyChangedFor(nameof(IsCharacterEnabled))]
    [NotifyPropertyChangedFor(nameof(CharacterHint))]
    [NotifyPropertyChangedFor(nameof(CanTravelOrder))]
    [NotifyCanExecuteChangedFor(nameof(TravelOrderCommand))]
    [NotifyCanExecuteChangedFor(nameof(TravelBackCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmTravelBackCommand))]
    public partial bool IsLoading { get; set; }

    public bool IsNotLoading => !IsLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotUnderMaintenance))]
    [NotifyPropertyChangedFor(nameof(CanTravelOrder))]
    [NotifyCanExecuteChangedFor(nameof(TravelOrderCommand))]
    [NotifyCanExecuteChangedFor(nameof(TravelBackCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmTravelBackCommand))]
    public partial bool IsUnderMaintenance { get; set; }

    public bool IsNotUnderMaintenance => !IsUnderMaintenance;

    [ObservableProperty]
    public partial string MaintenanceMessage { get; set; } = string.Empty;

    public bool IsCharacterVisible   => SelectedSourceArea != null;
    public bool IsCharacterEnabled   => SelectedSourceArea != null && !IsLoading;
    public bool IsTargetAreaVisible  => SelectedCharacter  != null;
    public bool IsTargetGroupVisible => SelectedTargetArea != null;

    public string CharacterHint => IsLoading && SelectedSourceArea != null ? "(获取角色信息中)" : "选择角色";

    public bool CanTravelOrder =>
        SelectedSourceArea != null && SelectedCharacter != null && SelectedTargetArea != null && SelectedTargetGroup != null && !IsLoading && !IsUnderMaintenance;

    [ObservableProperty]
    public partial bool AutoStartGameOnComplete { get; set; } = true;

    // 超域返回页属性
    public ObservableCollection<DCTravelArea> ReturnSourceAreas { get; } = [];

    [ObservableProperty]
    public partial DCTravelArea? ReturnSelectedSourceArea { get; set; }

    partial void OnReturnSelectedSourceAreaChanged(DCTravelArea? value)
    {
        ReturnCurrentGroups.Clear();
        if (value != null)
            _ = LoadReturnCurrentGroupsAsync();
    }

    public ObservableCollection<DCTravelGroup> ReturnCurrentGroups { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmTravelBackCommand))]
    public partial DCTravelGroup? ReturnSelectedCurrentGroup { get; set; }

    [ObservableProperty]
    public partial string ReturnOrderInfo { get; set; } = string.Empty;

    private DCTravelMigrationOrder? pendingReturnOrder;
    private string?                 pendingTargetGroupName;

    // 进度页属性
    [ObservableProperty]
    public partial string TravelProgressText { get; set; } = "正在提交传送请求…";

    [ObservableProperty]
    public partial bool IsTravelInProgress { get; set; } = true;

    [RelayCommand(CanExecute = nameof(CanTravelOrderExecute))]
    private async Task TravelOrder() =>
        await StartTravelAsync();

    private bool CanTravelOrderExecute() =>
        SelectedTargetGroup != null &&
        SelectedCharacter   != null &&
        !IsLoading                  &&
        !IsUnderMaintenance;

    [RelayCommand(CanExecute = nameof(CanTravelBack))]
    private async Task TravelBack() =>
        await OpenReturnPageAsync();

    private bool CanTravelBack() =>
        SelectedOrder != null && !IsLoading && !IsUnderMaintenance;

    [RelayCommand]
    private async Task RefreshOrders() =>
        await RefreshOrdersAsync();

    [RelayCommand(CanExecute = nameof(CanConfirmTravelBack))]
    private async Task ConfirmTravelBack() =>
        await ConfirmTravelBackAsync();

    private bool CanConfirmTravelBack() =>
        ReturnSelectedCurrentGroup != null && !IsLoading && !IsUnderMaintenance;

    [RelayCommand]
    private void BackToDashboard() =>
        requestBackToDashboardAction();

    [RelayCommand]
    private void OpenHistory() =>
        requestOpenHistoryAction();

    [RelayCommand]
    private void BackToTravel() =>
        requestBackToTravelAction();

    [RelayCommand]
    private void ReturnFromProgress() =>
        CancelPollAndReturn();

    [RelayCommand]
    private void CancelReturn() =>
        requestOpenHistoryAction();

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

            if (AutoStartGameOnComplete)
                onTravelCompleteAction();
            else
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
        if (IsLoading) return;
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

    private async Task LoadCharactersAsync(CancellationToken ct)
    {
        if (SelectedSourceArea == null) return;
        IsLoading = true;

        try
        {
            var client = getDcTravelClientFunc();
            Characters.Clear();

            foreach (var g in SelectedSourceArea.GroupList)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var chars = await client.QueryRoleList(SelectedSourceArea.AreaID, g.GroupID);

                    ct.ThrowIfCancellationRequested();

                    foreach (var c in chars)
                    {
                        c.ServerName = g.GroupName;
                        Characters.Add(c);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[DCTravelVM] 加载角色失败 A={AreaID} G={GroupID}", SelectedSourceArea.AreaID, g.GroupID);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DCTravelVM] 加载角色失败");
        }
        finally
        {
            if (!ct.IsCancellationRequested)
                IsLoading = false;
        }
    }

    private async Task LoadTargetAreasAsync()
    {
        if (SelectedSourceArea == null || SelectedCharacter == null || IsLoading) return;
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
        if (SelectedTargetArea == null || IsLoading) return;
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
        if (SelectedOrder == null || IsLoading) return;

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
        if (pendingReturnOrder == null || ReturnSelectedCurrentGroup == null || IsLoading) return;

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

            if (AutoStartGameOnComplete)
                onTravelCompleteAction();
            else
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
        if (ReturnSelectedSourceArea == null || IsLoading) return;
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
            ConfirmTravelBackCommand.NotifyCanExecuteChanged();
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
}
