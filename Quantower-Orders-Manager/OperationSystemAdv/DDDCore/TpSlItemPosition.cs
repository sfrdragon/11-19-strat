using DivergentStrV0_1.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using TradingPlatform.BusinessLayer;

namespace DivergentStrV0_1.OperationSystemAdv.DDDCore
{
    public class TpSlItemPosition : ITpSlItems
    {
        public double ClosedQuantity
        {
            get
            {
                if (this.Position != null && this.EntryOrder != null)
                    return this.EntryOrder.FilledQuantity - this.Position.Quantity;
                else
                    return 0;
            }
        }

        public Order EntryOrder { get; private set; }

        public List<Trade> EntryTrades { get; private set; }

        public List<Trade> ExitTrades{ get; private set; }

        public bool Exposed => this.Position != null && this.Position.Quantity != 0;

        public double ExposedQuantity
        {
            get
            {
                if (this.Position != null)
                    return Math.Abs(this.Position.Quantity);
                else
                    return 0;
            }
        }

        public double Fees => this.Position != null ? this.Position.Fee.Value : 0;

        public double FilledQuantity
        {
            get 
            { 
                return this.EntryOrder != null ? Core.Instance.Orders.FirstOrDefault(x => x.Id == this.EntryOrder.Id).FilledQuantity : 
                    this.Position != null && Math.Abs(this.Position.Quantity) > 0 ? Math.Abs(this.Position.Quantity) : 0;
            }
        }

        public double GrossProfit
        {
            get
            {
                if (this.Position != null && this.Position.GrossPnL != null)
                    return this.Position.GrossPnL.Value;
                else
                    return 0;
            }
        }

        public string Id { get; private set; }

        public double NetProfit
        {
            get
            {

                #region 🧪 HACK [Soluzione temporanea]
                // evito i NetPnl nulli tentando di capire se sono loro a lanciare un eccezzione che cancella gli ordini
                #endregion

                if (this.Position != null)
                   return this.Position.NetPnL != null ? this.Position.NetPnL.Value : 0;
                else
                    return 0;
            }
        }

        public Position Position { get; private set; }
        public string PositionId { get; private set; }

        public double Quantity { get; private set; }

        public Side Side { get; private set; } 

        private PositionManagerStatus _status = PositionManagerStatus.Created;
        
        // CRITICAL: Track SL/TP order IDs since Position.StopLoss/TakeProfit are NULL in Quantower
        public string StopLossOrderId { get; set; }
        public string TakeProfitOrderId { get; set; }
        
        // Local state cache for SL/TP tracking
        private static long _versionSeed;
        private double _expectedSlPrice = double.NaN;
        private double _expectedTpPrice = double.NaN;
        private DateTime _plannedSlUpdatedUtc = DateTime.MinValue;
        private DateTime _plannedTpUpdatedUtc = DateTime.MinValue;
        public double PendingSlTarget { get; internal set; } = double.NaN;
        public double PendingTpTarget { get; internal set; } = double.NaN;

        public double ExpectedSlPrice
        {
            get => _expectedSlPrice;
            set
            {
                _expectedSlPrice = value;
                _plannedSlUpdatedUtc = DateTime.UtcNow;
                Touch();
            }
        }

        public double ExpectedTpPrice
        {
            get => _expectedTpPrice;
            set
            {
                _expectedTpPrice = value;
                _plannedTpUpdatedUtc = DateTime.UtcNow;
                Touch();
            }
        }

        public long StateVersion { get; private set; }
        public DateTime LastHealthCheckUtc { get; set; } = DateTime.MinValue;
        public DateTime PlannedSlUpdatedUtc => _plannedSlUpdatedUtc;
        public DateTime PlannedTpUpdatedUtc => _plannedTpUpdatedUtc;
        public DateTime LastStopPlacementUtc { get; private set; } = DateTime.MinValue;
        public DateTime LastTpPlacementUtc { get; private set; } = DateTime.MinValue;
        
        // Lazy-resolved order references with 1-second cache
        private Order _cachedSlOrder;
        private Order _cachedTpOrder;
        private DateTime _slOrderLastChecked = DateTime.MinValue;
        private DateTime _tpOrderLastChecked = DateTime.MinValue;
        
        // Validation state
        public bool SlOrderValidated { get; set; }
        public bool TpOrderValidated { get; set; }

        public void SetEntryOrder(Order order)
        {
            this.EntryOrder = order;
            this.Quantity = order.TotalQuantity;
            this.Side = order.Side;
            Touch();
        }

        public void SetPosition(Position position)
        {
            if (position == null || Math.Abs(position.Quantity) < 1e-8)
            {
                this.Position = null;
                this.PositionId = null;

                this.TryUpdateStatus(true);
                return;
            }

            this.Position = position;
            this.PositionId = position.Id;
            Touch();
        }

        public void PlanStopPrice(double price)
        {
            this.ExpectedSlPrice = price;
            this.PendingSlTarget = price;
            this.StopLossOrderId = string.IsNullOrEmpty(this.StopLossOrderId) ? null : this.StopLossOrderId;
        }

        public void PlanTakeProfitPrice(double price)
        {
            this.ExpectedTpPrice = price;
            this.PendingTpTarget = price;
            this.TakeProfitOrderId = string.IsNullOrEmpty(this.TakeProfitOrderId) ? null : this.TakeProfitOrderId;
        }

        public void ResetProtectiveOrders()
        {
            this.StopLossOrderId = null;
            this.TakeProfitOrderId = null;
            this.ExpectedSlPrice = double.NaN;
            this.ExpectedTpPrice = double.NaN;
            this.PendingSlTarget = double.NaN;
            this.PendingTpTarget = double.NaN;
            this._cachedSlOrder = null;
            this._cachedTpOrder = null;
            this.LastStopPlacementUtc = DateTime.MinValue;
            this.LastTpPlacementUtc = DateTime.MinValue;
            this.SlOrderValidated = false;
            this.TpOrderValidated = false;
        }

        public void MarkStopPlacement()
        {
            LastStopPlacementUtc = DateTime.UtcNow;
        }

        public void MarkTpPlacement()
        {
            LastTpPlacementUtc = DateTime.UtcNow;
        }


        #region 🐞 BUG [Bug noto da risolvere #6]
        //Lo stato non puo aggiornarsi correttamente perche usa proprieta nn tracciate 
        #endregion

        public PositionManagerStatus Status
        {
            get
            {
                if (this.EntryOrder != null && this.Position == null)
                {
                    bool hasPendingExitOrders = !string.IsNullOrEmpty(this.StopLossOrderId) || !string.IsNullOrEmpty(this.TakeProfitOrderId);
                    return hasPendingExitOrders ? PositionManagerStatus.Placed : PositionManagerStatus.Closed;
                }
                else if (this.EntryOrder != null && this.Position != null)
                {
                    var pos = Core.Instance.Positions.FirstOrDefault(x => x.Id == this.Position.Id);
                    if (pos != null && pos.Quantity != 0)
                    {
                        this.Position = pos;
                        this.PositionId = pos.Id;
                        switch (Core.Instance.Orders.Any(x => x.Id == this.EntryOrder.Id))
                        {
                            case true:
                                this.EntryOrder = Core.Instance.Orders.FirstOrDefault(x => x.Id == this.EntryOrder.Id);
                                return PositionManagerStatus.PartialyFilled;
                            case false:
                                if (pos.Quantity < this.EntryOrder.TotalQuantity)
                                    return PositionManagerStatus.PartialyClosed;
                                else
                                    return PositionManagerStatus.Filled;
                        }
                    }
                    else
                        return PositionManagerStatus.Closed;
                }
                else
                    return PositionManagerStatus.Created;
            }
        }

        public Symbol Symbol { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

        public event EventHandler<PositionManagerStatus[]> ItemClosed;
        public event EventHandler QuitAll;

        public TpSlItemPosition(string id)
        {
            this.Id = id;

            EntryTrades = new List<Trade>();
            ExitTrades = new List<Trade>();

        }

        public Order GetStopLossOrder(Symbol symbol)
        {
            if (symbol == null)
                return null;

            // Cache valid for 1 second
            if (_cachedSlOrder != null &&
                (DateTime.UtcNow - _slOrderLastChecked).TotalSeconds < 1 &&
                (_cachedSlOrder.Status == OrderStatus.Opened || _cachedSlOrder.Status == OrderStatus.PartiallyFilled))
                return _cachedSlOrder;

            Order slOrder = null;

            // Priority 1: Tracked ID
            if (!string.IsNullOrEmpty(StopLossOrderId))
                slOrder = Core.Instance.Orders.FirstOrDefault(o => o.Id == StopLossOrderId);

            // Priority 2: PositionId + Behavior + Price proximity
            if (slOrder == null && Position != null && !double.IsNaN(ExpectedSlPrice))
            {
                slOrder = Core.Instance.Orders.FirstOrDefault(o =>
                    o.PositionId == Position.Id &&
                    o.OrderType?.Behavior == OrderTypeBehavior.Stop &&
                    Math.Abs(o.TriggerPrice - ExpectedSlPrice) < symbol.TickSize * 2 &&
                    (o.Status == OrderStatus.Opened || o.Status == OrderStatus.PartiallyFilled)
                );
            }

            // Priority 3: Side + Price proximity (no PositionId required)
            if (slOrder == null && !double.IsNaN(ExpectedSlPrice))
            {
                slOrder = Core.Instance.Orders.FirstOrDefault(o =>
                    o.Side != this.Side &&  // SL is opposite side
                    o.OrderType?.Behavior == OrderTypeBehavior.Stop &&
                    Math.Abs(o.TriggerPrice - ExpectedSlPrice) < symbol.TickSize * 2 &&
                    (o.Status == OrderStatus.Opened || o.Status == OrderStatus.PartiallyFilled)
                );
            }

            _cachedSlOrder = slOrder;
            _slOrderLastChecked = DateTime.UtcNow;

            // Update tracked ID if found via fallback
            if (slOrder != null && string.IsNullOrEmpty(StopLossOrderId))
                StopLossOrderId = slOrder.Id;

            return slOrder;
        }

        public Order GetTakeProfitOrder(Symbol symbol)
        {
            if (symbol == null)
                return null;

            // Cache valid for 1 second
            if (_cachedTpOrder != null &&
                (DateTime.UtcNow - _tpOrderLastChecked).TotalSeconds < 1 &&
                (_cachedTpOrder.Status == OrderStatus.Opened || _cachedTpOrder.Status == OrderStatus.PartiallyFilled))
                return _cachedTpOrder;

            Order tpOrder = null;

            // Priority 1: Tracked ID
            if (!string.IsNullOrEmpty(TakeProfitOrderId))
                tpOrder = Core.Instance.Orders.FirstOrDefault(o => o.Id == TakeProfitOrderId);

            // Priority 2: PositionId + Behavior + Price proximity
            if (tpOrder == null && Position != null && !double.IsNaN(ExpectedTpPrice))
            {
                tpOrder = Core.Instance.Orders.FirstOrDefault(o =>
                    o.PositionId == Position.Id &&
                    o.OrderType?.Behavior == OrderTypeBehavior.Limit &&
                    Math.Abs(o.Price - ExpectedTpPrice) < symbol.TickSize * 2 &&
                    (o.Status == OrderStatus.Opened || o.Status == OrderStatus.PartiallyFilled)
                );
            }

            // Priority 3: Side + Price proximity (no PositionId required)
            if (tpOrder == null && !double.IsNaN(ExpectedTpPrice))
            {
                tpOrder = Core.Instance.Orders.FirstOrDefault(o =>
                    o.Side != this.Side &&  // TP is opposite side
                    o.OrderType?.Behavior == OrderTypeBehavior.Limit &&
                    Math.Abs(o.Price - ExpectedTpPrice) < symbol.TickSize * 2 &&
                    (o.Status == OrderStatus.Opened || o.Status == OrderStatus.PartiallyFilled)
                );
            }

            _cachedTpOrder = tpOrder;
            _tpOrderLastChecked = DateTime.UtcNow;

            // Update tracked ID if found via fallback
            if (tpOrder != null && string.IsNullOrEmpty(TakeProfitOrderId))
                TakeProfitOrderId = tpOrder.Id;

            return tpOrder;
        }

        public void Quit()
        {
            AppLog.System("TpSlItemPosition", "QuitDiag",
                $"Quit called for item {this.Id}, Position={this.Position?.Id ?? this.PositionId}, SL_ID={this.StopLossOrderId}, TP_ID={this.TakeProfitOrderId}");

            Position activePos = this.Position;
            if (activePos == null && !string.IsNullOrEmpty(this.PositionId))
            {
                activePos = Core.Instance.Positions.FirstOrDefault(x => x.Id == this.PositionId);
                if (activePos != null)
                    this.Position = activePos;
            }

            if (activePos != null)
            {
                try
                {
                    activePos.Close();
                    AppLog.System("TpSlItemPosition", "Quit", $"Position {activePos.Id} close request sent");
                }
                catch (Exception ex)
                {
                    AppLog.Error("TpSlItemPosition", "Quit", $"Failed to close position {activePos.Id}: {ex.Message}");
                }
            }
            else
            {
                AppLog.System("TpSlItemPosition", "QuitDiag", "No active position found to close");
            }

            // Helper: Cancel SL using all available methods
            Order slOrder = null;

            // Method 1: Tracked ID
            if (!string.IsNullOrEmpty(this.StopLossOrderId))
                slOrder = Core.Instance.Orders.FirstOrDefault(o => o.Id == this.StopLossOrderId);

            // Method 2: Position.StopLoss
            if (slOrder == null)
                slOrder = Core.Instance.Orders.FirstOrDefault(o => o.Id == (this.Position?.StopLoss?.Id ?? activePos?.StopLoss?.Id));

            // Method 3: Fuzzy match by expected price
            if (slOrder == null && !double.IsNaN(this.ExpectedSlPrice))
            {
                slOrder = Core.Instance.Orders.FirstOrDefault(o =>
                    o.OrderType?.Behavior == OrderTypeBehavior.Stop &&
                    Math.Abs(o.TriggerPrice - this.ExpectedSlPrice) < 1.0 &&  // Within 1 point
                    (o.Status == OrderStatus.Opened || o.Status == OrderStatus.PartiallyFilled)
                );

                if (slOrder != null)
                    AppLog.System("TpSlItemPosition", "Quit", $"Found SL by price: {slOrder.Id} at {slOrder.TriggerPrice:F2}");
            }

            if (slOrder != null && (slOrder.Status == OrderStatus.Opened || slOrder.Status == OrderStatus.PartiallyFilled))
            {
                try
                {
                    slOrder.Cancel();
                    AppLog.System("TpSlItemPosition", "Quit", $"Cancelled SL order {slOrder.Id}");
                }
                catch (Exception ex)
                {
                    AppLog.Error("TpSlItemPosition", "Quit", $"SL cancel failed: {ex.Message}");
                }
            }
            else if (slOrder != null)
            {
                AppLog.System("TpSlItemPosition", "QuitDiag", $"SL order {slOrder.Id} status {slOrder.Status}, no cancel needed");
            }

            // Helper: Cancel TP using all available methods
            Order tpOrder = null;

            // Method 1: Tracked ID
            if (!string.IsNullOrEmpty(this.TakeProfitOrderId))
                tpOrder = Core.Instance.Orders.FirstOrDefault(o => o.Id == this.TakeProfitOrderId);

            // Method 2: Position.TakeProfit
            if (tpOrder == null)
                tpOrder = Core.Instance.Orders.FirstOrDefault(o => o.Id == (this.Position?.TakeProfit?.Id ?? activePos?.TakeProfit?.Id));

            // Method 3: Fuzzy match by expected price
            if (tpOrder == null && !double.IsNaN(this.ExpectedTpPrice))
            {
                tpOrder = Core.Instance.Orders.FirstOrDefault(o =>
                    o.OrderType?.Behavior == OrderTypeBehavior.Limit &&
                    Math.Abs(o.Price - this.ExpectedTpPrice) < 1.0 &&  // Within 1 point
                    (o.Status == OrderStatus.Opened || o.Status == OrderStatus.PartiallyFilled)
                );

                if (tpOrder != null)
                    AppLog.System("TpSlItemPosition", "Quit", $"Found TP by price: {tpOrder.Id} at {tpOrder.Price:F2}");
            }

            if (tpOrder != null && (tpOrder.Status == OrderStatus.Opened || tpOrder.Status == OrderStatus.PartiallyFilled))
            {
                try
                {
                    tpOrder.Cancel();
                    AppLog.System("TpSlItemPosition", "Quit", $"Cancelled TP order {tpOrder.Id}");
                }
                catch (Exception ex)
                {
                    AppLog.Error("TpSlItemPosition", "Quit", $"TP cancel failed: {ex.Message}");
                }
            }
            else if (tpOrder != null)
            {
                AppLog.System("TpSlItemPosition", "QuitDiag", $"TP order {tpOrder.Id} status {tpOrder.Status}, no cancel needed");
            }

            // Step 3: GroupId scan fallback - cancel any remaining OCO children linked to entry
            try
            {
                var entryTokens = SplitGroupIds(this.EntryOrder?.GroupId).ToHashSet();

                if (entryTokens.Count > 0)
                {
                    var groupOrders = Core.Instance.Orders
                        .Where(o =>
                        {
                            if (string.IsNullOrEmpty(o.GroupId)) return false;
                            if (o.Id == this.EntryOrder.Id) return false;
                            if (o.Status != OrderStatus.Opened && o.Status != OrderStatus.PartiallyFilled) return false;

                            var tokens = SplitGroupIds(o.GroupId);
                            return tokens.Any(token => entryTokens.Contains(token));
                        })
                        .ToList();

                    foreach (var go in groupOrders)
                    {
                        try
                        {
                            go.Cancel();
                            AppLog.System("TpSlItemPosition", "Quit",
                                $"Cancelled OCO order {go.Id} ({go.OrderType?.Behavior}) with GroupId={go.GroupId}");
                        }
                        catch (Exception ex)
                        {
                            AppLog.Error("TpSlItemPosition", "Quit",
                                $"Failed to cancel OCO order {go.Id}: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("TpSlItemPosition", "Quit", $"GroupId scan cancellation error: {ex.Message}");
            }
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

        public void TryUpdateStatus(bool force = false)
        {
            var oldStatus = _status;
            _status = this.Status;
 
 
            if (oldStatus != _status || force)
            {
                AppLog.Trading("TpSlItemPosition", "StatusChange", $"PositionManager {Id} status changed to {_status}");
                if (_status == PositionManagerStatus.Closed)
                    ItemClosed?.Invoke(this, new PositionManagerStatus[2] { oldStatus, _status });
 
            }
        }
 
        public void TryUpdateStatus()
        {
            TryUpdateStatus(false);
        }

        public TpSlItems2 TryUpdateTrade(Trade trade)
        {
            throw new NotImplementedException();
        }

        public void UpdateSlOrders(Func<double, double> updateFunction)
        {
            throw new NotImplementedException();
        }

        public void UpdateTpOrders(Func<double, double> updateFunction)
        {
            throw new NotImplementedException();
        }

        public void Touch()
        {
            StateVersion = Interlocked.Increment(ref _versionSeed);
        }
    }
}

