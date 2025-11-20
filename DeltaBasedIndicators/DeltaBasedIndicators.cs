// Copyright QUANTOWER LLC. © 2017-2023. All rights reserved.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using TradingPlatform.BusinessLayer;

namespace DeltaBasedIndicators
{
	/// <summary>
	/// Delta-based indicator exposing four discrete flags only:
	///  0: APAVD_Flag (±1)
	///  1: VD_Strength_Flag (±2)
	///  2: VD_Price_Divergent_Flag (±3)
	///  3: VD_to_Volume_Flag (±4)
	/// Robust to warm-up and zero-division; uses HistoricalData[1] for non-repainting context.
	/// </summary>
	public class DeltaBasedIndicators : Indicator , IVolumeAnalysisIndicator
	{
		// APAVD (Average Price move to Average VD) settings
		[InputParameter("APAVD Settings", 0)]
		public readonly string _tag__uno = "#############";

	[InputParameter("Delta: Use Median", 1, 0, 1, 1)]
	public int _Use_Median = 0;

	[InputParameter("Delta: Threshold Multiplier", 2, 0.1, 20.0, 0.01, 2)]
	public double _Trh = 2.0;

	[InputParameter("Delta: Lookback", 3)]
	public int _LoockBackWindow= 30;

		// VD Strength settings
		[InputParameter("VD Strength Settings", 10)]
		public readonly string _tag__due = "#############";

	[InputParameter("Delta Strength: Lookback", 11)]
	public int _LoockBackWindow_Strength = 30;

	[InputParameter("Delta Strength: Threshold Multiplier", 12, 0.1, 20.0, 0.01, 2)]
	public double _Trh_Strenght = 2.0;

	// VD Divergence settings
	[InputParameter("VD Divergence Settings", 20)]
	public readonly string _tag__tre = "#############";

	[InputParameter("VD Divergence: Threshold Multiplier", 21, 0.1, 20.0, 0.01, 2)]
	public double _Trh_Divergence = 1.0;

		// VD/Volume settings
		[InputParameter("VD/Volume Settings", 30)]
		public readonly string _tag__quattro = "#############";

	[InputParameter("VDtV Lookback", 31)]
	public int _LoockBackWindow_VDtV = 30;

	[InputParameter("VDtV Threshold Multiplier", 32, 0.1, 20.0, 0.01, 2)]
	public double _Trh_VDtV = 2.0;

        [InputParameter("Force Volume Ready", 33, 0, 1, 1)]
        public int _forceVolume = 0;

	[InputParameter("Use Tick-Based Delta", 40, 0, 1, 1)]
	public int _UseTickBasedDelta = 0;

        private RingBuffer<double> _DeltaBuffer;
		private RingBuffer<double> _DeltaBuffer_Strenght;
		private RingBuffer<double> _PriceBuffer;
		private RingBuffer<double> _VolumeBuffer;
		private RingBuffer<double> _DeltaBuffer_VDtV;
		private bool volumeReady = false;
		
	// Tick-based delta calculation
	private int _currentBarUpTicks = 0;
	private int _currentBarDownTicks = 0;
	private double _lastTickPrice = double.NaN;
	private bool _tickBasedModeActive = false;
	private Dictionary<int, double> _historicalTickDeltas = new Dictionary<int, double>();
	private HashSet<int> _savedBarIndices = new HashSet<int>();
	private int _lastProcessedBarIndex = -1;
	
	public DeltaBasedIndicators()
			: base()
		{
			// Defines indicator's name and description.
			Name = "DeltaBasedIndicators";
			Description = "My indicator's annotation";

			// Output lines: all flags only
			AddLineSeries("APAVD_Flag", Color.CadetBlue, 1, LineStyle.Solid);
			AddLineSeries("VD_Strength_Flag", Color.Red, 1, LineStyle.Solid);
			AddLineSeries("VD_Price_Divergent_Flag", Color.Gold, 1, LineStyle.Solid);
			AddLineSeries("VD_to_Volume_Flag", Color.Purple, 1, LineStyle.Solid);

			SeparateWindow = true;
		}

		public bool IsRequirePriceLevelsCalculation => false;

		public void VolumeAnalysisData_Loaded()
		{
			this.volumeReady = true;
		}

		/// <summary>
		/// This function will be called after creating an indicator as well as after its input params reset or chart (symbol or timeframe) updates.
		/// </summary>
	protected override void OnInit()
	{
		// Enable tick updates if tick-based delta mode is active
		if (this._UseTickBasedDelta != 0)
		{
			this.UpdateType = IndicatorUpdateType.OnTick;
			this._tickBasedModeActive = true;
			
			Core.Instance.Loggers.Log("[DeltaIndicators][Init] TICK MODE ACTIVATED - UpdateType set to OnTick, will count up/down ticks per bar", LoggingLevel.Trading);
		}
		else
		{
			this.UpdateType = IndicatorUpdateType.OnBarClose;
			this._tickBasedModeActive = false;
		}
		
		_DeltaBuffer = new RingBuffer<double>(_LoockBackWindow);
		_DeltaBuffer_Strenght = new RingBuffer<double>(_LoockBackWindow_Strength);
		_PriceBuffer = new RingBuffer<double>(_LoockBackWindow);
		_VolumeBuffer = new RingBuffer<double>(_LoockBackWindow_VDtV);
		_DeltaBuffer_VDtV = new RingBuffer<double>(_LoockBackWindow_VDtV);
	}

		/// <summary>
		/// Calculation entry point. This function is called when a price data updates. 
		/// Will be runing under the HistoricalBar mode during history loading. 
		/// Under NewTick during realtime. 
		/// Under NewBar if start of the new bar is required.
		/// </summary>
		/// <param name="args">Provides data of updating reason and incoming price.</param>
	protected override void OnUpdate(UpdateArgs args)
	{
		// DIAGNOSTIC: Log all events in tick mode
		if (this._tickBasedModeActive)
		{
			Core.Instance.Loggers.Log($"[DeltaIndicators][Update] Event={args.Reason}, Count={this.Count}, DictSize={_historicalTickDeltas.Count}", LoggingLevel.Trading);
		}
		
		// Backup: Detect bar transitions by Count changes (works even if NewBar doesn't fire)
		if (this._tickBasedModeActive)
		{
			int currentBarIndex = this.Count;
			bool barTransitioned = (currentBarIndex != _lastProcessedBarIndex && _lastProcessedBarIndex >= 0);
			
			if (barTransitioned && args.Reason != UpdateReason.NewBar)
			{
				// Bar changed but NewBar didn't fire - save manually
				Core.Instance.Loggers.Log($"[DeltaIndicators][ManualBarSave] Bar transition detected without NewBar event! Saving bar #{_lastProcessedBarIndex}", LoggingLevel.Trading);
				
				double completedBarDelta = GetTickBasedDelta();
				if (!_savedBarIndices.Contains(_lastProcessedBarIndex))
				{
					_historicalTickDeltas[_lastProcessedBarIndex] = completedBarDelta;
					_savedBarIndices.Add(_lastProcessedBarIndex);
				}
				
				ResetTickCounters();
				_lastTickPrice = this.HistoricalData[0][PriceType.Open];
			}
			
			_lastProcessedBarIndex = currentBarIndex;
		}
		
		// Handle tick-based mode tick processing
		if (this._tickBasedModeActive)
		{
			if (args.Reason == UpdateReason.NewBar)
			{
				// CRITICAL: Save completed bar's delta BEFORE resetting
				double completedBarDelta = GetTickBasedDelta();
				int completedBarKey = this.Count - 1;  // Bar that just closed
				
				// Only save if we have data AND haven't already saved this bar
				if (this.Count > 1 && !_savedBarIndices.Contains(completedBarKey))
				{
					_historicalTickDeltas[completedBarKey] = completedBarDelta;
					_savedBarIndices.Add(completedBarKey);
					
					// Diagnostic logging
					Core.Instance.Loggers.Log($"[DeltaIndicators][SaveBar] Saved Bar #{completedBarKey}: Delta={completedBarDelta} (Up={_currentBarUpTicks}, Down={_currentBarDownTicks})", LoggingLevel.Trading);
				}
				else if (_savedBarIndices.Contains(completedBarKey))
				{
					Core.Instance.Loggers.Log($"[DeltaIndicators][SaveBar] Bar #{completedBarKey} ALREADY SAVED - skipping to prevent overwrite", LoggingLevel.Trading);
				}
				
				// Clean up old entries beyond max lookback window
				int maxLookback = Math.Max(_LoockBackWindow, Math.Max(_LoockBackWindow_Strength, _LoockBackWindow_VDtV));
				if (_historicalTickDeltas.Count > maxLookback + 10)
				{
					var keysToRemove = _historicalTickDeltas.Keys.Where(k => k < this.Count - maxLookback - 5).ToList();
					foreach (var key in keysToRemove)
					{
						_historicalTickDeltas.Remove(key);
						_savedBarIndices.Remove(key);
					}
				}
				
				// NOW reset for new bar
				ResetTickCounters();
				// Initialize with current bar's open
				_lastTickPrice = this.HistoricalData[0][PriceType.Open];
			}
			else if (args.Reason == UpdateReason.NewTick)
			{
				// Process tick
				double tickPrice = this.HistoricalData[0][PriceType.Last];
				ProcessTick(tickPrice);
				return; // Don't run calculations until bar close
			}
		}
		
		// Handle historical bar loading in tick mode
		if (this._tickBasedModeActive && args.Reason == UpdateReason.HistoricalBar)
		{
			int historicalBarKey = this.Count;  // Bar being loaded
			
			// Only save if not already saved (prevents overwrites)
			if (!_savedBarIndices.Contains(historicalBarKey))
			{
				if (this.volumeReady || _forceVolume != 0)
				{
					// During historical loading, we don't get individual ticks
					// Fall back to VolumeAnalysisData as proxy
					double historicalDelta = this.HistoricalData[0].VolumeAnalysisData.Total.Delta;
					_historicalTickDeltas[historicalBarKey] = historicalDelta;
					_savedBarIndices.Add(historicalBarKey);
					
					// Diagnostic logging
					Core.Instance.Loggers.Log($"[DeltaIndicators][LoadHistBar] Loaded Bar #{historicalBarKey}: Delta={historicalDelta}", LoggingLevel.Trading);
				}
			}
		}
		
		// Run calculations only on bar close/new bar
		if (args.Reason != UpdateReason.NewBar && args.Reason != UpdateReason.HistoricalBar)
			return;

		int barCount = this.HistoricalData?.Count ?? 0;
		if (barCount <= 1)
			return;

		if (this._tickBasedModeActive && this.Count < 1)
			return;
	
	// Get delta from appropriate source
	double delta;
	if (this._tickBasedModeActive)
	{
		// Get delta for the current bar that just closed (now at index [0])
		delta = GetHistoricalTickDelta(0);
		
		// Diagnostic logging
		Core.Instance.Loggers.Log($"[DeltaIndicators][RetrieveDelta] Bar [0] (key={this.Count}): Delta={delta}, Found={_historicalTickDeltas.ContainsKey(this.Count)}", LoggingLevel.Trading);
	}
	else
	{
		// Wait until volume analysis is fully available
		if (!this.volumeReady && _forceVolume == 0)
			return;
		
		var currentBar = this.HistoricalData[0];
		var volumeAnalysis = currentBar?.VolumeAnalysisData;
		var totalVolumeAnalysis = volumeAnalysis?.Total;
		if (totalVolumeAnalysis == null)
			return;
		
		delta = totalVolumeAnalysis.Delta;
	}

		// Maintain rolling buffers (price)
		if (!this._PriceBuffer.IsFull)
			this.FillPriceBuffer();

		else
		{
			double closePrice = this.HistoricalData[0]?[PriceType.Close] ?? double.NaN;
			double openPrice = this.HistoricalData[0]?[PriceType.Open] ?? double.NaN;
			if (double.IsNaN(closePrice) || double.IsNaN(openPrice))
				return;

			double priceMove = Math.Abs(closePrice - openPrice);
			this._PriceBuffer.Add(priceMove);
		}

			// Maintain rolling buffers (delta for APAVD)
			if (!this._DeltaBuffer.IsFull)
				this.FillDeltaBuffer(this._LoockBackWindow, this._DeltaBuffer);

			else
				this._DeltaBuffer.Add(Math.Abs(delta));

			// Maintain rolling buffers (delta for Strength)
			if (!this._DeltaBuffer_Strenght.IsFull)
				this.FillDeltaBuffer(_LoockBackWindow_Strength, this._DeltaBuffer_Strenght);
			else
				this._DeltaBuffer_Strenght.Add(Math.Abs(delta));

			// VDtV buffers
			if (!this._VolumeBuffer.IsFull)
				this.FillVolumeBuffer();
			else
			{
				double barVolume = this.HistoricalData[0]?[PriceType.Volume] ?? double.NaN;
				if (double.IsNaN(barVolume))
					return;
				this._VolumeBuffer.Add(barVolume);
			}

			if (!this._DeltaBuffer_VDtV.IsFull)
				this.FillDeltaBuffer(_LoockBackWindow_VDtV, this._DeltaBuffer_VDtV);
			else
				this._DeltaBuffer_VDtV.Add(Math.Abs(delta));

			// Ensure buffers are ready before using averages
			if (!this._PriceBuffer.IsFull || !this._DeltaBuffer.IsFull || !this._DeltaBuffer_Strenght.IsFull)
				return;

			var priceSeries = this._PriceBuffer.ToArray();
			var deltaSeries = this._DeltaBuffer.ToArray();
			var deltaStrengthSeries = this._DeltaBuffer_Strenght.ToArray();

		var priceCentral = this._Use_Median != 0 ? ComputeMedian(priceSeries) : priceSeries.Average();
		var deltaCentral = this._Use_Median != 0 ? ComputeMedian(deltaSeries) : deltaSeries.Average();
		var deltaStrengthCentral = this._Use_Median != 0 ? ComputeMedian(deltaStrengthSeries) : deltaStrengthSeries.Average();


			double APAVD;
			if (deltaCentral > 0)
				APAVD = priceCentral / deltaCentral;
			else
				APAVD = double.PositiveInfinity; // force isOkey to false

			var lastDeltaAbs = Math.Abs(deltaSeries.Last());
			double CPVD = lastDeltaAbs > 0 ? priceSeries.Last() / lastDeltaAbs : 0.0;

			bool isOkey = !double.IsInfinity(APAVD) && !double.IsNaN(APAVD) && CPVD > APAVD * this._Trh;
			bool isOkeyStrenght = Math.Abs(delta) > deltaStrengthCentral*this._Trh_Strenght;

			double value = !isOkey ? 0 : (delta > 0 ? 1 : -1);
			double value_strenght = !isOkeyStrenght ? 0 : (delta > 0 ? 1 : -1);

			// Divergenza: segno del VD vs direzione del prezzo
			int priceSign = Math.Sign(this.HistoricalData[0][PriceType.Close] - this.HistoricalData[0][PriceType.Open]);
			int vdSign = Math.Sign(delta);
			double deltaAbs = Math.Abs(delta);
			bool divergenceMagnitude = deltaCentral > 0 && deltaAbs > deltaCentral * this._Trh_Divergence;
			int delta_resoult = (divergenceMagnitude && priceSign != 0 && vdSign != 0 && vdSign != priceSign) ? (vdSign > 0 ? 1 : -1) : 0;

			

			// Scale flags for readability: line0=±1, line1=±2, line2=±3, line3=±4
			SetValue(value); // ±1 or 0
			int strengthFlag = value_strenght == 0 ? 0 : (value_strenght > 0 ? 2 : -2);
			SetValue(strengthFlag, 1);
			int divFlag = delta_resoult == 0 ? 0 : (delta_resoult > 0 ? 3 : -3);
			SetValue(divFlag, 2);

			// VDtV flag (|VD|/Vol vs medie)
			if (this._VolumeBuffer.IsFull && this._DeltaBuffer_VDtV.IsFull)
			{
				var avgVD_v = this._DeltaBuffer_VDtV.ToArray().Average();
				var avgVol = this._VolumeBuffer.ToArray().Average();
				var vdNow = Math.Abs(delta);
				double volNow = this.HistoricalData[0]?[PriceType.Volume] ?? double.NaN;
				if (double.IsNaN(volNow))
					return;

				double baseRatio = (avgVol > 0) ? (avgVD_v / avgVol) : double.NaN;
				double curRatio = (volNow > 0) ? (vdNow / volNow) : 0.0;

				bool strongVDtV = !double.IsNaN(baseRatio) && !double.IsInfinity(baseRatio) && baseRatio > 0 && curRatio > baseRatio * this._Trh_VDtV;
				int vdtvFlag = strongVDtV ? (delta > 0 ? 1 : -1) : 0;
				int vdtvFlagScaled = vdtvFlag == 0 ? 0 : (vdtvFlag > 0 ? 4 : -4);
				SetValue(vdtvFlagScaled, 3);
			}

		}


	private void FillDeltaBuffer(int period, RingBuffer<double> ringBuffer)
	{
		if (this.Count < period)
			return; // Not enough bars to seed the buffer

		if (this._tickBasedModeActive)
		{
			// Fill from historical tick delta storage
			Core.Instance.Loggers.Log($"[DeltaIndicators][FillBuffer] Filling buffer (tick mode): period={period}, Count={this.Count}", LoggingLevel.Trading);
			
			for (int i = period; i > 0; i--)
			{
				double val = GetHistoricalTickDelta(i);
				int barKey = this.Count - i;
				Core.Instance.Loggers.Log($"[DeltaIndicators][FillBuffer]   Bar [{i}] Key={barKey}: Delta={val}", LoggingLevel.Trading);
				ringBuffer.Add(Math.Abs(val));
			}
		}
		else
		{
			// Fill from VolumeAnalysisData
			for (int i = period; i > 0; i--)
				ringBuffer.Add(Math.Abs(this.HistoricalData[i].VolumeAnalysisData.Total.Delta));
		}

	}

	private void FillVolumeBuffer()
	{
		if (this.Count < this._LoockBackWindow_VDtV)
			return;

		for (int i = this._LoockBackWindow_VDtV; i > 0; i--)
			this._VolumeBuffer.Add(this.HistoricalData[i][PriceType.Volume]);
	}

	private double GetTickBasedDelta()
	{
		// Return simple count difference
		return (double)(_currentBarUpTicks - _currentBarDownTicks);
	}

	private double GetHistoricalTickDelta(int barsAgo)
	{
		// Get tick delta for bar at index [barsAgo]
		// For bar at index [1] (most recent completed bar), use Count - 1
		int barKey = this.Count - barsAgo;
		
		if (_historicalTickDeltas.TryGetValue(barKey, out double delta))
			return delta;
		
		// If not found (during warmup or initialization), return 0
		return 0.0;
	}

	private void ResetTickCounters()
	{
		_currentBarUpTicks = 0;
		_currentBarDownTicks = 0;
		_lastTickPrice = double.NaN;
	}

	private void ProcessTick(double currentPrice)
	{
		// First tick of the bar - use bar open as reference
		if (double.IsNaN(_lastTickPrice))
		{
			_lastTickPrice = this.HistoricalData[0][PriceType.Open];
			Core.Instance.Loggers.Log($"[DeltaIndicators][TickInit] First tick of bar - initialized at {_lastTickPrice}", LoggingLevel.Trading);
			return;
		}
		
		// Compare current tick to previous tick
		if (currentPrice > _lastTickPrice)
		{
			_currentBarUpTicks++;
			Core.Instance.Loggers.Log($"[DeltaIndicators][Tick] UP: {_lastTickPrice} → {currentPrice}, Total Up={_currentBarUpTicks}", LoggingLevel.Trading);
		}
		else if (currentPrice < _lastTickPrice)
		{
			_currentBarDownTicks++;
			Core.Instance.Loggers.Log($"[DeltaIndicators][Tick] DOWN: {_lastTickPrice} → {currentPrice}, Total Down={_currentBarDownTicks}", LoggingLevel.Trading);
		}
		// If currentPrice == _lastTickPrice, don't count (unchanged tick)
		
		_lastTickPrice = currentPrice;
	}

	private static double ComputeMedian(double[] values)
		{
			if (values == null || values.Length == 0)
				return double.NaN;

			var filtered = values.Where(v => !double.IsNaN(v) && !double.IsInfinity(v)).ToArray();
			if (filtered.Length == 0)
				return double.NaN;

			Array.Sort(filtered);
			int mid = filtered.Length / 2;
			if (filtered.Length % 2 == 0)
				return (filtered[mid - 1] + filtered[mid]) / 2.0;

			return filtered[mid];
		}

		private void FillPriceBuffer()
		{
			if (this.Count < this._LoockBackWindow)
				return; // Not enough bars to seed the buffer

			for (int i = this._LoockBackWindow;  i > 0; i--)
			{
				this._PriceBuffer.Add(Math.Abs(this.HistoricalData[i][PriceType.Close] - this.HistoricalData[i][PriceType.Open]));

			}
		}
	}
}


