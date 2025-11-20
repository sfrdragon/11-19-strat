using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using TradingPlatform.BusinessLayer;

namespace DivergentStrV0_1.Utils
{
    /// <summary>
    /// Robust protective order placement and validation system
    /// Handles SL/TP placement with retry logic, validation, and cleanup
    /// </summary>
    public class ProtectiveOrderManager
    {
        private readonly Symbol _symbol;
        private readonly Account _account;
        private const int MAX_PLACEMENT_ATTEMPTS = 3;
        private const int RETRY_DELAY_MS = 200;
        private const int VALIDATION_TIMEOUT_MS = 2000;

        public ProtectiveOrderManager(Symbol symbol, Account account)
        {
            _symbol = symbol ?? throw new ArgumentNullException(nameof(symbol));
            _account = account ?? throw new ArgumentNullException(nameof(account));
        }

        /// <summary>
        /// Place stop loss with retry and validation
        /// </summary>
        public PlacementResult PlaceStopLoss(Position position, double slPrice, string comment = null)
        {
            if (position == null)
                return PlacementResult.Failure("Position is null");

            if (double.IsNaN(slPrice) || slPrice <= 0)
                return PlacementResult.Failure($"Invalid SL price: {slPrice}");

            // Round to tick size
            slPrice = _symbol.RoundPriceToTickSize(slPrice);

            // Get stop order type
            var stopOrderType = _symbol.GetAlowedOrderTypes(OrderTypeUsage.CloseOrder)
                .FirstOrDefault(x => x.Behavior == OrderTypeBehavior.Stop);

            if (stopOrderType == null)
                return PlacementResult.Failure("No stop order type available");

            // Determine opposite side
            Side exitSide = position.Side == Side.Buy ? Side.Sell : Side.Buy;

            // Build request
            var request = new PlaceOrderRequestParameters
            {
                Symbol = _symbol,
                Account = _account,
                Side = exitSide,
                Quantity = position.Quantity,
                OrderTypeId = stopOrderType.Id,
                TriggerPrice = slPrice,
                PositionId = position.Id,  // CRITICAL: Link to position
                TimeInForce = TimeInForce.Day,
                Comment = comment,
                AdditionalParameters = new List<SettingItem>
                {
                    new SettingItemBoolean(OrderType.REDUCE_ONLY, true)
                }
            };

            // Attempt placement with retry
            return PlaceOrderWithRetryAndValidation(request, "StopLoss", OrderTypeBehavior.Stop, slPrice);
        }

        /// <summary>
        /// Place take profit with retry and validation
        /// </summary>
        public PlacementResult PlaceTakeProfit(Position position, double tpPrice, string comment = null)
        {
            if (position == null)
                return PlacementResult.Failure("Position is null");

            if (double.IsNaN(tpPrice) || tpPrice <= 0)
                return PlacementResult.Failure($"Invalid TP price: {tpPrice}");

            // Round to tick size
            tpPrice = _symbol.RoundPriceToTickSize(tpPrice);

            // Get limit order type
            var limitOrderType = _symbol.GetAlowedOrderTypes(OrderTypeUsage.CloseOrder)
                .FirstOrDefault(x => x.Behavior == OrderTypeBehavior.Limit);

            if (limitOrderType == null)
                return PlacementResult.Failure("No limit order type available");

            // Determine opposite side
            Side exitSide = position.Side == Side.Buy ? Side.Sell : Side.Buy;

            // Build request
            var request = new PlaceOrderRequestParameters
            {
                Symbol = _symbol,
                Account = _account,
                Side = exitSide,
                Quantity = position.Quantity,
                OrderTypeId = limitOrderType.Id,
                Price = tpPrice,  // Note: Price, not TriggerPrice
                PositionId = position.Id,  // CRITICAL: Link to position
                TimeInForce = TimeInForce.Day,
                Comment = comment,
                AdditionalParameters = new List<SettingItem>
                {
                    new SettingItemBoolean(OrderType.REDUCE_ONLY, true)
                }
            };

            // Attempt placement with retry
            return PlaceOrderWithRetryAndValidation(request, "TakeProfit", OrderTypeBehavior.Limit, tpPrice);
        }

        /// <summary>
        /// Place both SL and TP atomically with validation
        /// </summary>
        public BracketResult PlaceBracket(Position position, double slPrice, double tpPrice, string baseComment = null)
        {
            if (position == null)
                return new BracketResult { Success = false, Message = "Position is null" };

            AppLog.Trading("ProtectiveOrderManager", "PlaceBracket",
                $"Placing bracket for position {position.Id}: SL={slPrice:F2}, TP={tpPrice:F2}");

            var slResult = PlaceStopLoss(position, slPrice, $"{baseComment}.StopLoss");
            var tpResult = PlaceTakeProfit(position, tpPrice, $"{baseComment}.TakeProfit");

            bool bothSucceeded = slResult.Success && tpResult.Success;

            if (!bothSucceeded)
            {
                AppLog.Error("ProtectiveOrderManager", "PlaceBracket",
                    $"Bracket placement incomplete: SL={slResult.Success}, TP={tpResult.Success}");

                // If one succeeded but not the other, try to cancel the successful one
                if (slResult.Success && !tpResult.Success && !string.IsNullOrEmpty(slResult.OrderId))
                {
                    CancelOrder(slResult.OrderId, "Bracket incomplete - TP failed");
                }
                else if (tpResult.Success && !slResult.Success && !string.IsNullOrEmpty(tpResult.OrderId))
                {
                    CancelOrder(tpResult.OrderId, "Bracket incomplete - SL failed");
                }
            }

            return new BracketResult
            {
                Success = bothSucceeded,
                StopLossOrderId = slResult.OrderId,
                TakeProfitOrderId = tpResult.OrderId,
                Message = bothSucceeded ? "Bracket placed successfully" :
                    $"SL: {slResult.Message}, TP: {tpResult.Message}"
            };
        }

        /// <summary>
        /// Cancel all protective orders for a position
        /// </summary>
        public void CancelProtectiveOrders(string positionId, string reason)
        {
            if (string.IsNullOrEmpty(positionId))
                return;

            var ordersToCancel = Core.Instance.Orders
                .Where(o => o.PositionId == positionId &&
                           (o.Status == OrderStatus.Opened || o.Status == OrderStatus.PartiallyFilled) &&
                           (o.OrderType?.Behavior == OrderTypeBehavior.Stop ||
                            o.OrderType?.Behavior == OrderTypeBehavior.Limit))
                .ToList();

            if (ordersToCancel.Any())
            {
                AppLog.System("ProtectiveOrderManager", "CancelProtective",
                    $"Cancelling {ordersToCancel.Count} protective order(s) for position {positionId}. Reason: {reason}");

                foreach (var order in ordersToCancel)
                {
                    CancelOrder(order.Id, reason);
                }
            }
        }

        /// <summary>
        /// Find and cleanup orphaned protective orders (orders without positions)
        /// </summary>
        public int CleanupOrphanedOrders(string strategyPrefix = null)
        {
            var activePositionIds = Core.Instance.Positions
                .Where(p => p.Symbol == _symbol && p.Account == _account)
                .Select(p => p.Id)
                .ToHashSet(StringComparer.Ordinal);

            var orphanedOrders = Core.Instance.Orders
                .Where(o => o.Symbol == _symbol &&
                           o.Account == _account &&
                           (o.Status == OrderStatus.Opened || o.Status == OrderStatus.PartiallyFilled) &&
                           (o.OrderType?.Behavior == OrderTypeBehavior.Stop ||
                            o.OrderType?.Behavior == OrderTypeBehavior.Limit) &&
                           !string.IsNullOrEmpty(o.PositionId) &&
                           !activePositionIds.Contains(o.PositionId))
                .ToList();

            // Additional filter by strategy prefix if provided
            if (!string.IsNullOrEmpty(strategyPrefix))
            {
                orphanedOrders = orphanedOrders
                    .Where(o => !string.IsNullOrEmpty(o.Comment) &&
                               o.Comment.StartsWith(strategyPrefix, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            if (orphanedOrders.Any())
            {
                AppLog.System("ProtectiveOrderManager", "CleanupOrphaned",
                    $"Found {orphanedOrders.Count} orphaned protective order(s), cancelling...");

                foreach (var order in orphanedOrders)
                {
                    CancelOrder(order.Id, "Orphaned - position closed");
                }
            }

            return orphanedOrders.Count;
        }

        /// <summary>
        /// Validate that a position has both SL and TP
        /// </summary>
        public ValidationResult ValidateProtection(Position position)
        {
            if (position == null)
                return new ValidationResult { IsValid = false, Message = "Position is null" };

            var protectiveOrders = Core.Instance.Orders
                .Where(o => o.PositionId == position.Id &&
                           (o.Status == OrderStatus.Opened || o.Status == OrderStatus.PartiallyFilled))
                .ToList();

            bool hasStopLoss = protectiveOrders.Any(o => o.OrderType?.Behavior == OrderTypeBehavior.Stop);
            bool hasTakeProfit = protectiveOrders.Any(o => o.OrderType?.Behavior == OrderTypeBehavior.Limit);

            if (!hasStopLoss && !hasTakeProfit)
            {
                return new ValidationResult
                {
                    IsValid = false,
                    Message = "Missing both SL and TP",
                    HasStopLoss = false,
                    HasTakeProfit = false
                };
            }
            else if (!hasStopLoss)
            {
                return new ValidationResult
                {
                    IsValid = false,
                    Message = "Missing SL",
                    HasStopLoss = false,
                    HasTakeProfit = true
                };
            }
            else if (!hasTakeProfit)
            {
                return new ValidationResult
                {
                    IsValid = false,
                    Message = "Missing TP",
                    HasStopLoss = true,
                    HasTakeProfit = false
                };
            }

            return new ValidationResult
            {
                IsValid = true,
                Message = "Position fully protected",
                HasStopLoss = true,
                HasTakeProfit = true
            };
        }

        #region Private Helpers

        private PlacementResult PlaceOrderWithRetryAndValidation(
            PlaceOrderRequestParameters request,
            string orderTypeLabel,
            OrderTypeBehavior expectedBehavior,
            double expectedPrice)
        {
            TradingOperationResult lastResult = null;

            for (int attempt = 1; attempt <= MAX_PLACEMENT_ATTEMPTS; attempt++)
            {
                if (attempt > 1)
                {
                    AppLog.System("ProtectiveOrderManager", "Retry",
                        $"Retry {attempt}/{MAX_PLACEMENT_ATTEMPTS} for {orderTypeLabel}");
                    Thread.Sleep(RETRY_DELAY_MS * attempt);  // Exponential backoff
                }

                // Preflight: ensure price fields correct
                PreflightOrderRequest(request);

                lastResult = Core.Instance.PlaceOrder(request);

                if (lastResult.Status == TradingOperationResultStatus.Success)
                {
                    string orderId = lastResult.OrderId;
                    AppLog.Trading("ProtectiveOrderManager", "OrderPlaced",
                        $"{orderTypeLabel} order placed: {orderId} @ {expectedPrice:F2}");

                    // Validate order actually exists
                    if (ValidateOrderPlacement(orderId, expectedBehavior, expectedPrice))
                    {
                        return PlacementResult.Success(orderId, expectedPrice);
                    }
                    else
                    {
                        AppLog.Error("ProtectiveOrderManager", "Validation",
                            $"{orderTypeLabel} order {orderId} validation failed");
                        return PlacementResult.Failure($"Order placed but validation failed: {orderId}");
                    }
                }

                // Handle common errors
                string errorMsg = lastResult.Message ?? "Unknown error";
                AppLog.Error("ProtectiveOrderManager", "PlaceOrderFailed",
                    $"{orderTypeLabel} placement attempt {attempt} failed: {errorMsg}");

                // Try to fix common issues
                if (errorMsg.Contains("tick", StringComparison.OrdinalIgnoreCase) ||
                    errorMsg.Contains("increment", StringComparison.OrdinalIgnoreCase))
                {
                    // Re-round prices
                    if (!double.IsNaN(request.Price))
                        request.Price = _symbol.RoundPriceToTickSize(request.Price);
                    if (!double.IsNaN(request.TriggerPrice))
                        request.TriggerPrice = _symbol.RoundPriceToTickSize(request.TriggerPrice);
                }
                else if (errorMsg.Contains("not supported", StringComparison.OrdinalIgnoreCase))
                {
                    // Remove additional parameters
                    request.AdditionalParameters?.Clear();
                }
            }

            return PlacementResult.Failure($"Failed after {MAX_PLACEMENT_ATTEMPTS} attempts: {lastResult?.Message}");
        }

        private void PreflightOrderRequest(PlaceOrderRequestParameters request)
        {
            if (request == null || _symbol == null)
                return;

            var orderType = _symbol.GetAlowedOrderTypes(OrderTypeUsage.CloseOrder)?
                .FirstOrDefault(o => o.Id == request.OrderTypeId);

            if (orderType == null)
                return;

            // Set correct price fields based on order type
            switch (orderType.Behavior)
            {
                case OrderTypeBehavior.Stop:
                    if (!double.IsNaN(request.TriggerPrice))
                        request.TriggerPrice = _symbol.RoundPriceToTickSize(request.TriggerPrice);
                    request.Price = double.NaN;
                    break;

                case OrderTypeBehavior.Limit:
                    if (!double.IsNaN(request.Price))
                        request.Price = _symbol.RoundPriceToTickSize(request.Price);
                    request.TriggerPrice = double.NaN;
                    break;
            }
        }

        private bool ValidateOrderPlacement(string orderId, OrderTypeBehavior expectedBehavior, double expectedPrice)
        {
            if (string.IsNullOrEmpty(orderId))
                return false;

            // Poll for order to appear
            var startTime = DateTime.UtcNow;
            while ((DateTime.UtcNow - startTime).TotalMilliseconds < VALIDATION_TIMEOUT_MS)
            {
                var order = Core.Instance.Orders.FirstOrDefault(o => o.Id == orderId);

                if (order != null)
                {
                    // Verify order properties
                    bool behaviorMatches = order.OrderType?.Behavior == expectedBehavior;

                    double actualPrice = expectedBehavior == OrderTypeBehavior.Stop
                        ? order.TriggerPrice
                        : order.Price;

                    bool priceMatches = Math.Abs(actualPrice - expectedPrice) < _symbol.TickSize * 0.1;

                    if (behaviorMatches && priceMatches)
                    {
                        return true;
                    }
                    else
                    {
                        AppLog.System("ProtectiveOrderManager", "ValidationMismatch",
                            $"Order {orderId}: Behavior={behaviorMatches}, Price={priceMatches} " +
                            $"(expected {expectedPrice:F2}, got {actualPrice:F2})");
                        return false;
                    }
                }

                Thread.Sleep(50);
            }

            AppLog.Error("ProtectiveOrderManager", "ValidationTimeout",
                $"Order {orderId} not found after {VALIDATION_TIMEOUT_MS}ms");
            return false;
        }

        private void CancelOrder(string orderId, string reason)
        {
            try
            {
                var order = Core.Instance.Orders.FirstOrDefault(o => o.Id == orderId);
                if (order != null && (order.Status == OrderStatus.Opened || order.Status == OrderStatus.PartiallyFilled))
                {
                    order.Cancel();
                    AppLog.System("ProtectiveOrderManager", "CancelOrder",
                        $"Cancelled order {orderId}: {reason}");
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("ProtectiveOrderManager", "CancelOrderFailed",
                    $"Failed to cancel order {orderId}: {ex.Message}");
            }
        }

        #endregion
    }

    #region Result Classes

    public class PlacementResult
    {
        public bool Success { get; set; }
        public string OrderId { get; set; }
        public double PlacedPrice { get; set; }
        public string Message { get; set; }

        public static PlacementResult Success(string orderId, double price)
        {
            return new PlacementResult
            {
                Success = true,
                OrderId = orderId,
                PlacedPrice = price,
                Message = "Order placed successfully"
            };
        }

        public static PlacementResult Failure(string message)
        {
            return new PlacementResult
            {
                Success = false,
                Message = message
            };
        }
    }

    public class BracketResult
    {
        public bool Success { get; set; }
        public string StopLossOrderId { get; set; }
        public string TakeProfitOrderId { get; set; }
        public string Message { get; set; }
    }

    public class ValidationResult
    {
        public bool IsValid { get; set; }
        public string Message { get; set; }
        public bool HasStopLoss { get; set; }
        public bool HasTakeProfit { get; set; }
    }

    #endregion
}
