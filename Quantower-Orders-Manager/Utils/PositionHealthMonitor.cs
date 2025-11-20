using System;
using System.Collections.Generic;
using System.Linq;
using TradingPlatform.BusinessLayer;
using DivergentStrV0_1.Strategies;

namespace DivergentStrV0_1.Utils
{
    /// <summary>
    /// Continuously monitors position health and auto-repairs missing/incorrect SL/TP
    /// Runs on every tick to ensure no position exists without protection
    /// </summary>
    public class PositionHealthMonitor
    {
        private readonly Symbol _symbol;
        private readonly Account _account;
        private readonly ProtectiveOrderManager _protectiveManager;
        private readonly ISlTpStrategy<SlTpData> _slTpStrategy;

        private readonly Dictionary<string, PositionHealthState> _healthStates = new Dictionary<string, PositionHealthState>();
        private readonly HashSet<string> _alertedPositions = new HashSet<string>();
        private readonly HashSet<string> _emergencyFlattenedPositions = new HashSet<string>();

        private DateTime _lastOrphanCleanup = DateTime.MinValue;
        private const int ORPHAN_CLEANUP_INTERVAL_MS = 2000;  // Cleanup every 2 seconds

        private const int MAX_REPAIR_ATTEMPTS = 3;
        private const int EMERGENCY_FLATTEN_THRESHOLD_MS = 10000;  // 10 seconds without protection

        public PositionHealthMonitor(
            Symbol symbol,
            Account account,
            ProtectiveOrderManager protectiveManager,
            ISlTpStrategy<SlTpData> slTpStrategy)
        {
            _symbol = symbol ?? throw new ArgumentNullException(nameof(symbol));
            _account = account ?? throw new ArgumentNullException(nameof(account));
            _protectiveManager = protectiveManager ?? throw new ArgumentNullException(nameof(protectiveManager));
            _slTpStrategy = slTpStrategy ?? throw new ArgumentNullException(nameof(slTpStrategy));
        }

        /// <summary>
        /// Check all positions and repair any issues (call on every tick)
        /// </summary>
        public HealthCheckResult CheckAndRepair(SlTpData marketData)
        {
            var result = new HealthCheckResult();

            // PHASE 1: Periodic orphan cleanup
            if ((DateTime.UtcNow - _lastOrphanCleanup).TotalMilliseconds > ORPHAN_CLEANUP_INTERVAL_MS)
            {
                int orphansRemoved = _protectiveManager.CleanupOrphanedOrders();
                result.OrphansRemoved = orphansRemoved;
                _lastOrphanCleanup = DateTime.UtcNow;

                if (orphansRemoved > 0)
                {
                    AppLog.System("PositionHealthMonitor", "OrphanCleanup",
                        $"Removed {orphansRemoved} orphaned protective order(s)");
                }
            }

            // PHASE 2: Get all active positions
            var activePositions = Core.Instance.Positions
                .Where(p => p.Symbol == _symbol && p.Account == _account)
                .ToList();

            if (!activePositions.Any())
            {
                // No positions - clear health tracking
                _healthStates.Clear();
                _alertedPositions.Clear();
                return result;
            }

            result.TotalPositions = activePositions.Count;

            // PHASE 3: Check each position
            foreach (var position in activePositions)
            {
                var healthState = GetOrCreateHealthState(position.Id);
                healthState.LastCheckTime = DateTime.UtcNow;

                // Validate protection
                var validation = _protectiveManager.ValidateProtection(position);

                if (validation.IsValid)
                {
                    // Position is healthy
                    result.HealthyPositions++;
                    healthState.IsHealthy = true;
                    healthState.RepairAttempts = 0;
                    _alertedPositions.Remove(position.Id);
                    continue;
                }

                // Position needs repair
                result.UnhealthyPositions++;
                healthState.IsHealthy = false;

                // Log alert (once per position)
                if (_alertedPositions.Add(position.Id))
                {
                    AppLog.Error("PositionHealthMonitor", "UnhealthyPosition",
                        $"⚠️ Position {position.Id} unhealthy: {validation.Message}");
                }

                // Check if we should attempt repair
                if (healthState.RepairAttempts >= MAX_REPAIR_ATTEMPTS)
                {
                    // Max repair attempts reached
                    var timeSinceCreation = (DateTime.UtcNow - healthState.CreationTime).TotalMilliseconds;

                    if (timeSinceCreation > EMERGENCY_FLATTEN_THRESHOLD_MS &&
                        !_emergencyFlattenedPositions.Contains(position.Id))
                    {
                        // Emergency flatten
                        AppLog.Error("PositionHealthMonitor", "EmergencyFlatten",
                            $"🚨 EMERGENCY: Position {position.Id} unprotected for {timeSinceCreation:F0}ms, flattening!");

                        EmergencyFlattenPosition(position);
                        _emergencyFlattenedPositions.Add(position.Id);
                        result.EmergencyFlattens++;
                    }

                    continue;
                }

                // Attempt repair
                AppLog.System("PositionHealthMonitor", "RepairAttempt",
                    $"Attempting repair for position {position.Id} (attempt {healthState.RepairAttempts + 1}/{MAX_REPAIR_ATTEMPTS})");

                bool repaired = AttemptRepair(position, validation, marketData);

                if (repaired)
                {
                    result.RepairsSucceeded++;
                    healthState.RepairAttempts = 0;
                    healthState.IsHealthy = true;
                    AppLog.System("PositionHealthMonitor", "RepairSuccess",
                        $"✅ Successfully repaired position {position.Id}");
                }
                else
                {
                    result.RepairsFailed++;
                    healthState.RepairAttempts++;
                    AppLog.Error("PositionHealthMonitor", "RepairFailed",
                        $"❌ Failed to repair position {position.Id} (attempt {healthState.RepairAttempts}/{MAX_REPAIR_ATTEMPTS})");
                }
            }

            // PHASE 4: Cleanup health states for closed positions
            var activePositionIds = activePositions.Select(p => p.Id).ToHashSet();
            var closedPositionIds = _healthStates.Keys
                .Where(id => !activePositionIds.Contains(id))
                .ToList();

            foreach (var closedId in closedPositionIds)
            {
                _healthStates.Remove(closedId);
                _alertedPositions.Remove(closedId);
                _emergencyFlattenedPositions.Remove(closedId);
            }

            return result;
        }

        /// <summary>
        /// Force validation of all positions (call after major events like reversals)
        /// </summary>
        public void ForceValidation()
        {
            _alertedPositions.Clear();
            _lastOrphanCleanup = DateTime.MinValue;  // Force orphan cleanup
        }

        /// <summary>
        /// Reset health tracking (call after strategy restart)
        /// </summary>
        public void Reset()
        {
            _healthStates.Clear();
            _alertedPositions.Clear();
            _emergencyFlattenedPositions.Clear();
            _lastOrphanCleanup = DateTime.MinValue;
        }

        #region Private Helpers

        private PositionHealthState GetOrCreateHealthState(string positionId)
        {
            if (!_healthStates.TryGetValue(positionId, out var state))
            {
                state = new PositionHealthState
                {
                    PositionId = positionId,
                    CreationTime = DateTime.UtcNow
                };
                _healthStates[positionId] = state;
            }
            return state;
        }

        private bool AttemptRepair(Position position, ValidationResult validation, SlTpData marketData)
        {
            bool slPlaced = true;
            bool tpPlaced = true;

            // Calculate SL/TP based on current market data
            double entryPrice = position.OpenPrice;
            marketData.currentPrice = position.CurrentPrice ?? entryPrice;

            // Set correct SlTriggerPrice based on position side
            if (position.Side == Side.Buy)
                marketData.SlTriggerPrice = marketData.PreviousLow;
            else
                marketData.SlTriggerPrice = marketData.PreviousHigh;

            // Place missing SL
            if (!validation.HasStopLoss)
            {
                var slPrices = _slTpStrategy.CalculateSl(marketData, position.Side, entryPrice);

                if (slPrices != null && slPrices.Count > 0 && !double.IsNaN(slPrices[0]))
                {
                    double slPrice = _symbol.RoundPriceToTickSize(slPrices[0]);

                    AppLog.System("PositionHealthMonitor", "PlaceMissingSL",
                        $"Placing missing SL for position {position.Id} at {slPrice:F2}");

                    var slResult = _protectiveManager.PlaceStopLoss(
                        position,
                        slPrice,
                        $"REPAIR_SL_{DateTime.UtcNow.Ticks}");

                    slPlaced = slResult.Success;

                    if (!slPlaced)
                    {
                        AppLog.Error("PositionHealthMonitor", "SlPlaceFailed",
                            $"Failed to place SL: {slResult.Message}");
                    }
                }
                else
                {
                    AppLog.Error("PositionHealthMonitor", "SlCalcFailed",
                        "Cannot calculate SL price for repair");
                    slPlaced = false;
                }
            }

            // Place missing TP
            if (!validation.HasTakeProfit)
            {
                var tpPrices = _slTpStrategy.CalculateTp(marketData, position.Side, entryPrice);

                if (tpPrices != null && tpPrices.Count > 0 && !double.IsNaN(tpPrices[0]))
                {
                    double tpPrice = _symbol.RoundPriceToTickSize(tpPrices[0]);

                    AppLog.System("PositionHealthMonitor", "PlaceMissingTP",
                        $"Placing missing TP for position {position.Id} at {tpPrice:F2}");

                    var tpResult = _protectiveManager.PlaceTakeProfit(
                        position,
                        tpPrice,
                        $"REPAIR_TP_{DateTime.UtcNow.Ticks}");

                    tpPlaced = tpResult.Success;

                    if (!tpPlaced)
                    {
                        AppLog.Error("PositionHealthMonitor", "TpPlaceFailed",
                            $"Failed to place TP: {tpResult.Message}");
                    }
                }
                else
                {
                    AppLog.Error("PositionHealthMonitor", "TpCalcFailed",
                        "Cannot calculate TP price for repair");
                    tpPlaced = false;
                }
            }

            return slPlaced && tpPlaced;
        }

        private void EmergencyFlattenPosition(Position position)
        {
            try
            {
                var marketOrderType = _symbol.GetAlowedOrderTypes(OrderTypeUsage.Order)
                    .FirstOrDefault(x => x.Behavior == OrderTypeBehavior.Market);

                if (marketOrderType == null)
                {
                    AppLog.Error("PositionHealthMonitor", "EmergencyFlatten",
                        "No market order type available for emergency flatten");
                    return;
                }

                Side exitSide = position.Side == Side.Buy ? Side.Sell : Side.Buy;

                var request = new PlaceOrderRequestParameters
                {
                    Symbol = _symbol,
                    Account = _account,
                    Side = exitSide,
                    Quantity = position.Quantity,
                    OrderTypeId = marketOrderType.Id,
                    PositionId = position.Id,
                    Comment = $"EMERGENCY_FLATTEN_{DateTime.UtcNow.Ticks}"
                };

                var result = Core.Instance.PlaceOrder(request);

                if (result.Status == TradingOperationResultStatus.Success)
                {
                    AppLog.System("PositionHealthMonitor", "EmergencyFlatten",
                        $"Emergency flatten order placed for position {position.Id}: {result.OrderId}");
                }
                else
                {
                    AppLog.Error("PositionHealthMonitor", "EmergencyFlatten",
                        $"Failed to place emergency flatten order: {result.Message}");
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("PositionHealthMonitor", "EmergencyFlatten",
                    $"Exception during emergency flatten: {ex.Message}");
            }
        }

        #endregion
    }

    #region Helper Classes

    internal class PositionHealthState
    {
        public string PositionId { get; set; }
        public DateTime CreationTime { get; set; }
        public DateTime LastCheckTime { get; set; }
        public bool IsHealthy { get; set; }
        public int RepairAttempts { get; set; }
    }

    public class HealthCheckResult
    {
        public int TotalPositions { get; set; }
        public int HealthyPositions { get; set; }
        public int UnhealthyPositions { get; set; }
        public int RepairsSucceeded { get; set; }
        public int RepairsFailed { get; set; }
        public int EmergencyFlattens { get; set; }
        public int OrphansRemoved { get; set; }

        public bool AllHealthy => TotalPositions > 0 && UnhealthyPositions == 0;

        public override string ToString()
        {
            return $"Positions: {TotalPositions} total, {HealthyPositions} healthy, {UnhealthyPositions} unhealthy | " +
                   $"Repairs: {RepairsSucceeded} ok, {RepairsFailed} failed | " +
                   $"Emergency: {EmergencyFlattens} | Orphans: {OrphansRemoved}";
        }
    }

    #endregion
}
