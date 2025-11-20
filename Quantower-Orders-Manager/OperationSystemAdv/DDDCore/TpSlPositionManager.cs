using DivergentStrV0_1.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using TradingPlatform.BusinessLayer;

namespace DivergentStrV0_1.OperationSystemAdv.DDDCore
{
    public class TpSlPositionManager : PositionManagerBase<TpSlItemPosition>
    {
        #region INput
        public override int TradeCount => throw new NotImplementedException();
        private Symbol _symbol;
        private Account _account;
        private bool _isInitialized = false;

        // Cache child OCO orders that may arrive before the entry order (especially on Rithmic)
        private readonly Dictionary<string, List<Order>> _pendingChildrenByGroupToken = new();
        private readonly Dictionary<OrderTypeBehavior, OrderType> _closeOrderTypeCache = new();
        private const double PRICE_MATCH_TOLERANCE_MULTIPLIER = 2.1; // 2 ticks + epsilon
        private readonly Dictionary<string, (double? Stop, double? Take)> _plannedBrackets = new();
        public override event EventHandler QuitAll;
        public override double ExposedAmmount
        {
            get
            {
                try
                {
                    return Items.Sum(x => x.EntryOrder.TotalQuantity);

                }
                catch (Exception)
                {

                    var ammount = 0.0;

                    var orders = Core.Instance.Orders.Where(x => x.Symbol == _symbol && x.Account == _account && x.AdditionalInfo == null
                        && (x.Status == OrderStatus.Opened || x.Status == OrderStatus.PartiallyFilled));

                    ammount += orders.Sum(x =>  x.RemainingQuantity);

                    ammount += Core.Instance.Positions.Where(x => x.Symbol == _symbol && x.Account == _account).Sum(x => x.Quantity);

                    return ammount;
                }
            }
        }
        #endregion

        // Helper to capture Symbol/Account from first order
        private void CaptureSymbolAccount(Order order)
        {
            if (_symbol == null && order != null && order.Symbol != null && order.Account != null)
            {
                this._symbol = order.Symbol;
                this._account = order.Account;
                this._isInitialized = true;
            }
        }

        public TpSlPositionManager()
        {
            Core.Instance.PositionAdded += Instance_PositionAdded;
            
            // CRITICAL: Capture Symbol/Account from first order
            // This allows position tracking even when PlaceEntryOrder() bypassed
            // Lock removed: CreateItem() is now atomic in base class
            Core.Instance.OrderAdded += (obj) =>
            {
                CaptureSymbolAccount(obj);
                Instance_OrderAdded(obj);
            };

            Core.Instance.OrderRemoved += (e) =>
            {
                Order order = e as Order;
            };

            Core.Instance.PositionRemoved += (obj) =>
            {

                    if (obj.Symbol == _symbol && obj.Account == _account)
                    {
                        var item = this.Items.Where(x => x.Side == obj.Side && x.Position != null && x.Position.Id == obj.Id);
                        if (item.Any())
                        {
                            foreach (var i in item)
                            {
                                //📝 TODO: [Da completare] gestire la chiusura degli ordiri relativo al bug noto di ciclicita di inserimento ordini
                                //this.Items.Remove(i);
                                //this.ClosedItems.Add(i);
                                i.TryUpdateStatus();
                                i.Quit();

                                // Defensive: cancel any remaining OCO orders linked to this entry GroupId
                                // RITHMIC FIX: Handle composite GroupIds
                                try
                                {
                                    string entryGroup = i.EntryOrder?.GroupId;
                                    var entryTokens = SplitGroupIds(entryGroup).ToList();
                                    if (entryTokens.Count == 0 && !string.IsNullOrEmpty(entryGroup))
                                        entryTokens.Add(entryGroup);

                                    if (entryTokens.Count > 0)
                                    {
                                        var groupOrders = Core.Instance.Orders
                                            .Where(o =>
                                            {
                                                if (string.IsNullOrEmpty(o.GroupId)) return false;
                                                // Parse composite GroupId and check if entry's GroupId is a token
                                                var gs = SplitGroupIds(o.GroupId);
                                                return gs.Any(token => entryTokens.Contains(token)) &&
                                                       o.Id != i.EntryOrder.Id &&
                                                       (o.Status == OrderStatus.Opened || o.Status == OrderStatus.PartiallyFilled);
                                            })
                                            .ToList();
                                        
                                        foreach (var go in groupOrders)
                                        {
                                            try 
                                            { 
                                                go.Cancel(); 
                                                AppLog.System("TpSlPositionManager", "PositionRemovedCleanup", 
                                                    $"Cancelled OCO order {go.Id} ({go.OrderType?.Behavior}) with GroupId={go.GroupId}"); 
                                            }
                                            catch (Exception ex) 
                                            { 
                                                AppLog.Error("TpSlPositionManager", "PositionRemovedCleanup", 
                                                    $"Failed to cancel OCO order {go.Id}: {ex.Message}"); 
                                            }
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    AppLog.Error("TpSlPositionManager", "PositionRemovedCleanup", $"Cleanup error: {ex.Message}");
                                }
                            }
                        }
                    }
                
            };

        }

        public void PlanBracket(string comment, double? stopPrice, double? takePrice)
        {
            if (string.IsNullOrEmpty(comment))
                return;

            lock (_lockObj)
            {
                _plannedBrackets[comment] = (stopPrice, takePrice);
                AppLog.System("TpSlPositionManager", "BracketPlan",
                    $"Planned bracket for {comment}: SL={(stopPrice?.ToString("F2") ?? "null")}, TP={(takePrice?.ToString("F2") ?? "null")}");
            }
        }

        private void Instance_OrderAdded(Order obj)
        {
            CatchOrders(obj);
        }

        private static IEnumerable<string> SplitGroupIds(string groupId)
        {
            if (string.IsNullOrEmpty(groupId))
                yield break;

            foreach (var token in groupId.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = token.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                    yield return trimmed;
            }
        }

        private void CachePendingChild(Order order, IEnumerable<string> groupTokens)
        {
            foreach (var token in groupTokens)
            {
                if (!_pendingChildrenByGroupToken.TryGetValue(token, out var list))
                {
                    list = new List<Order>();
                    _pendingChildrenByGroupToken[token] = list;
                }

                if (!list.Any(o => o.Id == order.Id))
                {
                    list.Add(order);
                    AppLog.System("TpSlPositionManager", "PendingOco", 
                        $"Cached child order {order.Id} awaiting entry GroupId token '{token}'");
                }
            }
        }

        private void RemovePendingChild(Order order)
        {
            foreach (var kvp in _pendingChildrenByGroupToken.ToList())
            {
                var list = kvp.Value;
                if (list.RemoveAll(o => o.Id == order.Id) > 0 && list.Count == 0)
                    _pendingChildrenByGroupToken.Remove(kvp.Key);
            }
        }

        private bool TryAssignChildOrder(TpSlItemPosition item, Order childOrder)
        {
            if (item == null || childOrder == null)
                return false;

            if (item.EntryOrder == null)
                return false;

            if (childOrder.Id == item.EntryOrder.Id)
                return false;

            var behavior = childOrder.OrderType?.Behavior;

            if (behavior == OrderTypeBehavior.Stop)
            {
                if (string.Equals(item.StopLossOrderId, childOrder.Id, StringComparison.Ordinal))
                {
                    RemovePendingChild(childOrder);
                    return true; // already tracked
                }

                if (!string.IsNullOrEmpty(item.StopLossOrderId))
                {
                    var existingOrder = Core.Instance.Orders.FirstOrDefault(o => o.Id == item.StopLossOrderId);
                    if (existingOrder != null && (existingOrder.Status == OrderStatus.Opened || existingOrder.Status == OrderStatus.PartiallyFilled))
                        return false; // keep current active SL
                }

                if (string.IsNullOrEmpty(item.StopLossOrderId))
                {
                    item.StopLossOrderId = childOrder.Id;
                    AppLog.System("TpSlPositionManager", "CatchOrders",
                        $"✅ Captured SL order {childOrder.Id} (GroupId={childOrder.GroupId}) for item {item.Id}");
                    RemovePendingChild(childOrder);
                    return true;
                }
            }
            else if (behavior == OrderTypeBehavior.Limit)
            {
                if (string.Equals(item.TakeProfitOrderId, childOrder.Id, StringComparison.Ordinal))
                {
                    RemovePendingChild(childOrder);
                    return true; // already tracked
                }

                if (!string.IsNullOrEmpty(item.TakeProfitOrderId))
                {
                    var existingOrder = Core.Instance.Orders.FirstOrDefault(o => o.Id == item.TakeProfitOrderId);
                    if (existingOrder != null && (existingOrder.Status == OrderStatus.Opened || existingOrder.Status == OrderStatus.PartiallyFilled))
                        return false; // keep current active TP
                }

                if (string.IsNullOrEmpty(item.TakeProfitOrderId))
                {
                    item.TakeProfitOrderId = childOrder.Id;
                    AppLog.System("TpSlPositionManager", "CatchOrders",
                        $"✅ Captured TP order {childOrder.Id} (GroupId={childOrder.GroupId}) for item {item.Id}");
                    RemovePendingChild(childOrder);
                    return true;
                }
            }

            return false;
        }

        private void AttachPendingChildrenForItem(TpSlItemPosition item)
        {
            var entryGroup = item?.EntryOrder?.GroupId;
            if (string.IsNullOrEmpty(entryGroup))
            {
                TryHydrateChildOrders(item);
                return;
            }

            var entryTokens = SplitGroupIds(entryGroup).ToList();
            if (entryTokens.Count == 0)
                entryTokens.Add(entryGroup);

            foreach (var token in entryTokens.ToList())
            {
                if (_pendingChildrenByGroupToken.TryGetValue(token, out var pendingList))
                {
                    foreach (var child in pendingList.ToList())
                        TryAssignChildOrder(item, child);

                    if (pendingList.Count == 0)
                        _pendingChildrenByGroupToken.Remove(token);
                }
            }

            TryHydrateChildOrders(item);
        }

        private bool TryHydrateChildOrders(TpSlItemPosition item)
        {
            if (item == null)
                return false;

            bool updated = false;

            // Fast-path: bind directly from Position if available
            if (item.Position != null)
            {
                if (string.IsNullOrEmpty(item.StopLossOrderId) && item.Position.StopLoss != null)
                {
                    item.StopLossOrderId = item.Position.StopLoss.Id;
                    item.ExpectedSlPrice = item.Position.StopLoss.TriggerPrice;
                    updated = true;
                    AppLog.System("TpSlPositionManager", "FastBind", $"SL bound via Position.StopLoss for item {item.Id}");
                }
                if (string.IsNullOrEmpty(item.TakeProfitOrderId) && item.Position.TakeProfit != null)
                {
                    item.TakeProfitOrderId = item.Position.TakeProfit.Id;
                    item.ExpectedTpPrice = item.Position.TakeProfit.Price;
                    updated = true;
                    AppLog.System("TpSlPositionManager", "FastBind", $"TP bound via Position.TakeProfit for item {item.Id}");
                }
            }

            if (string.IsNullOrEmpty(item.StopLossOrderId))
            {
                updated |= TryBindChildOrderByScan(item, OrderTypeBehavior.Stop);
            }

            if (string.IsNullOrEmpty(item.TakeProfitOrderId))
            {
                updated |= TryBindChildOrderByScan(item, OrderTypeBehavior.Limit);
            }

            return updated;
        }

        private void EnsureProtectiveOrders(TpSlItemPosition item)
        {
            if (item == null)
                return;

            var position = ResolvePosition(item);
            var symbol = _symbol ?? position?.Symbol ?? item.Symbol;
            if (symbol == null)
                return;

            double quantity = Math.Abs(position?.Quantity ?? item.Quantity);
            if (quantity <= 0 && item.EntryOrder != null)
                quantity = Math.Abs(item.EntryOrder.TotalQuantity);

            if (symbol.MinLot > 0 && quantity < symbol.MinLot)
                quantity = symbol.MinLot;

            if (quantity <= 0)
                return;

            string positionId = position?.Id ?? item.PositionId;
            if (string.IsNullOrEmpty(positionId))
            {
                AppLog.System("TpSlPositionManager", "ProtectiveOrders",
                    $"Cannot ensure protective orders for item {item.Id?.Substring(0, 8) ?? "unknown"}: missing position id");
                return;
            }

            Side positionSide = position?.Side ?? item.Side;

            // Ensure Stop Loss
            double desiredSl = !double.IsNaN(item.ExpectedSlPrice) ? item.ExpectedSlPrice : item.PendingSlTarget;
            if (!double.IsNaN(desiredSl))
            {
                var workingSl = item.GetStopLossOrder(_symbol);

                if (workingSl == null)
                {
                    bool isFirstPlacement = item.LastStopPlacementUtc == DateTime.MinValue;
                    if (isFirstPlacement || (DateTime.UtcNow - item.LastStopPlacementUtc).TotalMilliseconds > 500)
                    {
                        AppLog.System("TpSlPositionManager", "SlRecon",
                            $"Stop missing for item {item.Id}: expected {desiredSl:F2}, placing new stop");
                        item.ExpectedSlPrice = desiredSl;
                        PlaceOrReplaceStop(item, quantity);
                    }
                }
                else
                {
                    double delta = Math.Abs(workingSl.TriggerPrice - desiredSl);
                    if (delta > (symbol?.TickSize ?? 0.25))
                    {
                        AppLog.System("TpSlPositionManager", "SlRecon",
                            $"Stop drift for item {item.Id}: planned {desiredSl:F2}, broker {workingSl.TriggerPrice:F2} (Δ={delta:F2})");
                    }
                }
            }

            // Ensure Take Profit
            double desiredTp = !double.IsNaN(item.ExpectedTpPrice) ? item.ExpectedTpPrice : item.PendingTpTarget;
            if (!double.IsNaN(desiredTp))
            {
                var workingTp = item.GetTakeProfitOrder(_symbol);

                if (workingTp == null)
                {
                    bool isFirstPlacement = item.LastTpPlacementUtc == DateTime.MinValue;
                    if (isFirstPlacement || (DateTime.UtcNow - item.LastTpPlacementUtc).TotalMilliseconds > 500)
                    {
                        AppLog.System("TpSlPositionManager", "TpRecon",
                            $"Take profit missing for item {item.Id}: expected {desiredTp:F2}, placing new TP");
                        item.ExpectedTpPrice = desiredTp;
                        PlaceOrReplaceTakeProfit(item, quantity);
                    }
                }
                else
                {
                    double delta = Math.Abs(workingTp.Price - desiredTp);
                    if (delta > (symbol?.TickSize ?? 0.25))
                    {
                        AppLog.System("TpSlPositionManager", "TpRecon",
                            $"Take profit drift for item {item.Id}: planned {desiredTp:F2}, broker {workingTp.Price:F2} (Δ={delta:F2})");
                    }
                }
            }
        }

        private void PlaceOrReplaceStop(TpSlItemPosition item, double quantity)
        {
            if (_symbol == null || _account == null)
                return;

            var orderType = ResolveCloseOrderType(OrderTypeBehavior.Stop);
            if (orderType == null)
            {
                AppLog.Error("TpSlPositionManager", "PlaceStop", "No stop order type available for symbol");
                return;
            }

            string positionId = item.Position?.Id ?? item.PositionId;
            if (string.IsNullOrEmpty(positionId))
            {
                AppLog.System("TpSlPositionManager", "PlaceStop", $"Skipping stop placement for {item.Id}: missing position id");
                return;
            }

            var positionSide = item.Position?.Side ?? item.Side;
            var side = positionSide == Side.Buy ? Side.Sell : Side.Buy;

            double trigger = _symbol.RoundPriceToTickSize(item.ExpectedSlPrice);

            var req = new PlaceOrderRequestParameters
            {
                Symbol = _symbol,
                Account = _account,
                Side = side,
                Quantity = quantity,
                OrderTypeId = orderType.Id,
                TriggerPrice = trigger,
                TimeInForce = TimeInForce.Day,
                PositionId = positionId,
                Comment = $"{item.Id}.{OrderTypeSubcomment.StopLoss}"
            };

            req.AdditionalParameters = new List<SettingItem>
            {
                new SettingItemBoolean(OrderType.REDUCE_ONLY, true)
            };

            PreflightOrder(req);
            TradingOperationResult result;
            try
            {
                result = PlaceOrderWithRetry(req, "PlaceStop");
            }
            catch (Exception ex)
            {
                AppLog.Error("TpSlPositionManager", "PlaceStop", $"Exception during place: {ex.Message}");
                return;
            }
            if (result.Status == TradingOperationResultStatus.Success)
            {
                string placedId = result.OrderId;
                if (!string.IsNullOrEmpty(placedId))
                {
                    item.StopLossOrderId = placedId;
                }
                item.PlanStopPrice(trigger);
                item.PendingSlTarget = double.NaN;
                item.MarkStopPlacement();
                AppLog.Trading("TpSlPositionManager", "PlaceStop",
                    $"Stop order placed at {trigger:F2} for item {item.Id} (qty {quantity})");
            }
            else
            {
                AppLog.Error("TpSlPositionManager", "PlaceStop",
                    $"Failed to place stop for {item.Id}: {result.Message}");
            }
        }

        private void PlaceOrReplaceTakeProfit(TpSlItemPosition item, double quantity)
        {
            if (_symbol == null || _account == null)
                return;

            var orderType = ResolveCloseOrderType(OrderTypeBehavior.Limit);
            if (orderType == null)
            {
                AppLog.Error("TpSlPositionManager", "PlaceTp", "No limit order type available for symbol");
                return;
            }

            string positionId = item.Position?.Id ?? item.PositionId;
            if (string.IsNullOrEmpty(positionId))
            {
                AppLog.System("TpSlPositionManager", "PlaceTp", $"Skipping TP placement for {item.Id}: missing position id");
                return;
            }

            var positionSide = item.Position?.Side ?? item.Side;
            var side = positionSide == Side.Buy ? Side.Sell : Side.Buy;

            double price = _symbol.RoundPriceToTickSize(item.ExpectedTpPrice);

            var req = new PlaceOrderRequestParameters
            {
                Symbol = _symbol,
                Account = _account,
                Side = side,
                Quantity = quantity,
                OrderTypeId = orderType.Id,
                Price = price,
                TimeInForce = TimeInForce.Day,
                PositionId = positionId,
                Comment = $"{item.Id}.{OrderTypeSubcomment.TakeProfit}"
            };

            req.AdditionalParameters = new List<SettingItem>
            {
                new SettingItemBoolean(OrderType.REDUCE_ONLY, true)
            };

            PreflightOrder(req);
            TradingOperationResult result;
            try
            {
                result = PlaceOrderWithRetry(req, "PlaceTp");
            }
            catch (Exception ex)
            {
                AppLog.Error("TpSlPositionManager", "PlaceTp", $"Exception during place: {ex.Message}");
                return;
            }
            if (result.Status == TradingOperationResultStatus.Success)
            {
                string placedId = result.OrderId;
                if (!string.IsNullOrEmpty(placedId))
                {
                    item.TakeProfitOrderId = placedId;
                }
                item.PlanTakeProfitPrice(price);
                item.PendingTpTarget = double.NaN;
                item.MarkTpPlacement();
                AppLog.Trading("TpSlPositionManager", "PlaceTp",
                    $"Take profit placed at {price:F2} for item {item.Id} (qty {quantity})");
            }
            else
            {
                AppLog.Error("TpSlPositionManager", "PlaceTp",
                    $"Failed to place take profit for {item.Id}: {result.Message}");
            }
        }

        private OrderType ResolveCloseOrderType(OrderTypeBehavior behavior)
        {
            if (_symbol == null)
                return null;

            if (_closeOrderTypeCache.TryGetValue(behavior, out var cached) && cached != null)
                return cached;

            var allowed = _symbol.GetAlowedOrderTypes(OrderTypeUsage.CloseOrder);
            if (allowed == null)
                return null;

            var resolved = allowed.FirstOrDefault(o => o.Behavior == behavior);
            if (resolved != null)
                _closeOrderTypeCache[behavior] = resolved;

            return resolved;
        }

        private bool TryBindChildOrderByScan(TpSlItemPosition item, OrderTypeBehavior behavior)
        {
            if (_symbol == null || _account == null)
                return false;

            double tolerance = (_symbol.TickSize > 0 ? _symbol.TickSize : 0.25) * PRICE_MATCH_TOLERANCE_MULTIPLIER;
            double expectedPrice = behavior == OrderTypeBehavior.Stop ? item.ExpectedSlPrice : item.ExpectedTpPrice;

            var ordersSnapshot = Core.Instance.Orders
                .Where(o => o.Symbol == _symbol && o.Account == _account)
                .ToList();

            Order matched = null;

            if (!double.IsNaN(expectedPrice))
            {
                matched = ordersSnapshot.FirstOrDefault(o =>
                    o.OrderType?.Behavior == behavior &&
                    (o.Status == OrderStatus.Opened || o.Status == OrderStatus.PartiallyFilled) &&
                    o.Id != item.EntryOrder?.Id &&
                    PriceMatches(expectedPrice, behavior == OrderTypeBehavior.Stop ? o.TriggerPrice : o.Price, tolerance));
            }

            if (matched == null && !string.IsNullOrEmpty(item.PositionId))
            {
                matched = ordersSnapshot.FirstOrDefault(o =>
                    o.OrderType?.Behavior == behavior &&
                    (o.Status == OrderStatus.Opened || o.Status == OrderStatus.PartiallyFilled) &&
                    o.Id != item.EntryOrder?.Id &&
                    string.Equals(o.PositionId, item.PositionId, StringComparison.Ordinal));
            }

            if (matched == null)
                return false;

            if (behavior == OrderTypeBehavior.Stop)
            {
                item.StopLossOrderId = matched.Id;
                item.ExpectedSlPrice = matched.TriggerPrice;
            }
            else
            {
                item.TakeProfitOrderId = matched.Id;
                item.ExpectedTpPrice = matched.Price;
            }

            RemovePendingChild(matched);

            return true;
        }

        // PHASE A/B: Preflight & OMS retry helpers (close orders)
        private void PreflightOrder(PlaceOrderRequestParameters req)
        {
            if (req == null || _symbol == null)
                return;

            var orderType = _symbol.GetAlowedOrderTypes(OrderTypeUsage.CloseOrder)?.FirstOrDefault(o => o.Id == req.OrderTypeId);
            if (orderType != null)
            {
                if (orderType.Behavior == OrderTypeBehavior.Limit)
                {
                    if (!double.IsNaN(req.Price)) req.Price = _symbol.RoundPriceToTickSize(req.Price);
                    req.TriggerPrice = double.NaN;
                }
                else if (orderType.Behavior == OrderTypeBehavior.Stop)
                {
                    if (!double.IsNaN(req.TriggerPrice)) req.TriggerPrice = _symbol.RoundPriceToTickSize(req.TriggerPrice);
                    req.Price = double.NaN;
                }
            }

            // For safety: ensure PositionId provided for close orders
            if (string.IsNullOrEmpty(req.PositionId))
            {
                AppLog.System("TpSlPositionManager", "Preflight", "Missing PositionId for close order");
            }
        }

        private TradingOperationResult PlaceOrderWithRetry(PlaceOrderRequestParameters req, string context)
        {
            try
            {
                var result = Core.Instance.PlaceOrder(req);
                if (result.Status == TradingOperationResultStatus.Success)
                    return result;

                string msg = result.Message ?? "unknown";
                AppLog.Error("TpSlPositionManager", "OmsRefuse", $"{context}: Refuse: {msg} req: beh? posId={req.PositionId ?? "-"} price={req.Price} trig={req.TriggerPrice}");

                bool retried = false;

                // Off tick → re-round retry once
                if (msg.IndexOf("tick", StringComparison.OrdinalIgnoreCase) >= 0 || msg.IndexOf("increment", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (!double.IsNaN(req.Price)) req.Price = _symbol.RoundPriceToTickSize(req.Price);
                    if (!double.IsNaN(req.TriggerPrice)) req.TriggerPrice = _symbol.RoundPriceToTickSize(req.TriggerPrice);
                    retried = true;
                }

                // Unsupported parameter → drop additional, retry once
                if (!retried && (msg.IndexOf("not supported", StringComparison.OrdinalIgnoreCase) >= 0 || msg.IndexOf("reduce", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    if (req.AdditionalParameters != null)
                        req.AdditionalParameters.Clear();
                    retried = true;
                }

                if (retried)
                {
                    var r2 = Core.Instance.PlaceOrder(req);
                    if (r2.Status != TradingOperationResultStatus.Success)
                        AppLog.Error("TpSlPositionManager", "OmsRefuse", $"{context}: retry failed: {r2.Message}");
                    return r2;
                }

                return result;
            }
            catch (Exception ex)
            {
                AppLog.Error("TpSlPositionManager", "OmsRefuse", $"{context}: exception during place: {ex.Message}");
                throw;
            }
        }

        private static bool PriceMatches(double expected, double actual, double tolerance)
        {
            if (double.IsNaN(expected) || double.IsNaN(actual))
                return false;

            return Math.Abs(actual - expected) <= tolerance;
        }

        public bool EnsureChildOrders(TpSlItemPosition item)
        {
            var hydrated = TryHydrateChildOrders(item);
            EnsureProtectiveOrders(item);
            return hydrated;
        }

        private void CatchOrders(Order order)
        {
            bool orderIsEntry = false;

            if (order.Comment != null)
            {
                var splitted = GetSplittedComment(order.Comment);

                if (splitted is KeyValuePair<string, OrderTypeSubcomment> parsedComment)
                {
                    lock (_lockObj)
                    {
                        if (!_itemsDictionary.ContainsKey(parsedComment.Key))
                            this.CreateItem(parsedComment.Key);
                    }

                    var itm = this.Items.FirstOrDefault(x => x.Id == parsedComment.Key) as TpSlItemPosition;
                    if (itm == null)
                        return;

                    switch (parsedComment.Value)
                    {
                        case OrderTypeSubcomment.Entry:
                            if (itm.EntryOrder == null)
                            {
                                try
                                {
                                    itm.SetEntryOrder(order);
                                    lock (_lockObj)
                                    {
                                        if (_plannedBrackets.TryGetValue(parsedComment.Key, out var planned))
                                        {
                                            if (planned.Stop.HasValue && !double.IsNaN(planned.Stop.Value))
                                                itm.PlanStopPrice(planned.Stop.Value);
                                            if (planned.Take.HasValue && !double.IsNaN(planned.Take.Value))
                                                itm.PlanTakeProfitPrice(planned.Take.Value);

                                            _plannedBrackets.Remove(parsedComment.Key);

                                            AppLog.System("TpSlPositionManager", "BracketPlan",
                                                $"Applied planned bracket to {parsedComment.Key}: SL={(planned.Stop?.ToString("F2") ?? "null")}, TP={(planned.Take?.ToString("F2") ?? "null")}");
                                        }
                                    }
                                    AppLog.System("TpSlPositionManager", "CatchOrders",
                                        $"Entry order bound: OrderId={order.Id}, ItemId={parsedComment.Key}");

                                    AppLog.System("TpSlPositionManager", "BundleState",
                                        $"Bundle {parsedComment.Key}: Entry={order.Id}, SL={itm.StopLossOrderId ?? "pending"}, TP={itm.TakeProfitOrderId ?? "pending"}, Pos={itm.PositionId ?? "pending"}");

                                    AttachPendingChildrenForItem(itm);
                                    orderIsEntry = true;
                                }
                                catch (Exception ex)
                                {
                                    AppLog.Error("TpSlPositionManager", "EntryOrder",
                                        $"Failed to bind entry order {order.Id} to item {parsedComment.Key}: {ex.Message}");
                                    throw;
                                }
                            }
                            else
                            {
                                AppLog.System("TpSlPositionManager", "CatchOrders",
                                    $"Entry order already bound for item {parsedComment.Key}, ignoring duplicate event");
                                orderIsEntry = true;
                            }
                            break;

                        case OrderTypeSubcomment.StopLoss:
                            itm.StopLossOrderId = order.Id;
                            if (!double.IsNaN(order.TriggerPrice))
                                itm.ExpectedSlPrice = order.TriggerPrice;
                            itm.MarkStopPlacement();
                            AppLog.System("TpSlPositionManager", "CatchOrders",
                                $"Linked stop order {order.Id} to item {parsedComment.Key} at {order.TriggerPrice:F2}");
                            return;

                        case OrderTypeSubcomment.TakeProfit:
                            itm.TakeProfitOrderId = order.Id;
                            if (!double.IsNaN(order.Price))
                                itm.ExpectedTpPrice = order.Price;
                            itm.MarkTpPlacement();
                            AppLog.System("TpSlPositionManager", "CatchOrders",
                                $"Linked take profit order {order.Id} to item {parsedComment.Key} at {order.Price:F2}");
                            return;
                    }
                }
            }

            if (orderIsEntry)
                return;


        // CRITICAL FIX: Capture SL/TP orders created by OCO brackets
        // When using SlTpHolder, Quantower creates orders with same GroupId as entry order
        // but WITHOUT our comment system, so we must track by GroupId matching
        // RITHMIC FIX: Handle composite GroupIds like "2358333981,2358333982" and cache until entry arrives
        if (!string.IsNullOrEmpty(order.GroupId))
        {
            var childTokens = SplitGroupIds(order.GroupId).ToArray();
 
            bool captured = false;
            foreach (var item in this.Items)
            {
                var entryGroup = item.EntryOrder?.GroupId;
                if (string.IsNullOrEmpty(entryGroup))
                    continue;
 
                var entryTokens = SplitGroupIds(entryGroup).ToArray();
                if (entryTokens.Length == 0)
                    entryTokens = new[] { entryGroup };

                if (entryTokens.Any(token => childTokens.Contains(token)))
                {
                    if (TryAssignChildOrder(item as TpSlItemPosition, order))
                    {
                        captured = true;
                        break;
                    }
                }
            }

            if (!captured && childTokens.Length > 0)
            {
                CachePendingChild(order, childTokens);
            }
        }
        }

        private void Instance_PositionAdded(Position obj)
        {
            lock (_lockObj)
            {
                if (!AccountMatches(obj.Account) || !SymbolMatches(obj.Symbol))
                    return;

                CatchPosition(obj);
            }
        }

        public override void Dispose()
        {
            AppLog.System("TpSlPositionManager", "Dispose", 
                $"Starting disposal: {Items?.Count ?? 0} active items, {ClosedItems?.Count ?? 0} closed items");
            
            // Unsubscribe events FIRST (prevent new items during cleanup)
            Core.Instance.PositionAdded -= Instance_PositionAdded;
            Core.Instance.OrderAdded -= Instance_OrderAdded;
            Core.Instance.OrderRemoved += (e) =>
            {
                Order order = e as Order;
            };
            
            // Note: OrderRemoved lambda cannot be properly unsubscribed (different instance)
            // This is acceptable - event source lifetime matches manager lifetime
            
            // Clear Items collection
            if (Items != null)
            {
                // Unsubscribe ItemClosed from all active items
                foreach (var item in Items.ToList()) // ToList() creates copy to avoid modification during iteration
                {
                    try 
                    { 
                        item.ItemClosed -= TpSlPositionManager_ItemClosed;
                    }
                    catch (Exception ex)
                    {
                        AppLog.Error("TpSlPositionManager", "Dispose", $"Error unsubscribing ItemClosed: {ex.Message}");
                    }
                }
                Items.Clear();
                AppLog.System("TpSlPositionManager", "Dispose", "Items cleared");
            }
            
            // Clear ClosedItems collection (releases 300-900MB of trade history)
            if (ClosedItems != null)
            {
                int count = ClosedItems.Count;
                ClosedItems.Clear();
                AppLog.System("TpSlPositionManager", "Dispose", $"ClosedItems cleared: {count} items released");
            }
            
            // Clear dictionary
            if (_itemsDictionary != null)
            {
                _itemsDictionary.Clear();
                AppLog.System("TpSlPositionManager", "Dispose", "_itemsDictionary cleared");
            }
            
            AppLog.System("TpSlPositionManager", "Dispose", "Disposal complete - memory released");
        }

        // PHASE 9: Method removed - replaced by tick-level discovery in RowanStrategy.Update()

        public override void PlaceEntryOrder(PlaceOrderRequestParameters req, string comment, List<PlaceOrderRequestParameters> sl, List<PlaceOrderRequestParameters> tp, object sender = null)
        {
            if (!_isInitialized)
            {
                this._symbol = req.Symbol;
                this._account = req.Account;
                this._isInitialized = true;
            }


            #region 🐞 BUG [Bug noto da risolvere]
            // BUG Vengono eseguiti ingressi multipli anche se gli item sono gia registrati 
            #endregion


            PlaceOrderRequestParameters orderobj = req;
            orderobj.Comment = $"{comment}.{OrderTypeSubcomment.Entry.ToString()}";

            double? plannedSlPrice = null;
            double? plannedTpPrice = null;

            if (sl != null && sl.Count > 0)
            {
                var slParams = sl[0];
                double expected = slParams.TriggerPrice;
                if (_symbol != null)
                    expected = _symbol.RoundPriceToTickSize(expected);
                plannedSlPrice = expected;
            }

            if (tp != null && tp.Count > 0)
            {
                var tpParams = tp[0];
                double expected = tpParams.Price;
                if (_symbol != null)
                    expected = _symbol.RoundPriceToTickSize(expected);
                plannedTpPrice = expected;
            }

            PlanBracket(comment, plannedSlPrice, plannedTpPrice);

            var result = Core.Instance.PlaceOrder(orderobj);

            if ( result.Status == TradingOperationResultStatus.Success)
            {
                AppLog.System("TpSlPositionManager", "EntryOrder", $"Entry order placed successfully with comment: {orderobj.Comment}");
                
                // CRITICAL: Record expected prices on the SINGLE item that should exist
                lock (_lockObj)
                {
                    var targetItem = Items.OfType<TpSlItemPosition>()
                        .FirstOrDefault(i => string.Equals(i.Id, comment, StringComparison.Ordinal));
                    
                    if (targetItem == null)
                    {
                        AppLog.Error("TpSlPositionManager", "EntryOrder",
                            $"CRITICAL: Item {comment} not found after order placement - may indicate race condition");
                        return;
                    }
                    
                    // Atomically set expected prices on the item
                    if (sl != null && sl.Count > 0)
                    {
                        var slParams = sl[0];
                        double expected = slParams.TriggerPrice;
                        if (_symbol != null)
                            expected = _symbol.RoundPriceToTickSize(expected);
                        targetItem.PlanStopPrice(expected);
                        targetItem.StopLossOrderId = null; // protective order will be placed post-fill
                        AppLog.System("TpSlPositionManager", "EntryOrder", 
                            $"Expected SL price recorded: {expected:F2} for item {targetItem.Id}");
                    }
                    
                    if (tp != null && tp.Count > 0)
                    {
                        var tpParams = tp[0];
                        double expected = tpParams.Price;
                        if (_symbol != null)
                            expected = _symbol.RoundPriceToTickSize(expected);
                        targetItem.PlanTakeProfitPrice(expected);
                        targetItem.TakeProfitOrderId = null;
                        AppLog.System("TpSlPositionManager", "EntryOrder", 
                            $"Expected TP price recorded: {expected:F2} for item {targetItem.Id}");
                    }
                }
                
                AppLog.System("TpSlPositionManager", "EntryOrder",
                    $"Expected prices set, tick-level discovery active");
            }
            else
                AppLog.Error("TpSlPositionManager", "EntryOrder", $"Failed to place entry order with comment: {orderobj.Comment}. Reason: {result.Message}");

            if (!this._isInitialized)
            {
                AppLog.Error("TpSlPositionManager", "Initialization", "TpSlPositionManager is not initialized.");
                return;
            }
        }

        private void CatchPosition(Position pos)
        {
            try
            {
                var tpItems = this.Items.OfType<TpSlItemPosition>().ToList();

                // Step 1: Update ALL items already tracking this Position.Id
                var alreadyTracked = tpItems
                    .Where(x => string.Equals(x.PositionId, pos.Id, StringComparison.Ordinal))
                    .ToList();

                if (alreadyTracked.Any())
                {
                    foreach (var tracked in alreadyTracked)
                    {
                        tracked.SetPosition(pos);
                        AppLog.System("TpSlPositionManager", "CatchPosition",
                            $"🔄 Position updated: Id={pos.Id}, Side={pos.Side}, Qty={pos.Quantity}");
                        EnsureProtectiveOrders(tracked);
                    }
                    return;  // CRITICAL: Don't bind to another item
                }

                // Step 2: ONLY if no item tracks this Position.Id, bind to unassigned item
                var candidate = tpItems
                    .Where(x =>
                        x.Side == pos.Side &&
                        x.EntryOrder != null &&
                        string.IsNullOrEmpty(x.PositionId) &&  // Must be unbound
                        x.Position == null)
                    .OrderByDescending(x => x.EntryOrder?.LastUpdateTime ?? DateTime.MinValue)
                    .FirstOrDefault();

                if (candidate != null)
                {
                    candidate.SetPosition(pos);
                    AppLog.System("TpSlPositionManager", "CatchPosition",
                        $"✅ Position matched: Id={pos.Id}, Side={pos.Side}, Item={candidate.Id}");

                    if (TryHydrateChildOrders(candidate))
                    {
                        AppLog.System("TpSlPositionManager", "CatchPosition",
                            $"🔄 Child orders hydrated for item {candidate.Id} | SL={candidate.StopLossOrderId ?? "-"}, TP={candidate.TakeProfitOrderId ?? "-"}");
                    }

                    // PHASE 8: Log bundle completion
                    AppLog.System("TpSlPositionManager", "BundleState",
                        $"Bundle {candidate.Id}: Entry={candidate.EntryOrder?.Id}, SL={candidate.StopLossOrderId ?? "missing"}, TP={candidate.TakeProfitOrderId ?? "missing"}, Pos={pos.Id} - COMPLETE");

                    EnsureProtectiveOrders(candidate);
                }
                else if (!alreadyTracked.Any())
                {
                    AppLog.System("TpSlPositionManager", "CatchPosition",
                        $"⚠️ No unbound item for position: Id={pos.Id}, Side={pos.Side}, Qty={pos.Quantity}");
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("TpSlPositionManager", "PositionSetup", 
                    $"Error setting position for {pos.Id}: {ex.Message}");
                throw;
            }
        }

        private Position ResolvePosition(TpSlItemPosition item)
        {
            if (item == null)
                return null;

            if (item.Position != null)
                return item.Position;

            if (!string.IsNullOrEmpty(item.PositionId))
            {
                var live = Core.Instance.Positions.FirstOrDefault(p => p.Id == item.PositionId);
                if (live != null)
                {
                    item.SetPosition(live);
                    return live;
                }
            }

            return null;
        }

        public override void UpdateSl(TpSlItemPosition item, Func<double, double> updateFunction)
        {
            if (item == null)
                throw new ArgumentNullException(nameof(item));

            var resolvedPosition = ResolvePosition(item);

            // RELAXED GUARD: Allow trailing if we have expected SL price
            if (resolvedPosition == null && double.IsNaN(item.ExpectedSlPrice))
            {
                AppLog.System("TpSlPositionManager", "UpdateSl_Skip",
                    $"[Item {item.Id?.Substring(0, 8) ?? "unknown"}] No position and no expected SL price");
                return;
            }

            // Use lazy resolver with price-based fuzzy matching
            Order slOrder = item.GetStopLossOrder(_symbol);
            double currentTrigger = slOrder?.TriggerPrice ?? item.ExpectedSlPrice;
            double currentPrice = slOrder?.Price ?? item.ExpectedSlPrice;

            if (double.IsNaN(currentTrigger))
            {
                string posInfo = item.Position != null ? $"Pos={item.Position.Id}" : "NoPos";
                AppLog.System("TpSlPositionManager", "UpdateSl_Skip",
                    $"[Item {item.Id?.Substring(0, 8) ?? "unknown"}] Unable to compute SL target (missing baseline, {posInfo})");
                return;
            }

            var proposedTrigger = updateFunction(currentTrigger);
            var proposedPrice = double.IsNaN(currentPrice) ? proposedTrigger : updateFunction(currentPrice);

            if (double.IsNaN(proposedPrice))
                proposedPrice = proposedTrigger;

            double brokerTrigger = slOrder?.TriggerPrice ?? double.NaN;
            double deltaFromPlan = (!double.IsNaN(item.ExpectedSlPrice) && !double.IsNaN(brokerTrigger))
                ? Math.Abs(brokerTrigger - item.ExpectedSlPrice)
                : double.NaN;

            AppLog.System("TpSlPositionManager", "SlRecon",
                $"[Item {item.Id?.Substring(0, 8) ?? "unknown"}] Target={proposedTrigger:F2}, Broker={(slOrder != null ? brokerTrigger.ToString("F2") : "N/A")}, Planned={item.ExpectedSlPrice:F2}, Δ={(!double.IsNaN(deltaFromPlan) ? deltaFromPlan.ToString("F2") : "N/A")}");

            if (slOrder == null)
            {
                item.PlanStopPrice(proposedTrigger);
                EnsureProtectiveOrders(item);
                return;
            }

            item.StopLossOrderId = slOrder.Id;

            double Snap(double p)
            {
                try
                {
                    var ts = _symbol?.TickSize ?? 0;
                    return ts > 0 ? Math.Round(p / ts) * ts : p;
                }
                catch { return p; }
            }

            proposedTrigger = Snap(proposedTrigger);
            proposedPrice = Snap(proposedPrice);

            double tickSize = _symbol?.TickSize ?? 0.25;
            bool triggerChanged = double.IsNaN(slOrder.TriggerPrice) || Math.Abs(slOrder.TriggerPrice - proposedTrigger) >= tickSize;
            bool priceChanged = double.IsNaN(slOrder.Price) || Math.Abs(slOrder.Price - proposedPrice) >= tickSize;

            if (triggerChanged || priceChanged)
            {
                AppLog.Trading("TpSlPositionManager", "UpdateSl", $"Updating SL: {slOrder.TriggerPrice:F2} → {proposedTrigger:F2} for position {resolvedPosition?.Id ?? item.PositionId}");
                // Stop modify: send TriggerPrice only
                Core.Instance.ModifyOrder(slOrder, triggerPrice: proposedTrigger, price: double.NaN);
                
                // Update local expectation
                item.PlanStopPrice(proposedTrigger);

                // Verify snapshot up to 2 times; if mismatch >1 tick, cancel+place
                bool verified = false;
                for (int i = 0; i < 2; i++)
                {
                    try { System.Threading.Thread.Sleep(250); } catch { }
                    var snap = Core.Instance.Orders.FirstOrDefault(o => o.Id == slOrder.Id);
                    if (snap != null)
                    {
                        double dt = Math.Abs((snap.TriggerPrice) - proposedTrigger);
                        if (dt <= (_symbol?.TickSize ?? 0.25)) { verified = true; break; }
                    }
                }
                if (!verified)
                {
                    try
                    {
                        slOrder.Cancel();
                        AppLog.System("TpSlPositionManager", "SlReplace", $"Chart/broker mismatch; replacing SL at {proposedTrigger:F2}");
                    }
                    catch { }
                    // place fresh
                    double fallbackQty = resolvedPosition?.Quantity ?? Math.Abs(item.Quantity);
                    PlaceOrReplaceStop(item, fallbackQty);
                }
            }
            else
            {
                AppLog.System("TpSlPositionManager", "UpdateSl", $"SL unchanged: {slOrder.TriggerPrice:F2} (target {proposedTrigger:F2})");
            }
        }

        public override void UpdateTp(TpSlItemPosition item, Func<double, double> updateFunction)
        {
            throw new NotImplementedException();
        }

        public override void CreateItem(string comment)
        {
            lock (_lockObj)
            {
                if (_itemsDictionary.ContainsKey(comment))
                {
                    AppLog.Error("TpSlPositionManager", "ItemLifecycle", $"Item with ID {comment} already exists; skipped recreation.");
                    return;
                }

                base.CreateItem(comment);

                if (this.Items.Count > 0)
                {
                    var createdItem = this.Items.Last();
                    createdItem.ItemClosed += this.TpSlPositionManager_ItemClosed;
                    AppLog.System("TpSlPositionManager", "ItemLifecycle", $"Creato nuovo SlTpItems con ID: {comment}");
                }
            }
        }

        private void TpSlPositionManager_ItemClosed(object sender, PositionManagerStatus[] e)
        {
            var item = sender as TpSlItemPosition;
            if (item != null)
            {
                item.ItemClosed -= this.TpSlPositionManager_ItemClosed;
                this.Items.Remove(item);
                this.ClosedItems.Add(item);
                AppLog.System("TpSlPositionManager", "ItemLifecycle", $"Item con ID {item.Id} chiuso e spostato in ClosedItems.");
            }

        }
        protected override TpSlItemPosition CreateNewItem(string comment) => new TpSlItemPosition(comment);

        /// <summary>
        /// Removes items that reference positions no longer in Core.Instance.Positions.
        /// This prevents duplicate exposure tracking and unblocks reversals.
        /// Call after ForceClosePositions confirms flatness.
        /// </summary>
        public void PruneOrphanedItems()
        {
            try
            {
                // Get all live position IDs from Core
                var livePositionIds = Core.Instance.Positions
                    .Where(p => p.Symbol == this._symbol && p.Account == this._account)
                    .Select(p => p.Id)
                    .ToHashSet();
                
                // Find items referencing positions that no longer exist in Core
                var orphanedItems = this.Items
                    .OfType<TpSlItemPosition>()
                    .Where(x => !string.IsNullOrEmpty(x.PositionId) && !livePositionIds.Contains(x.PositionId))
                    .ToList();
                
                if (orphanedItems.Any())
                {
                    AppLog.System("TpSlPositionManager", "PruneOrphans", 
                        $"Pruning {orphanedItems.Count} orphaned item(s) with stale position references");
                    
                    foreach (var itm in orphanedItems)
                    {
                        try
                        {
                            itm.ItemClosed -= this.TpSlPositionManager_ItemClosed;
                            this.Items.Remove(itm);
                            this.ClosedItems.Add(itm);
                            AppLog.System("TpSlPositionManager", "PruneOrphans", 
                                $"Pruned item {itm.Id} (Position.Id={itm.Position.Id} no longer exists)");
                        }
                        catch (Exception ex)
                        {
                            AppLog.Error("TpSlPositionManager", "PruneOrphans", 
                                $"Error pruning item {itm.Id}: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("TpSlPositionManager", "PruneOrphans", $"Prune operation failed: {ex.Message}");
            }
        }

        private bool AccountMatches(Account account)
        {
            if (account == null)
                return false;

            if (_account == null)
                return true;

            return string.Equals(account.Id, _account.Id, StringComparison.OrdinalIgnoreCase);
        }

        private bool SymbolMatches(Symbol candidate)
        {
            if (candidate == null)
                return false;

            if (_symbol == null)
                return true;

            if (candidate == _symbol ||
                string.Equals(candidate.Id, _symbol.Id, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate.Name, _symbol.Name, StringComparison.OrdinalIgnoreCase))
                return true;

            if (!string.IsNullOrEmpty(_symbol.Name) && !string.IsNullOrEmpty(candidate.Name) &&
                candidate.Name.StartsWith(_symbol.Name, StringComparison.OrdinalIgnoreCase))
                return true;

            return this.Items.OfType<TpSlItemPosition>().Any(item => item.EntryOrder?.Symbol != null &&
                (item.EntryOrder.Symbol == candidate ||
                 string.Equals(item.EntryOrder.Symbol.Id, candidate.Id, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>
        /// PHASE 2: Update SL with broker verification and force-sync fallback
        /// Modifies the SL order, then verifies broker accepted the change.
        /// If broker mismatch detected, cancels old SL and places new one.
        /// </summary>
        public void UpdateSl(TpSlItemPosition item, double newSlPrice)
        {
            if (item == null || _symbol == null)
                return;

            var position = ResolvePosition(item);
            if (position == null && string.IsNullOrEmpty(item.PositionId))
                return;

            var slOrder = item.GetStopLossOrder(_symbol);
            if (slOrder == null)
            {
                AppLog.System("TpSlPositionManager", "UpdateSl",
                    $"No SL order found for item {item.Id?.Substring(0, 8)}, cannot update");
                return;
            }

            double roundedPrice = _symbol.RoundPriceToTickSize(newSlPrice);
            double currentTrigger = slOrder.TriggerPrice;

            // Sanity check: don't update if already at target
            if (Math.Abs(roundedPrice - currentTrigger) < _symbol.TickSize * 0.1)
                return;

            AppLog.Trading("TpSlPositionManager", "SlPreview",
                $"Item {item.Id?.Substring(0, 8)}: Modifying SL {currentTrigger:F2} → {roundedPrice:F2}");

            try
            {
                // Attempt modify with TriggerPrice ONLY (Stop order semantics)
                var result = Core.Instance.ModifyOrder(
                    slOrder,
                    triggerPrice: roundedPrice,
                    price: double.NaN  // Explicitly no Price for Stop orders
                );

                if (result.Status != TradingOperationResultStatus.Success)
                {
                    AppLog.Error("TpSlPositionManager", "UpdateSl",
                        $"Modify failed for item {item.Id?.Substring(0, 8)}: {result.Message}");
                    
                    // FALLBACK: Cancel and re-place
                    ForceSyncSl(item, roundedPrice, slOrder);
                    return;
                }

                // VERIFICATION: Poll broker to confirm the modify took effect
                System.Threading.Thread.Sleep(250);  // Give broker time to process

                for (int attempt = 0; attempt < 2; attempt++)
                {
                    var verifyOrder = Core.Instance.Orders.FirstOrDefault(o => o.Id == slOrder.Id);
                    if (verifyOrder == null)
                    {
                        AppLog.System("TpSlPositionManager", "UpdateSl",
                            $"SL order {slOrder.Id} disappeared after modify, will re-place");
                        ForceSyncSl(item, roundedPrice, null);
                        return;
                    }

                    double brokerTrigger = verifyOrder.TriggerPrice;
                    double deltaTicks = Math.Abs(_symbol.CalculateTicks(roundedPrice, brokerTrigger));

                    if (deltaTicks <= 1.0)
                    {
                        // Success: broker matches our intent
                        AppLog.Trading("TpSlPositionManager", "UpdateSl",
                            $"✅ SL updated successfully: {roundedPrice:F2} (verified)");
                        return;
                    }

                    if (attempt == 0)
                    {
                        AppLog.System("TpSlPositionManager", "UpdateSl",
                            $"Broker mismatch attempt {attempt + 1}: expected {roundedPrice:F2}, broker {brokerTrigger:F2}, Δ{deltaTicks:F1}t, retrying verification...");
                        System.Threading.Thread.Sleep(250);
                    }
                    else
                    {
                        // Final attempt failed: force-sync
                        AppLog.System("TpSlPositionManager", "SlBrokerMismatch",
                            $"Broker SL mismatch persists: expected {roundedPrice:F2}, broker {brokerTrigger:F2}, Δ{deltaTicks:F1}t");
                        ForceSyncSl(item, roundedPrice, verifyOrder);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("TpSlPositionManager", "UpdateSl",
                    $"Exception during SL update: {ex.Message}");
                ForceSyncSl(item, roundedPrice, slOrder);
            }
        }

        /// <summary>
        /// Force-sync SL by cancelling existing and placing new
        /// </summary>
        private void ForceSyncSl(TpSlItemPosition item, double targetPrice, Order existingOrder)
        {
            if (item == null || _symbol == null)
                return;

            var position = ResolvePosition(item);

            try
            {
                // Cancel existing if present
                if (existingOrder != null)
                {
                    AppLog.System("TpSlPositionManager", "SlForceSync",
                        $"Cancelling stale SL order {existingOrder.Id} at {existingOrder.TriggerPrice:F2}");
                    Core.Instance.CancelOrder(existingOrder);
                    item.StopLossOrderId = null;
                    System.Threading.Thread.Sleep(100);
                }

                // Place new SL at correct price
                double quantity = Math.Abs(position?.Quantity ?? item.Quantity);
                if (quantity <= 0 && item.EntryOrder != null)
                    quantity = Math.Abs(item.EntryOrder.TotalQuantity);

                item.ExpectedSlPrice = targetPrice;
                PlaceOrReplaceStop(item, quantity);

                AppLog.System("TpSlPositionManager", "SlForceSync",
                    $"✅ Placed new SL at {targetPrice:F2} for item {item.Id?.Substring(0, 8)}");
            }
            catch (Exception ex)
            {
                AppLog.Error("TpSlPositionManager", "SlForceSync",
                    $"Failed to force-sync SL: {ex.Message}");
            }
        }
 
    }
}
