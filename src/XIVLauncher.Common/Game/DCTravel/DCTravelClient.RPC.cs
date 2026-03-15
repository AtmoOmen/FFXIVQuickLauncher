using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Serilog;
using XIVLauncher.Common.Game.Exceptions;
using XIVLauncher.Common.Http;

namespace XIVLauncher.Common.Game.DCTravel;

public partial class DCTravelClient
{
    private const string APP_ID = "100001900";

    [HttpRpc]
    public async Task<string> RefreshGameSessionId()
    {
        ArgumentNullException.ThrowIfNull(RefreshGameSessionByGuidFunc);

        try
        {
            var newSid = await RefreshGameSessionByGuidFunc();
            return newSid;
        }
        catch (LoginException ex)
        {
            if (ex.ErrorCode == (int)LoginExceptionCode.OutdatedLoginInfo)
            {
                if (RefreshGameSessionIDByAutoLoginFunc != null)
                    return await RefreshGameSessionIDByAutoLoginFunc();

                throw new Exception("登录过期且未开启自动登录, 请重新使用 XIVLauncher 登录游戏");
            }

            throw;
        }
    }

    [HttpRpc]
    public void SetSdoArea(string name) =>
        SetSdoAreaFunc?.Invoke(name);

    [HttpRpc]
    public async Task<List<DCTravelArea>> QueryGroupListTravelSource()
    {
        // API: /api/orderserivce/queryGroupListTravelSource?appId=100001900
        var data = await GetRequestData
                   (
                       "api/orderserivce/queryGroupListTravelSource",
                       DCTravelAPIType.Travel,
                       new Dictionary<string, string> { ["appId"] = APP_ID }
                   );
        EnsureResultCode(data, "QueryGroupListTravelSource");
        var areaList = DeserializeList<DCTravelArea>(data, "groupList", "QueryGroupListTravelSource");
        foreach (var item in areaList)
            item.SetAreaForGroup();
        return areaList;
    }

    [HttpRpc]
    public async Task<List<DCTravelArea>> QueryGroupListTravelTarget(int areaId, int groupId)
    {
        // API: /api/orderserivce/queryGroupListTravelTarget?appId=100001900&areaId={areaId}&groupId={groupId}
        var data = await GetRequestData
                   (
                       "api/orderserivce/queryGroupListTravelTarget",
                       DCTravelAPIType.Travel,
                       new Dictionary<string, string>
                       {
                           ["appId"]   = APP_ID,
                           ["areaId"]  = areaId.ToString(),
                           ["groupId"] = groupId.ToString()
                       }
                   );
        EnsureResultCode(data, "QueryGroupListTravelTarget");
        var areaList = DeserializeList<DCTravelArea>(data, "groupList", "QueryGroupListTravelTarget");
        foreach (var item in areaList)
            item.SetAreaForGroup();
        return areaList;
    }

    [HttpRpc]
    public async Task<List<DCTravelCharacter>> QueryRoleList(int areaId, int groupId)
    {
        // API: /api/gmallgateway/queryRoleList4Migration?appId=100001900&areaId={areaId}&groupId={groupId}
        var data = await GetRequestData
                   (
                       "api/gmallgateway/queryRoleList4Migration",
                       DCTravelAPIType.Travel,
                       new Dictionary<string, string>
                       {
                           ["appId"]   = APP_ID,
                           ["areaId"]  = areaId.ToString(),
                           ["groupId"] = groupId.ToString()
                       }
                   );
        EnsureResultCode(data, "QueryRoleList");
        var characterList = DeserializeList<DCTravelCharacter>(data, "roleList", "QueryRoleList");

        foreach (var character in characterList)
        {
            character.AreaID  = areaId;
            character.GroupID = groupId;
        }

        return characterList;
    }

    [HttpRpc]
    public Task<int> QueryTravelQueueTime(int areaId, int groupId) =>
        // API 已下线，保留方法签名以维持 RPC 协议兼容。
        Task.FromResult(0);

    [HttpRpc]
    public async Task<string> TravelOrder(DCTravelGroup targetGroup, DCTravelGroup sourceGroup, DCTravelCharacter character)
    {
        // API: /api/orderserivce/travelOrder
        ArgumentNullException.ThrowIfNull(targetGroup);
        ArgumentNullException.ThrowIfNull(sourceGroup);
        ArgumentNullException.ThrowIfNull(character);

        var data = await GetRequestData
                   (
                       "api/orderserivce/travelOrder",
                       DCTravelAPIType.Travel,
                       new Dictionary<string, string>
                       {
                           ["appId"]            = APP_ID,
                           ["migrationType"]    = "4",
                           ["isMigrationTimes"] = "1",
                           ["productId"]        = "1",
                           ["areaId"]           = sourceGroup.AreaID.ToString(),
                           ["areaName"]         = sourceGroup.AreaName,
                           ["groupId"]          = sourceGroup.GroupID.ToString(),
                           ["groupCode"]        = sourceGroup.GroupCode,
                           ["groupName"]        = sourceGroup.GroupName,
                           ["targetArea"]       = targetGroup.AreaID.ToString(),
                           ["targetAreaName"]   = targetGroup.AreaName,
                           ["targetGroupId"]    = targetGroup.GroupID.ToString(),
                           ["targetGroupCode"]  = targetGroup.GroupCode,
                           ["targetGroupName"]  = targetGroup.GroupName,
                           ["roleList"]         = $"[{character.ToQueryString()}]"
                       }
                   );
        EnsureResultCode(data, "TravelOrder");
        return GetRequiredString(data, "orderId", "TravelOrder");
    }

    [HttpRpc]
    public async Task<DCTravelOrderInfo> QueryOrderStatus(string orderId)
    {
        // API: /api/gmallgateway/queryOrderStatus?orderId={orderId}
        ArgumentException.ThrowIfNullOrWhiteSpace(orderId);
        var data = await GetRequestData
                   (
                       "api/gmallgateway/queryOrderStatus",
                       DCTravelAPIType.Travel,
                       new Dictionary<string, string> { ["orderId"] = orderId }
                   );
        var     migrationStatus  = GetRequiredInt(data, "migrationStatus", "QueryOrderStatus");
        var     messageStr       = data["migrationMsg"]?.GetValue<string>();
        string? checkMessage     = null;
        string? migrationMessage = null;

        if (!string.IsNullOrWhiteSpace(messageStr) && JsonNode.Parse(messageStr) is JsonArray { Count: > 0 } parsed && parsed[0] is JsonObject messageItem)
        {
            checkMessage     = messageItem["checkMsg"]?.GetValue<string>();
            migrationMessage = messageItem["migrationMsg"]?.GetValue<string>();
        }

        return new DCTravelOrderInfo
        {
            Status           = (DCTravelStatusType)migrationStatus,
            CheckMessage     = checkMessage     ?? string.Empty,
            MigrationMessage = migrationMessage ?? string.Empty
        };
    }

    [HttpRpc]
    public async Task<bool> InitOrderPage()
    {
        // API: /api/orderserivce/pageInit?migrationType=0
        try
        {
            _ = await GetRequestData("api/orderserivce/pageInit", DCTravelAPIType.Order, new Dictionary<string, string> { { "migrationType", "0" } });
            return true;
        }
        catch (DCTravelAPIException ex)
        {
            Log.Error(ex, "Failed to initialize order page");
            return false;
        }
    }

    [HttpRpc]
    public async Task<DCTravelMigrationOrders> QueryMigrationOrders(int pageIndex = 1)
    {
        // API: /api/orderserivce/queryMigrationOrders?appId=100001900&pageIndex={pageIndex}&pageNum=10
        var data = await GetRequestData
                   (
                       "api/orderserivce/queryMigrationOrders",
                       DCTravelAPIType.Order,
                       new Dictionary<string, string>
                       {
                           ["appId"]     = APP_ID,
                           ["pageIndex"] = pageIndex.ToString(),
                           ["pageNum"]   = "10"
                       }
                   );
        EnsureResultCode(data, "QueryMigrationOrders");
        var orderList      = new List<DCTravelMigrationOrder>();
        var orderListRaw   = GetRequiredString(data, "orderlist", "QueryMigrationOrders");
        var orderListArray = JsonNode.Parse(orderListRaw) as JsonArray ?? [];

        foreach (var order in orderListArray)
        {
            if (order is not JsonObject orderObject)
                continue;

            if (orderObject["migrationDetailList"] is not JsonArray { Count: > 0 } migrationDetailList || migrationDetailList[0] is not JsonObject migrationDetail)
                continue;

            if (!int.TryParse(orderObject["migrationStatus"]?.ToString(), out var migrationStatus))
                continue;
            if (!int.TryParse(orderObject["migrationType"]?.ToString(), out var migrationType))
                continue;
            if (!int.TryParse(orderObject["travelStatus"]?.ToString(), out var travelStatus))
                continue;

            if (migrationType != 4 || travelStatus != 1 || migrationStatus != 5)
                continue;

            if (!int.TryParse(orderObject["groupId"]?.ToString(), out var groupId))
                continue;

            var orderId    = orderObject["orderId"]?.GetValue<string>();
            var groupCode  = orderObject["groupCode"]?.GetValue<string>();
            var groupName  = orderObject["groupName"]?.GetValue<string>();
            var roleId     = migrationDetail["roleId"]?.GetValue<string>();
            var createTime = orderObject["createTime"]?.GetValue<string>();

            if (string.IsNullOrWhiteSpace
                    (orderId)
                || string.IsNullOrWhiteSpace(groupCode)
                || string.IsNullOrWhiteSpace(groupName)
                || string.IsNullOrWhiteSpace(roleId)
                || string.IsNullOrWhiteSpace(createTime))
                continue;

            orderList.Add
            (
                new DCTravelMigrationOrder
                {
                    OrderID    = orderId,
                    ContentID  = roleId,
                    GroupID    = groupId,
                    GroupCode  = groupCode,
                    GroupName  = groupName,
                    CreateTime = createTime
                }
            );
        }

        return new DCTravelMigrationOrders
        {
            Orders       = orderList.ToArray(),
            TotalCount   = GetRequiredInt(data, "totalCount",   "QueryMigrationOrders"),
            TotalPageNum = GetRequiredInt(data, "totalPageNum", "QueryMigrationOrders")
        };
    }

    [HttpRpc]
    public async Task<string> TravelBack(string orderId, int currentGroupId, string currentGroupCode, string currentGroupName)
    {
        // API: /api/orderserivce/travelBack?travelOrderId={orderId}&groupId={currentGroupId}&groupCode={currentGroupCode}&groupName={currentGroupName}
        ArgumentException.ThrowIfNullOrWhiteSpace(orderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentGroupCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentGroupName);
        var data = await GetRequestData
                   (
                       "api/orderserivce/travelBack",
                       DCTravelAPIType.Order,
                       new Dictionary<string, string>
                       {
                           ["travelOrderId"] = orderId,
                           ["groupId"]       = currentGroupId.ToString(),
                           ["groupCode"]     = currentGroupCode,
                           ["groupName"]     = currentGroupName
                       }
                   );
        EnsureResultCode(data, "TravelBack");
        return GetRequiredString(data, "orderId", "TravelBack");
    }

    private static int GetRequiredInt(JsonNode data, string propertyName, string actionName)
    {
        var valueNode = data[propertyName] ?? throw new DCTravelAPIException($"{actionName}: missing field '{propertyName}'.");
        return valueNode.GetValue<int>();
    }

    private static string GetRequiredString(JsonNode data, string propertyName, string actionName)
    {
        var valueNode = data[propertyName] ?? throw new DCTravelAPIException($"{actionName}: missing field '{propertyName}'.");
        return valueNode.GetValue<string>();
    }

    private static void EnsureResultCode(JsonNode data, string actionName)
    {
        var resultCode = GetRequiredInt(data, "resultCode", actionName);

        if (resultCode != 0)
        {
            var message = data["resultMessage"]?.GetValue<string>() ?? "unknown";
            throw new DCTravelAPIException($"{actionName} failed, resultCode: {resultCode}, message: {message}");
        }
    }

    private static List<T> DeserializeList<T>(JsonNode data, string propertyName, string actionName)
    {
        var rawJson = GetRequiredString(data, propertyName, actionName);
        var list    = JsonSerializer.Deserialize<List<T>>(rawJson);
        return list ?? [];
    }
}
