using DivergentStrV0_1.OperationSystemAdv;
using DivergentStrV0_1.OperationSystemAdv.DDDCore;
using DivergentStrV0_1.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using TradingPlatform.BusinessLayer;

namespace DivergentStrV0_1.Strategies
{
    public struct SlTpData
    {
        public Symbol Symbol { get; set; }

        public double SlTriggerPrice { get; set; }
        public double currentPrice { get; set; }
        public double AtrInTicks { get; set; }
        public double PreviousLow { get; set; }
        public double PreviousHigh { get; set; }
    }

    public enum TpMode
    {
        Dynamic = 0,  // Existing: Session levels
        Fixed = 1     // New: Fixed distance
    }

    public enum SlMode
    {
        PreviousCandle = 0,  // Existing: Trail to prev bar high/low
        AtrDistance = 1,     // Existing: Trail when too far from price
        AtrTrailing = 2      // NEW: Continuous ATR-based trailing
    }

    internal class RowanSlTpStrategy : ISlTpStrategy<SlTpData>
    {
        //TODO: [DEBUG] Expose ATR and TP distance tunables through validated parameters

        public int max_slInTicks { get; set; }
        public int min_slInTicks { get; set; }

        public int MinTpInTicks { get; set; }
        public int MaxTpInTicks { get; set; }
        //TODO: [DEBUG] Keep ATR slippage multiplier inside [0,2] guardrails
        public double AtrSlippageMultiplier { get; set; } = 0.0;
        private int delta_InTicks;
        
        // New mode selections
        public SlMode SlModeType { get; set; } = SlMode.PreviousCandle;
        public int AtrTrailingMultiplier { get; set; } = 2;
        public TpMode TpModeType { get; set; } = TpMode.Dynamic;
        public int FixedTpInTicks { get; set; } = 50;

        private bool _atrReady;
        private bool _pivotReady;
        private bool? _lastLoggedAtrReady;
        private bool? _lastLoggedPivotReady;
        private readonly Dictionary<string, double> _tickResiduals = new Dictionary<string, double>(StringComparer.Ordinal);

        public bool AtrReady => _atrReady;
        public bool PivotReady => _pivotReady;

        public RowanSlTpStrategy(int min_Tick, int max_Tick)
        {
            if (min_Tick <= 0)
                throw new ArgumentException("Min ticks must be positive", nameof(min_Tick));
            if (max_Tick <= 0)
                throw new ArgumentException("Max ticks must be positive", nameof(max_Tick));
            if (min_Tick > max_Tick)
                throw new ArgumentException("Min ticks cannot be greater than max ticks");

            this.max_slInTicks = max_Tick;
            this.min_slInTicks = min_Tick;
            delta_InTicks = Math.Abs(min_Tick - max_Tick);
        }

        public List<double> CalculateSl(SlTpData marketData, Side side, double entry_price)
        {
            //TODO: [DEBUG] Derive SL from previous candle range plus ATR multiplier, enforcing min/max distance guards

            var sl_temp = marketData.Symbol.CalculateTicks(entry_price, marketData.SlTriggerPrice);
            //TODO: [DEBUG] Confirm ATR slippage contribution respects configured bounds
            
            // MODE 0 (PreviousCandle) does NOT use ATR cushion - use raw pivot only
            var extraTicks = 0.0;
            if (this.SlModeType != SlMode.PreviousCandle)
            {
                extraTicks = Math.Max(0, (int)Math.Round(Math.Abs(marketData.AtrInTicks) * Math.Max(0.0, Math.Min(2.0, this.AtrSlippageMultiplier))));
            }
            var desiredAbsTicks = Math.Abs(sl_temp) + extraTicks;
            
            // ENFORCE min/max bounds
            var sl = (int)Math.Clamp(Math.Ceiling(desiredAbsTicks), this.min_slInTicks, this.max_slInTicks);

            double sl_price = side == Side.Buy
                ? marketData.Symbol.CalculatePrice(entry_price, -sl)
                : marketData.Symbol.CalculatePrice(entry_price, +sl);

            // VERIFICATION: Double-check actual distance
            double actualTicks = Math.Abs(marketData.Symbol.CalculateTicks(entry_price, sl_price));
            if (actualTicks < this.min_slInTicks || actualTicks > this.max_slInTicks)
            {
                // Log error and force to min/max
                AppLog.Error("RowanSlTpStrategy", "CalculateSl", 
                    $"SL distance {actualTicks} ticks outside range [{this.min_slInTicks}, {this.max_slInTicks}], forcing to bounds");
                
                int correctedSl = (int)Math.Clamp(actualTicks, this.min_slInTicks, this.max_slInTicks);
                sl_price = side == Side.Buy
                    ? marketData.Symbol.CalculatePrice(entry_price, -correctedSl)
                    : marketData.Symbol.CalculatePrice(entry_price, +correctedSl);
            }
            
            // MANDATORY: Round to tick size BEFORE returning (e.g., 0.25 increments for NQ)
            sl_price = marketData.Symbol.RoundPriceToTickSize(sl_price);

            return new List<double> { sl_price };
        }

        public List<double> CalculateTp(SlTpData marketData, Side side, double entry_price)
        {
            // NEW: Fixed TP Mode
            if (this.TpModeType == TpMode.Fixed)
            {
                // Fixed TP mode: Simple distance from entry
                int tpTicks = this.FixedTpInTicks;
                
                // Enforce min/max bounds
                int fixedMinTp = Math.Max(1, this.MinTpInTicks);
                int fixedMaxTp = this.MaxTpInTicks <= 0 ? fixedMinTp : Math.Max(fixedMinTp, this.MaxTpInTicks);
                tpTicks = (int)Math.Clamp(tpTicks, fixedMinTp, fixedMaxTp);
                
                double tp_price = side == Side.Buy
                    ? marketData.Symbol.CalculatePrice(entry_price, tpTicks)
                    : marketData.Symbol.CalculatePrice(entry_price, -tpTicks);
                
                // Verify actual distance
                double actualTicks = Math.Abs(marketData.Symbol.CalculateTicks(entry_price, tp_price));
                if (actualTicks < fixedMinTp || actualTicks > fixedMaxTp)
                {
                    AppLog.Error("RowanSlTpStrategy", "CalculateTp", 
                        $"Fixed TP {actualTicks} ticks outside range [{fixedMinTp}, {fixedMaxTp}], clamping");
                    
                    int correctedTp = (int)Math.Clamp(actualTicks, fixedMinTp, fixedMaxTp);
                    tp_price = side == Side.Buy
                        ? marketData.Symbol.CalculatePrice(entry_price, correctedTp)
                        : marketData.Symbol.CalculatePrice(entry_price, -correctedTp);
                }
                
                // MANDATORY: Round to tick size BEFORE returning
                tp_price = marketData.Symbol.RoundPriceToTickSize(tp_price);
                
                return new List<double> { tp_price };
            }
            
            // EXISTING: Dynamic TP Mode (original code preserved)
            //TODO: [DEBUG] Ensure TP level cache is refreshed before evaluating targets
            if (!StaticSessionManager.TpLevels.Levels.Any())
                StaticSessionManager.CalculateTPLevels();

            int minTpTicks = Math.Max(1, this.MinTpInTicks);
            int maxTpTicks = this.MaxTpInTicks <= 0 ? minTpTicks : Math.Max(minTpTicks, this.MaxTpInTicks);

            double GetTargetPrice(int ticks) => marketData.Symbol.CalculatePrice(entry_price, side == Side.Buy ? ticks : -ticks);

            double minTargetPrice = GetTargetPrice(minTpTicks);
            double maxTargetPrice = GetTargetPrice(maxTpTicks);

            var candidateTargets = new List<double>();
            foreach (var level in StaticSessionManager.TpLevels.Levels)
            {
                foreach (var candidate in new[] { level.High, level.Low })
                {
                    if (double.IsNaN(candidate) || double.IsInfinity(candidate))
                        continue;

                    double ticks = marketData.Symbol.CalculateTicks(entry_price, candidate);
                    bool isValidDirection = side == Side.Buy ? ticks > 0 : ticks < 0;
                    double absTicks = Math.Abs(ticks);

                    if (isValidDirection && absTicks >= minTpTicks && absTicks <= maxTpTicks)
                        candidateTargets.Add(candidate);
                }
            }

            double selectedTpItem;
            if (candidateTargets.Count > 0)
            {
                selectedTpItem = candidateTargets
                    .OrderBy(price => Math.Abs(marketData.Symbol.CalculateTicks(entry_price, price)))
                    .First();
            }
            else
            {
                //TODO: [DEBUG] Verify fallback target clamps to configured bounds when no TP level matches
                selectedTpItem = minTargetPrice;
                double fallbackTicks = Math.Abs(marketData.Symbol.CalculateTicks(entry_price, selectedTpItem));
                if (fallbackTicks > maxTpTicks)
                    selectedTpItem = maxTargetPrice;
            }
            
            // MANDATORY: Round to tick size BEFORE returning
            selectedTpItem = marketData.Symbol.RoundPriceToTickSize(selectedTpItem);

            return new List<double> { selectedTpItem };
        }

        // Interface implementation (required)
        public Func<double, double> UpdateSl(SlTpData marketData, ITpSlItems item)
        {
            return UpdateSl(marketData, item, false);
        }
        
        // Overload with isBarClose parameter for log control
        public Func<double, double> UpdateSl(SlTpData marketData, ITpSlItems item, bool isBarClose)
        {
            if (marketData.Symbol == null || item == null)
                return current_sl => current_sl;

            UpdateHealthFlags(marketData, item.Side);

            try
            {
                // MODE 0: Trail to Previous Candle - SL trails to pivot, clamped by min/max distance from CURRENT PRICE
                if (this.SlModeType == SlMode.PreviousCandle)
                {
                    return current_sl =>
                    {
                        string itemId = item.Id;
                        string shortId = !string.IsNullOrEmpty(itemId) && itemId.Length > 8 ? itemId.Substring(0, 8) : (itemId ?? "unknown");
                        var symbol = marketData.Symbol;

                        double pivot = item.Side == Side.Buy ? marketData.PreviousLow : marketData.PreviousHigh;
                        if (double.IsNaN(pivot) || pivot <= 0 || double.IsNaN(marketData.currentPrice))
                        {
                            AppLog.System("RowanSlTpStrategy", "UpdateSl_Mode0", 
                                $"[Item {shortId}] Invalid pivot={pivot}, keeping SL={current_sl:F2}");
                            return current_sl;
                        }

                        // MODE 0: Use raw pivot directly, NO ATR cushion
                        double targetPivot = pivot;

                        // Calculate distance from CURRENT PRICE to pivot
                        double pivotDistFromCurrent = Math.Abs(marketData.Symbol.CalculateTicks(marketData.currentPrice, targetPivot));

                        double target;

                        // Clamp pivot to min/max distance from CURRENT price
                        if (pivotDistFromCurrent < this.min_slInTicks)
                        {
                            // Pivot too close to current price → use min distance from current
                            target = item.Side == Side.Buy 
                                ? marketData.Symbol.CalculatePrice(marketData.currentPrice, -this.min_slInTicks)
                                : marketData.Symbol.CalculatePrice(marketData.currentPrice, +this.min_slInTicks);
                        }
                        else if (pivotDistFromCurrent > this.max_slInTicks)
                        {
                            // Pivot too far from current price → use max distance from current
                            target = item.Side == Side.Buy
                                ? marketData.Symbol.CalculatePrice(marketData.currentPrice, -this.max_slInTicks)
                                : marketData.Symbol.CalculatePrice(marketData.currentPrice, +this.max_slInTicks);
                        }
                        else
                        {
                            // Pivot is within range → use it directly as SL
                            target = targetPivot;
                        }
                        
                        // MANDATORY: Round to tick size IMMEDIATELY after clamp, BEFORE monotonic check
                        target = symbol.RoundPriceToTickSize(target);

                        // Monotonicity: only trail favorably (never loosen) - comparing rounded values
                        if (item.Side == Side.Buy && target < current_sl)
                            target = current_sl;
                        if (item.Side == Side.Sell && target > current_sl)
                            target = current_sl;
                        double finalTarget = ApplyTickResidual(itemId, symbol, current_sl, target);
                        bool willTrail = finalTarget != current_sl;

                        // DIAGNOSTIC (Step 4C): ALWAYS log Mode 0 calculation (not conditional)
                        // This ensures we can see if trailing logic runs even if bars aren't closing
                        AppLog.Trading("RowanSlTpStrategy", "UpdateSl_Mode0", 
                            $"[Item {shortId}] Side={item.Side}, CurrentPrice={marketData.currentPrice:F2}, " +
                            $"Pivot={pivot:F2}, PivotDist={pivotDistFromCurrent:F1}t, " +
                            $"Target={target:F2}, FinalTarget={finalTarget:F2}, CurrentSL={current_sl:F2}, " +
                            $"WillTrail={willTrail}, isBarClose={isBarClose}");

                        return finalTarget;
                    };
                }
                
                // MODE 1: ATR Distance (EXISTING LOGIC PRESERVED)
                if (this.SlModeType == SlMode.AtrDistance)
                {
                    return current_sl =>
                {
                    var delta = marketData.Symbol.CalculateTicks(current_sl, marketData.currentPrice);
                    bool isOut = delta > this.delta_InTicks;

                    if (!isOut)
                        return current_sl;

                    // Calculate new SL position
                    double newSlPrice;
                    if (item.Side == Side.Buy)
                        newSlPrice = marketData.Symbol.CalculatePrice(marketData.currentPrice, -max_slInTicks);
                    else
                        newSlPrice = marketData.Symbol.CalculatePrice(marketData.currentPrice, +max_slInTicks);
                    
                    // SAFETY CHECK: Ensure new SL respects min distance
                    double actualDistance = Math.Abs(marketData.Symbol.CalculateTicks(marketData.currentPrice, newSlPrice));
                    
                    if (actualDistance < this.min_slInTicks)
                    {
                        // Adjust to exactly min distance
                        newSlPrice = item.Side == Side.Buy
                            ? marketData.Symbol.CalculatePrice(marketData.currentPrice, -min_slInTicks)
                            : marketData.Symbol.CalculatePrice(marketData.currentPrice, +min_slInTicks);
                    }
                    else if (actualDistance > this.max_slInTicks)
                    {
                        // Adjust to exactly max distance (should rarely happen)
                        newSlPrice = item.Side == Side.Buy
                            ? marketData.Symbol.CalculatePrice(marketData.currentPrice, -max_slInTicks)
                            : marketData.Symbol.CalculatePrice(marketData.currentPrice, +max_slInTicks);
                    }
                    
                    return newSlPrice;
                };
                }
                
                // MODE 2: ATR Trailing (NEW - continuous trailing at fixed ATR distance)
                if (this.SlModeType == SlMode.AtrTrailing)
                {
                    return current_sl =>
                    {
                        string itemId = item.Id;
                        string shortId = !string.IsNullOrEmpty(itemId) && itemId.Length > 8 ? itemId.Substring(0, 8) : (itemId ?? "unknown");
                        var symbol = marketData.Symbol;
                        double currentPrice = marketData.currentPrice;

                        if (symbol == null || double.IsNaN(currentPrice))
                        {
                            ClearTickResidual(itemId);
                            return current_sl;
                        }

                        double atrTicks = Math.Abs(marketData.AtrInTicks);
                        if (atrTicks <= 0.0001)
                        {
                            ClearTickResidual(itemId);
                            return current_sl;
                        }

                        double trailingMult = Math.Max(1, this.AtrTrailingMultiplier);
                        double cushionMult = Math.Max(0.0, Math.Min(2.0, this.AtrSlippageMultiplier));

                        double baseDistanceTicks = atrTicks * trailingMult;
                        double cushionTicks = atrTicks * cushionMult;
                        int slDistance = (int)Math.Round(baseDistanceTicks + cushionTicks);

                        if (slDistance <= 0)
                        {
                            ClearTickResidual(itemId);
                            return current_sl;
                        }

                        slDistance = (int)Math.Clamp(slDistance, this.min_slInTicks, this.max_slInTicks);

                        double preliminaryTarget = item.Side == Side.Buy
                            ? symbol.CalculatePrice(currentPrice, -slDistance)
                            : symbol.CalculatePrice(currentPrice, +slDistance);

                        double pivotDistFromCurrent = Math.Abs(symbol.CalculateTicks(currentPrice, preliminaryTarget));

                        double target;
                        if (pivotDistFromCurrent < this.min_slInTicks)
                        {
                            target = item.Side == Side.Buy
                                ? symbol.CalculatePrice(currentPrice, -this.min_slInTicks)
                                : symbol.CalculatePrice(currentPrice, +this.min_slInTicks);
                        }
                        else if (pivotDistFromCurrent > this.max_slInTicks)
                        {
                            target = item.Side == Side.Buy
                                ? symbol.CalculatePrice(currentPrice, -this.max_slInTicks)
                                : symbol.CalculatePrice(currentPrice, +this.max_slInTicks);
                        }
                        else
                        {
                            target = preliminaryTarget;
                        }
                        
                        // MANDATORY: Round to tick size IMMEDIATELY after calculation, BEFORE monotonic check
                        target = symbol.RoundPriceToTickSize(target);

                        // Monotonicity: only trail favorably (comparing rounded values)
                        if (item.Side == Side.Buy && target < current_sl)
                            target = current_sl;
                        if (item.Side == Side.Sell && target > current_sl)
                            target = current_sl;

                        double finalTarget = ApplyTickResidual(itemId, symbol, current_sl, target);
                        bool willTrail = finalTarget != current_sl;
                        double currentDistance = Math.Abs(symbol.CalculateTicks(currentPrice, current_sl));
                        double targetDistance = Math.Abs(symbol.CalculateTicks(currentPrice, finalTarget));

                        // Only log at bar close to reduce log volume
                        if (isBarClose)
                        {
                            AppLog.Trading("RowanSlTpStrategy", "UpdateSl_Mode2",
                                $"[Item {shortId}] Side={item.Side}, ATR={marketData.AtrInTicks:F2}t, Mult={this.AtrTrailingMultiplier}, Cushion={cushionMult:F2}, Distance={slDistance}t, CurrentSL={current_sl:F2} ({currentDistance:F2}t), Target={target:F2}, FinalTarget={finalTarget:F2} ({targetDistance:F2}t), Price={currentPrice:F2}, WillTrail={willTrail}");
                        }

                        if (!willTrail)
                            return current_sl;

                        if (item.Side == Side.Buy && finalTarget <= current_sl)
                            return current_sl;
                        if (item.Side == Side.Sell && finalTarget >= current_sl)
                            return current_sl;

                        return finalTarget;
                    };
                }
                
                // Fallback: no update
                return current_sl => current_sl;
            }
            catch (Exception)
            {
                return current_sl => current_sl;
            }
        }

        private double ApplyTickResidual(string itemId, Symbol symbol, double currentSl, double rawTarget)
        {
            if (symbol == null || string.IsNullOrEmpty(itemId) || double.IsNaN(currentSl) || double.IsNaN(rawTarget))
                return rawTarget;

            double deltaTicks = symbol.CalculateTicks(currentSl, rawTarget);
            if (double.IsNaN(deltaTicks))
                return rawTarget;

            if (Math.Abs(deltaTicks) >= 1.0 - 1e-6)
            {
                _tickResiduals.Remove(itemId);
                return symbol.RoundPriceToTickSize(rawTarget);
            }

            double carry = 0.0;
            _tickResiduals.TryGetValue(itemId, out carry);
            carry += deltaTicks;

            if (Math.Abs(carry) >= 1.0)
            {
                int wholeTicks = carry > 0 ? (int)Math.Floor(carry) : (int)Math.Ceiling(carry);
                carry -= wholeTicks;
                _tickResiduals[itemId] = carry;
                double adjusted = symbol.CalculatePrice(currentSl, wholeTicks);
                return symbol.RoundPriceToTickSize(adjusted);
            }

            _tickResiduals[itemId] = carry;
            return currentSl;
        }

        public void ClearTickResidual(string itemId)
        {
            if (string.IsNullOrEmpty(itemId))
                return;
            _tickResiduals.Remove(itemId);
        }

        private void UpdateHealthFlags(SlTpData marketData, Side side)
        {
            bool atrReady = !double.IsNaN(marketData.AtrInTicks) && marketData.AtrInTicks > 0.0001;
            if (_lastLoggedAtrReady != atrReady)
            {
                AppLog.System("RowanSlTpStrategy", "Health",
                    $"ATR readiness => {atrReady} (AtrTicks={marketData.AtrInTicks:F4})");
                _lastLoggedAtrReady = atrReady;
            }
            _atrReady = atrReady;

            bool pivotReady;
            if (side == Side.Buy)
                pivotReady = !double.IsNaN(marketData.PreviousLow) && marketData.PreviousLow > 0;
            else if (side == Side.Sell)
                pivotReady = !double.IsNaN(marketData.PreviousHigh) && marketData.PreviousHigh > 0;
            else
                pivotReady = !double.IsNaN(marketData.PreviousHigh) && !double.IsNaN(marketData.PreviousLow);

            if (_lastLoggedPivotReady != pivotReady)
            {
                AppLog.System("RowanSlTpStrategy", "Health",
                    $"Pivot readiness => {pivotReady} (PrevHigh={marketData.PreviousHigh:F2}, PrevLow={marketData.PreviousLow:F2})");
                _lastLoggedPivotReady = pivotReady;
            }
            _pivotReady = pivotReady;
        }

        public Func<double, double> UpdateTp(SlTpData marketData, ITpSlItems item)
        {
            return currentTp => currentTp;
        }
    }
}

