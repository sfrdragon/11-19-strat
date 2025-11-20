using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using TradingPlatform.BusinessLayer;
using DivergentStrV0_1.OperationSystemAdv.DDDCore;
using DivergentStrV0_1.Strategies;

namespace DivergentStrV0_1.Utils
{
    /// <summary>
    /// Manages atomic position reversals with guaranteed completion or rollback
    /// Ensures old position closes, new position opens, and all SL/TP orders are correctly placed
    /// </summary>
    public class AtomicReversalManager
    {
        private readonly Symbol _symbol;
        private readonly Account _account;
        private readonly ProtectiveOrderManager _protectiveManager;
        private readonly TpSlPositionManager _positionManager;
        private readonly ISlTpStrategy<SlTpData> _slTpStrategy;

        private const int MAX_COMPLETION_WAIT_MS = 5000;
        private const int POLL_INTERVAL_MS = 100;

        public AtomicReversalManager(
            Symbol symbol,
            Account account,
            ProtectiveOrderManager protectiveManager,
            TpSlPositionManager positionManager,
            ISlTpStrategy<SlTpData> slTpStrategy)
        {
            _symbol = symbol ?? throw new ArgumentNullException(nameof(symbol));
            _account = account ?? throw new ArgumentNullException(nameof(account));
            _protectiveManager = protectiveManager ?? throw new ArgumentNullException(nameof(protectiveManager));
            _positionManager = positionManager ?? throw new ArgumentNullException(nameof(positionManager));
            _slTpStrategy = slTpStrategy ?? throw new ArgumentNullException(nameof(slTpStrategy));
        }

        /// <summary>
        /// Execute atomic reversal: close current position and open opposite position
        /// </summary>
        public ReversalResult ExecuteReversal(
            Side newSide,
            double quantity,
            SlTpData marketData,
            string comment,
            Func<string, TradingOperationResult> placeOrderFunc)
        {
            var result = new ReversalResult { NewSide = newSide, TargetQuantity = quantity };

            AppLog.Trading("AtomicReversalManager", "BeginReversal",
                $"═══════════════════════════════════════════════════════════");
            AppLog.Trading("AtomicReversalManager", "BeginReversal",
                $"Starting atomic reversal to {newSide} {quantity} contracts");

            // PHASE 1: Capture current state
            var currentPositions = GetCurrentPositions();
            if (!currentPositions.Any())
            {
                result.Message = "No existing positions to reverse";
                AppLog.Error("AtomicReversalManager", "NoPosition", result.Message);
                return result;
            }

            Side oldSide = currentPositions[0].Side;
            double oldQuantity = currentPositions.Sum(p => p.Quantity);

            AppLog.Trading("AtomicReversalManager", "CurrentState",
                $"Current: {oldSide} {oldQuantity} contracts across {currentPositions.Count} position(s)");

            // Capture old SL/TP order IDs for cleanup
            var oldProtectiveOrders = GetProtectiveOrdersForPositions(currentPositions);
            AppLog.Trading("AtomicReversalManager", "OldOrders",
                $"Found {oldProtectiveOrders.Count} old protective order(s) to cleanup");

            // PHASE 2: Cancel old SL/TP FIRST (prevent false triggers during reversal)
            AppLog.Trading("AtomicReversalManager", "CancelOld",
                "Cancelling old protective orders...");
            CancelProtectiveOrders(oldProtectiveOrders, "Reversal - closing position");

            // Give broker time to process cancellations
            Thread.Sleep(200);

            // PHASE 3: Place reversal market order
            AppLog.Trading("AtomicReversalManager", "PlaceReversal",
                $"Placing reversal market order: {newSide} {quantity}");

            var orderResult = placeOrderFunc(comment);

            if (orderResult.Status != TradingOperationResultStatus.Success)
            {
                result.Message = $"Failed to place reversal order: {orderResult.Message}";
                AppLog.Error("AtomicReversalManager", "OrderFailed", result.Message);
                return result;
            }

            result.ReversalOrderId = orderResult.OrderId;
            AppLog.Trading("AtomicReversalManager", "OrderPlaced",
                $"Reversal order placed: {orderResult.OrderId}");

            // PHASE 4: Wait for position to reverse
            AppLog.Trading("AtomicReversalManager", "WaitForFill",
                "Waiting for reversal to complete...");

            var newPosition = WaitForNewPosition(newSide, quantity);

            if (newPosition == null)
            {
                result.Message = "Timeout waiting for new position to appear";
                AppLog.Error("AtomicReversalManager", "Timeout", result.Message);

                // Emergency cleanup
                CleanupFailedReversal(oldSide);
                return result;
            }

            result.NewPosition = newPosition;
            AppLog.Trading("AtomicReversalManager", "PositionCreated",
                $"New position created: {newPosition.Id}, {newPosition.Side} {newPosition.Quantity} @ {newPosition.OpenPrice:F2}");

            // PHASE 5: Verify old position fully closed
            Thread.Sleep(300);  // Give broker time to update
            var remainingOldPositions = GetPositionsForSide(oldSide);

            if (remainingOldPositions.Any())
            {
                AppLog.Error("AtomicReversalManager", "OldPositionRemaining",
                    $"WARNING: {remainingOldPositions.Count} old {oldSide} position(s) still exist!");

                // Try to flatten remaining old positions
                EmergencyFlattenPositions(remainingOldPositions, "Reversal cleanup");
            }

            // PHASE 6: Calculate new SL/TP based on actual fill price
            AppLog.Trading("AtomicReversalManager", "CalculateSLTP",
                "Calculating SL/TP for new position...");

            double entryPrice = newPosition.OpenPrice;
            marketData.currentPrice = entryPrice;

            // Set correct SlTriggerPrice for the new side
            if (newSide == Side.Buy)
                marketData.SlTriggerPrice = marketData.PreviousLow;
            else
                marketData.SlTriggerPrice = marketData.PreviousHigh;

            var slPrices = _slTpStrategy.CalculateSl(marketData, newSide, entryPrice);
            var tpPrices = _slTpStrategy.CalculateTp(marketData, newSide, entryPrice);

            if (slPrices == null || slPrices.Count == 0 || double.IsNaN(slPrices[0]))
            {
                result.Message = "Failed to calculate SL price";
                AppLog.Error("AtomicReversalManager", "SlCalcFailed", result.Message);

                // Emergency: close new position if we can't protect it
                EmergencyFlattenPositions(new List<Position> { newPosition }, "Cannot calculate SL");
                return result;
            }

            if (tpPrices == null || tpPrices.Count == 0 || double.IsNaN(tpPrices[0]))
            {
                result.Message = "Failed to calculate TP price";
                AppLog.Error("AtomicReversalManager", "TpCalcFailed", result.Message);

                // Emergency: close new position if we can't calculate TP
                EmergencyFlattenPositions(new List<Position> { newPosition }, "Cannot calculate TP");
                return result;
            }

            double newSlPrice = _symbol.RoundPriceToTickSize(slPrices[0]);
            double newTpPrice = _symbol.RoundPriceToTickSize(tpPrices[0]);

            AppLog.Trading("AtomicReversalManager", "ProtectiveOrders",
                $"Placing protective orders: SL={newSlPrice:F2}, TP={newTpPrice:F2}");

            // PHASE 7: Place new SL/TP with validation
            var bracketResult = _protectiveManager.PlaceBracket(
                newPosition,
                newSlPrice,
                newTpPrice,
                comment);

            if (!bracketResult.Success)
            {
                result.Message = $"Failed to place protective orders: {bracketResult.Message}";
                AppLog.Error("AtomicReversalManager", "BracketFailed", result.Message);

                // CRITICAL: Emergency flatten if we can't protect the position
                AppLog.Error("AtomicReversalManager", "EmergencyFlatten",
                    "Cannot protect new position, emergency flattening!");
                EmergencyFlattenPositions(new List<Position> { newPosition }, "Failed to place SL/TP");
                return result;
            }

            result.StopLossOrderId = bracketResult.StopLossOrderId;
            result.TakeProfitOrderId = bracketResult.TakeProfitOrderId;

            // PHASE 8: Final validation
            Thread.Sleep(200);  // Give broker time to update
            var validation = _protectiveManager.ValidateProtection(newPosition);

            if (!validation.IsValid)
            {
                result.Message = $"Validation failed: {validation.Message}";
                AppLog.Error("AtomicReversalManager", "ValidationFailed", result.Message);

                // Try one more time to place missing orders
                if (!validation.HasStopLoss)
                {
                    var slRetry = _protectiveManager.PlaceStopLoss(newPosition, newSlPrice, $"{comment}.StopLoss");
                    if (slRetry.Success)
                        result.StopLossOrderId = slRetry.OrderId;
                }

                if (!validation.HasTakeProfit)
                {
                    var tpRetry = _protectiveManager.PlaceTakeProfit(newPosition, newTpPrice, $"{comment}.TakeProfit");
                    if (tpRetry.Success)
                        result.TakeProfitOrderId = tpRetry.OrderId;
                }
            }

            // Final validation
            var finalValidation = _protectiveManager.ValidateProtection(newPosition);
            result.Success = finalValidation.IsValid;

            if (result.Success)
            {
                result.Message = "Reversal completed successfully";
                AppLog.Trading("AtomicReversalManager", "Success",
                    $"✅ Reversal COMPLETED: {newSide} {newPosition.Quantity} @ {entryPrice:F2}, " +
                    $"SL={newSlPrice:F2}, TP={newTpPrice:F2}");
            }
            else
            {
                result.Message = $"Reversal incomplete: {finalValidation.Message}";
                AppLog.Error("AtomicReversalManager", "Incomplete", result.Message);
            }

            AppLog.Trading("AtomicReversalManager", "EndReversal",
                $"═══════════════════════════════════════════════════════════");

            return result;
        }

        #region Private Helpers

        private List<Position> GetCurrentPositions()
        {
            return Core.Instance.Positions
                .Where(p => p.Symbol == _symbol && p.Account == _account)
                .ToList();
        }

        private List<Position> GetPositionsForSide(Side side)
        {
            return Core.Instance.Positions
                .Where(p => p.Symbol == _symbol && p.Account == _account && p.Side == side)
                .ToList();
        }

        private List<Order> GetProtectiveOrdersForPositions(List<Position> positions)
        {
            var positionIds = positions.Select(p => p.Id).ToHashSet();

            return Core.Instance.Orders
                .Where(o => positionIds.Contains(o.PositionId) &&
                           (o.Status == OrderStatus.Opened || o.Status == OrderStatus.PartiallyFilled) &&
                           (o.OrderType?.Behavior == OrderTypeBehavior.Stop ||
                            o.OrderType?.Behavior == OrderTypeBehavior.Limit))
                .ToList();
        }

        private void CancelProtectiveOrders(List<Order> orders, string reason)
        {
            foreach (var order in orders)
            {
                try
                {
                    order.Cancel();
                    AppLog.System("AtomicReversalManager", "CancelOrder",
                        $"Cancelled {order.OrderType?.Behavior} order {order.Id}: {reason}");
                }
                catch (Exception ex)
                {
                    AppLog.Error("AtomicReversalManager", "CancelFailed",
                        $"Failed to cancel order {order.Id}: {ex.Message}");
                }
            }
        }

        private Position WaitForNewPosition(Side expectedSide, double expectedQuantity)
        {
            var startTime = DateTime.UtcNow;
            Position lastSeenPosition = null;

            while ((DateTime.UtcNow - startTime).TotalMilliseconds < MAX_COMPLETION_WAIT_MS)
            {
                var positions = GetPositionsForSide(expectedSide);

                if (positions.Any())
                {
                    // Return the most recent position
                    var newestPosition = positions
                        .OrderByDescending(p => p.CreateTime)
                        .FirstOrDefault();

                    if (newestPosition != null)
                    {
                        // Verify quantity is reasonable
                        if (Math.Abs(newestPosition.Quantity - expectedQuantity) <= _symbol.MinLot)
                        {
                            return newestPosition;
                        }

                        lastSeenPosition = newestPosition;
                    }
                }

                Thread.Sleep(POLL_INTERVAL_MS);
            }

            // Return last seen position even if quantity doesn't match exactly
            return lastSeenPosition;
        }

        private void EmergencyFlattenPositions(List<Position> positions, string reason)
        {
            AppLog.Error("AtomicReversalManager", "EmergencyFlatten",
                $"Emergency flattening {positions.Count} position(s): {reason}");

            var marketOrderType = _symbol.GetAlowedOrderTypes(OrderTypeUsage.Order)
                .FirstOrDefault(x => x.Behavior == OrderTypeBehavior.Market);

            if (marketOrderType == null)
            {
                AppLog.Error("AtomicReversalManager", "EmergencyFlatten",
                    "No market order type available!");
                return;
            }

            foreach (var position in positions)
            {
                try
                {
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
                        AppLog.System("AtomicReversalManager", "EmergencyFlatten",
                            $"Placed emergency flatten order for position {position.Id}");
                    }
                    else
                    {
                        AppLog.Error("AtomicReversalManager", "EmergencyFlatten",
                            $"Failed to flatten position {position.Id}: {result.Message}");
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Error("AtomicReversalManager", "EmergencyFlatten",
                        $"Exception flattening position {position.Id}: {ex.Message}");
                }
            }
        }

        private void CleanupFailedReversal(Side originalSide)
        {
            AppLog.System("AtomicReversalManager", "FailedCleanup",
                "Cleaning up after failed reversal...");

            // Cancel any remaining orders
            var allOrders = Core.Instance.Orders
                .Where(o => o.Symbol == _symbol &&
                           o.Account == _account &&
                           (o.Status == OrderStatus.Opened || o.Status == OrderStatus.PartiallyFilled))
                .ToList();

            foreach (var order in allOrders)
            {
                try
                {
                    order.Cancel();
                }
                catch { }
            }

            // Check final position state
            var remainingPositions = GetCurrentPositions();
            if (remainingPositions.Any())
            {
                AppLog.System("AtomicReversalManager", "FailedCleanup",
                    $"Remaining positions after failed reversal: {remainingPositions.Count}");
            }
        }

        #endregion
    }

    #region Result Classes

    public class ReversalResult
    {
        public bool Success { get; set; }
        public Side NewSide { get; set; }
        public double TargetQuantity { get; set; }
        public string ReversalOrderId { get; set; }
        public Position NewPosition { get; set; }
        public string StopLossOrderId { get; set; }
        public string TakeProfitOrderId { get; set; }
        public string Message { get; set; }
    }

    #endregion
}
