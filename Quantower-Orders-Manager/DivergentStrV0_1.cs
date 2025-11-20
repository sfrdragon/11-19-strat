using DivergentStrV0_1.OperationSystemAdv;
using DivergentStrV0_1.Strategies;
using DivergentStrV0_1.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using TradingPlatform.BusinessLayer;


namespace DivergentStrV0_1
{
    public class DivergentStrV0_1 : Strategy
    {
        #region ====== Keys for Settings UI ======
        private const string KEY_STRAT = "######## Strategy Settings ######";
        private const string KEY_ENTRY = "######## Entry Conditions ######";
        private const string KEY_EXIT = "######## Exit Conditions ######";
        private const string KEY_ATR = "######## Atr Indicator Settings ######";
        private const string KEY_DELTA = "######## Delta Settings ######";
        private const string KEY_SESS = "######## Sessions Settings ######";
        private const string KEY_SESS_COUNT = "######## Custom sessions count ######";
        private const string KEY_SESS_USEDEFAULT = "Use Default Sessions";
        private const string KEY_SNAPSHOT = "######## Snapshot Settings ######";
        #endregion

        #region ====== UI Toggles ======
        private int _uiShowEnv = 1;     // 0=hide, 1=show
        private int _uiShowStrat = 1;   // 0=hide, 1=show
        private int _uiShowAtr = 0;     // 0=hide, 1=show
        private int _uiShowDelta = 0;   // 0=hide, 1=show
        private int _uiShowSnapshots = 0;   // 0=hide, 1=show
        #endregion

        #region ====== ATR Settings (UI) ======
        private int _uiAtrLenRvol = 14;         // Dedicated ATR length for RVOL normalization
        private int _uiAtrLenHma = 14;          // Dedicated ATR length for HMA calculations
        private int _uiAtrNormalize = 1;        // 0=off, 1=on
        private double _uiAtrSlopeThr = 0.015;
        private double _uiAtrSlippageMultiplier = 1.0;

        private int _uiRvolShortLen = 14;
        private int _uiRvolLongLen = 60;

        private int _uiHmaUsePrice = 1;                     // 0=use volume, 1=use price
        private int _uiHmaLenComposite = 14;
        private int _uiHmaLenPure = 14;
        private int _uiUseAtrScaledHma = 0;                 // 0=off, 1=on
        private int _uiUseCompositeHmaForDirection = 0;     // 0=off, 1=on
        #endregion

        #region ====== Delta Settings (UI) ======
        private int _uiDeltaUseMedian = 0;      // 0=mean, 1=median
        private int _uiDeltaLookback = 30;
        private double _uiDeltaThresholdMult = 2.0;
        private int _uiDeltaStrengthLookback = 30;
        private double _uiDeltaStrengthMult = 2.0;

        //TODO: [DEBUG] Validate Delta divergence multiplier impact

        private double _uiDeltaDivergenceMult = 1.0;
        private double _uiDeltaVDtVMult = 2.0;
        private int _uiVDtVLookback = 30;
        private int _uiForceVolumeReady = 0;        // 0=off, 1=on
        private int _uiUseTickBasedDelta = 0;       // 0=off, 1=on
        #endregion

        #region ====== Sessions ======
        private int _CustomSessionsCount = 0;
        private int _UseDefaultSessions = 1;        // 0=custom, 1=default
        private int _UtcOffsetHours = -4;           // Default UTC-4 (EDT), change to -5 for EST
        private List<SimpleSessionUtc> _CustomSessions = new List<SimpleSessionUtc>();
        private Dictionary<int, List<DayOfWeek>> _sessionDays = new Dictionary<int, List<DayOfWeek>>();
        #endregion

        #region ====== Strategy Parameters ======
        private double _quantity = 1000.0;
        private double _minSlInTicks = 20.0;
        private double _maxSlInTicks = 500.0;
        private double _minTpInTicks = 20.0;
        private double _maxTpInTicks = 1000.0;
        private int _uiSlMode = 0;              // 0=PrevCandle, 1=AtrDistance, 2=AtrTrailing
        private int _uiAtrTrailingMult = 2;     // For ATR trailing mode
        private int _uiTpMode = 0;              // 0=Dynamic, 1=Fixed
        private int _uiFixedTpInTicks = 50;     // For fixed TP mode
        private int _debugMode = 0;             // 0=off, 1=on
        private int _maxOpen = 3;
        private double _maxSessionLossUsd = 100.0;
        private int _verbosityFrequency = 3;
        private int _slippageAtrPeriod = 14;
        private int _enableHeavyMetrics = 1;  // 0=disabled, 1=enabled (default ON)
        private int _exposureReconcileInterval = 0;
        
        // Offset Entry Parameters
        private int _useOffsetEntry = 0;            // 0=off, 1=on
        private int _useOffsetLimitOrders = 0;      // 0=market, 1=limit
        private double _offsetAtrMultiplier = 0.25;
        private int _offsetAtrPeriod = 14;
        
        private int _entryUseRVOL = 1;          // 0=off, 1=on
        private int _entryUseVDPS = 1;          // 0=off, 1=on
        private int _entryUseVDStrong = 1;      // 0=off, 1=on
        private int _entryUseHMA = 1;           // 0=off, 1=on
        private int _entryUseVDtV = 0;          // 0=off, 1=on
        private int _entryUseVDP = 0;           // 0=off, 1=on
        private int _entryMinConditions = 2;

        private int _exitUseRVOL = 1;           // 0=off, 1=on
        private int _exitUseVDPS = 1;           // 0=off, 1=on
        private int _exitUseVDStrong = 1;       // 0=off, 1=on
        private int _exitUseHMA = 1;            // 0=off, 1=on
        private int _exitUseVDtV = 0;           // 0=off, 1=on
        private int _exitUseVDP = 1;            // 0=off, 1=on
        private int _exitMinConditions = 1;
        #endregion

        #region ====== Snapshot Settings ======
        private string _uiSnapshotFileName = "snapshot";
        private int _uiSnapshotSaveRequest = 0;     // 0=no, 1=save
        private string _uiSnapshotLoadFileName = string.Empty;
        private int _uiSnapshotLoadRequest = 0;     // 0=no, 1=load
        private string _uiSnapshotFolderPath = SnapshotDirectoryRoot;
        #endregion

        private static readonly string SnapshotExtension = ".json";
        private static readonly string SnapshotDirectoryRoot =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                         "Quantower", "StrategySnapshots", "DivergentStrV0_1");

        private static readonly HashSet<string> SnapshotTransientNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            KEY_SNAPSHOT,
            nameof(_uiSnapshotSaveRequest),
            nameof(_uiSnapshotLoadFileName),
            nameof(_uiSnapshotLoadRequest),
            nameof(_uiSnapshotFolderPath)
        };

        #region ====== Lot System Parameters ======
        private int _useLotSystem = 0;          // 0=off, 1=on
        private int _lotMin = 0;
        private int _lotStep = 0;
        // Cache of last-published values to detect changes in OnSettingsUpdated
        private int _lastLotMinPublished = int.MinValue;
        private int _lastLotStepPublished = int.MinValue;
        private int _lastUseLotSystemPublished = 0;
        private string ComputeLotSelectionBaseAsset()
        {
            try
            {
                double lotSize = this._Symbol != null ? this._Symbol.LotSize : 1.0;
                double minLot = this._Symbol != null ? this._Symbol.MinLot : 0.0;
                double value = this._Symbol != null ? this._Symbol.Last : -1;
                return $"(Size) {lotSize}:(Min) {minLot}";
            }
            catch
            {
                return $"Data Are Missing";
            }
        }
        #endregion

        #region ====== Input Parameters (Quantower Integration) ======
        [InputParameter("Symbol", 0)]
        public Symbol _Symbol;

        [InputParameter("Account", 1)]
        public Account _Account;

        [InputParameter("From Time (UTC)", 2)]
        public DateTime _fomTime = DateTime.UtcNow.AddDays(-30.0);

        [InputParameter("Async Mode", 3)]
        public int _inputDebugMode = 0;         // 0=off, 1=on

        [InputParameter("Period", 4)]
        public Period _period = Period.MIN1;
        #endregion

        #region ====== Runtime State ======
        private HistoricalData hd;
        private Indicator AtrIndicator;
        private Indicator DeltaIndicato;
        private RowanStrategy _strategy;
        private IConditionable _conditionable = new RowanStrategy();
        private bool Debug = false;
        private bool readyToGo;
        #endregion

        #region ====== Public Properties ======
        public double Quantity => _quantity;
        public double MinSlInTicks => _minSlInTicks;
        public double MaxSlInTicks => _maxSlInTicks;
        public double MinTpInTicks => _minTpInTicks;
        public double MaxTpInTicks => _maxTpInTicks;
        public bool DebugMode => Debug;
        public IReadOnlyList<SimpleSessionUtc> CustomSessions => _CustomSessions.AsReadOnly();
        public int CustomSessionsCount => _CustomSessionsCount;
        public int UseDefaultSessions
        {
            get => _UseDefaultSessions;
            set => _UseDefaultSessions = value;
        }


        private double procesPercent => hd?.VolumeAnalysisCalculationProgress?.ProgressPercent ?? 0.0;
        private bool volumesLoaded => hd?.VolumeAnalysisCalculationProgress?.ProgressPercent == 100;
        #endregion

        #region ====== Constructor ======
        public DivergentStrV0_1()
        {
            this.Name = this._conditionable.StrategyName;
            this.Description = "Rowan Strategy";
        }
        #endregion

        #region ====== Lifecycle ======
        protected override void OnCreated()
        {
            AppLog.System("DivergentStr", "Lifecycle", "OnCreated");
        }

        protected override void OnRun()
        {
            AppLog.System("DivergentStr", "Lifecycle", string.Format("OnRun entered | AppDomain: {0}", AppDomain.CurrentDomain.FriendlyName));
            //TODO: [DEBUG] Verify indicator catalog availability before creating instances.
            this.AtrIndicator = Core.Instance.Indicators.CreateIndicator(
                Core.Instance.Indicators.All.FirstOrDefault(x => x.Name == "RVOL (evolved)"));

            this.DeltaIndicato = Core.Instance.Indicators.CreateIndicator(
                Core.Instance.Indicators.All.FirstOrDefault(x => x.Name == "DeltaBasedIndicators"));
            //TODO: [DEBUG] Confirm ATR indicator settings align with strategy thresholds.
            this.AtrIndicator.Settings = new List<SettingItem>
            {
                new SettingItemInteger("Short Length", _uiRvolShortLen),
                new SettingItemInteger("Long Length", _uiRvolLongLen),
                new SettingItemDouble("Slope Threshold (norm.)", _uiAtrSlopeThr),
                new SettingItemInteger("Use Price for HMA", _uiHmaUsePrice) { Minimum = 0, Maximum = 1 },
                new SettingItemInteger("HMA Length (Composite)", _uiHmaLenComposite),
                new SettingItemInteger("HMA Length (Pure)", _uiHmaLenPure),
                new SettingItemInteger("Use Composite HMA for Direction", _uiUseCompositeHmaForDirection) { Minimum = 0, Maximum = 1 },
                new SettingItemInteger("Use ATR-scaled HMA", _uiUseAtrScaledHma) { Minimum = 0, Maximum = 1 },
                new SettingItemInteger("Use ATR Normalization", _uiAtrNormalize) { Minimum = 0, Maximum = 1 },
                new SettingItemInteger("ATR Length (RVOL)", _uiAtrLenRvol),
                new SettingItemInteger("ATR Length (HMA)", _uiAtrLenHma)
            };
            //TODO: [DEBUG] Cross-check Delta indicator configuration against UI state.
            this.DeltaIndicato.Settings = new List<SettingItem>
            {
                new SettingItemInteger("Force Volume Ready", 1) { Minimum = 0, Maximum = 1 },
                new SettingItemInteger("Use Tick-Based Delta", _uiUseTickBasedDelta) { Minimum = 0, Maximum = 1 },
                new SettingItemInteger("Delta: Use Median", _uiDeltaUseMedian) { Minimum = 0, Maximum = 1 },
                new SettingItemInteger("Delta: Lookback", _uiDeltaLookback),
                new SettingItemDouble("Delta: Threshold Multiplier", _uiDeltaThresholdMult),
                new SettingItemInteger("Delta Strength: Lookback", _uiDeltaStrengthLookback),
                new SettingItemDouble("Delta Strength: Threshold Multiplier", _uiDeltaStrengthMult),
                new SettingItemDouble("VD Divergence: Threshold Multiplier", _uiDeltaDivergenceMult),
                new SettingItemInteger("VDtV Lookback", _uiVDtVLookback),
                new SettingItemDouble("VDtV Threshold Multiplier", _uiDeltaVDtVMult)
            };
            //TODO: [DEBUG] Validate history request parameters before strategy initialization.
            if (!_conditionable.Initialized)
            {
                var req = new HistoryRequestParameters
                {
                    Aggregation = new HistoryAggregationTime(_period, HistoryType.Last),
                    FromTime = _fomTime,
                    ToTime = default,
                    Symbol = _Symbol
                };
                //TODO: [DEBUG] Monitor session registration to avoid duplicated windows.
                if (_UseDefaultSessions != 0 || _CustomSessionsCount == 0 || _CustomSessions.Count == 0)
                {
                    // Use UTC offset for OffMarketUtc (TP target levels)
                    foreach (var s in OffMarketUtc.Build())
                        StaticSessionManager.AddSession(s, Utils.SessionType.Target);

                    // CRITICAL: InMarketUtc uses automatic DST detection for 5-6 PM ET break
                    // No manual offset needed - automatically adjusts for EST/EDT
                    foreach (var sv in InMarketUtc.Build())
                        StaticSessionManager.AddSession(sv, Utils.SessionType.Trade);
                    
                    AppLog.System("DivergentStr", "SessionSetup",
                        $"Default sessions registered with Auto-DST (Break: 5-6 PM ET)");
                }
                else
                {
                    foreach (var cs in _CustomSessions)
                        StaticSessionManager.AddSession(cs, Utils.SessionType.Trade);

                    foreach (var sx in OffMarketUtc.Build())
                        StaticSessionManager.AddSession(sx, Utils.SessionType.Target);
                }
                //TODO: [DEBUG] Ensure RowanStrategy dependencies are resolved prior to construction.
                // Compute effective quantity: either direct value or lot-based
                double lotQuantity = _useLotSystem != 0 ? Math.Max(0, (double)(_lotMin + _lotStep*this._Symbol.LotStep)) : 0;
                if (lotQuantity > 0 && lotQuantity < this._Symbol.MinLot)
                    lotQuantity = this._Symbol.MinLot;

                _strategy = new RowanStrategy(
                    this.DeltaIndicato,
                    this.AtrIndicator,
                    _maxOpen,
                    _quantity,
                    _maxSessionLossUsd,
                    _verbosityFrequency,
                    Math.Max(1, _slippageAtrPeriod),
                    lotQuantity,
                    _useOffsetEntry,
                    _useOffsetLimitOrders,
                    _offsetAtrMultiplier,
                    _offsetAtrPeriod,
                    _exposureReconcileInterval,
                    _UtcOffsetHours);  // Pass UTC offset for session time calculations
                //TODO: [DEBUG] Review entry/exit signal mappings whenever UI flags change.
                var entryLineNames = new List<string>();
                if (_entryUseRVOL != 0) entryLineNames.Add("RvolSignal");
                if (_entryUseHMA != 0) entryLineNames.Add("HMA_Direction");
                if (_entryUseVDPS != 0) entryLineNames.Add("APAVD_Flag");
                if (_entryUseVDStrong != 0) entryLineNames.Add("VD_Strength_Flag");
                if (_entryUseVDtV != 0) entryLineNames.Add("VD_to_Volume_Flag");
                if (_entryUseVDP != 0) entryLineNames.Add("VD_Price_Divergent_Flag");

                var exitLineNames = new List<string>();
                if (_exitUseRVOL != 0) exitLineNames.Add("RvolSignal");
                if (_exitUseHMA != 0) exitLineNames.Add("HMA_Direction");
                if (_exitUseVDPS != 0) exitLineNames.Add("APAVD_Flag");
                if (_exitUseVDStrong != 0) exitLineNames.Add("VD_Strength_Flag");
                if (_exitUseVDtV != 0) exitLineNames.Add("VD_to_Volume_Flag");
                if (_exitUseVDP != 0) exitLineNames.Add("VD_Price_Divergent_Flag");

                _strategy.ConfigureConditions(entryLineNames, _entryMinConditions, exitLineNames, _exitMinConditions);

                _strategy.InjectStrategy(new RowanSlTpStrategy(
                    (int)Math.Round(_minSlInTicks),
                    (int)Math.Round(_maxSlInTicks))
                {
                    MinTpInTicks = (int)Math.Max(1, Math.Round(_minTpInTicks)),
                    MaxTpInTicks = (int)Math.Max(Math.Max(1, Math.Round(_minTpInTicks)), Math.Max(1, Math.Round(_maxTpInTicks))),
                    AtrSlippageMultiplier = Math.Max(0.0, Math.Min(2.0, _uiAtrSlippageMultiplier)),
                    SlModeType = (SlMode)Math.Max(0, Math.Min(2, _uiSlMode)),
                    AtrTrailingMultiplier = Math.Max(1, Math.Min(10, _uiAtrTrailingMult)),
                    TpModeType = (TpMode)Math.Max(0, Math.Min(1, _uiTpMode)),
                    FixedTpInTicks = Math.Max(1, Math.Min(1000, _uiFixedTpInTicks))
                });

                _strategy.Init(req, _Account, _inputDebugMode != 0, "", _enableHeavyMetrics != 0);
                //StrategyLogHub.Publish("DivergentStr", $"Strategy initialized | AppDomain: {AppDomain.CurrentDomain.FriendlyName} | UseDefaultSessions: {_UseDefaultSessions}", LoggingLevel.System);
                _conditionable = _strategy;
            }
        }

        protected override void OnStop()
        {
            readyToGo = false;
            //TODO: [DEBUG] Verify disposal pipeline releases indicator references.
            StaticSessionManager.Dispose();
            _conditionable?.Dispose();
        }

        protected override void OnRemove()
        {
            AppLog.System("DivergentStr", "OnRemove", "Strategy removal - final cleanup");
            
            try
            {
                // Ensure disposal called (defensive - already called in OnStop, but safe to repeat)
                try
                {
                    StaticSessionManager.Dispose();
                }
                catch { }
                
                try
                {
                    _conditionable?.Dispose();
                }
                catch { }
                
                // Clear session collections with null guards
                try
                {
                    _CustomSessions?.Clear();
                }
                catch { }
                
                try
                {
                    _sessionDays?.Clear();
                }
                catch { }
                
                // Nullify indicator references (help GC) with null guards
                try
                {
                    this.AtrIndicator?.Dispose();
                    this.AtrIndicator = null;
                }
                catch { }
                
                try
                {
                    this.DeltaIndicato?.Dispose();
                    this.DeltaIndicato = null;
                }
                catch { }
                
                _conditionable = null;
                _strategy = null;
                
                AppLog.System("DivergentStr", "OnRemove", "Cleanup complete");
            }
            catch (Exception ex)
            {
                AppLog.Error("DivergentStr", "OnRemove", $"Error during cleanup: {ex.Message}");
            }
        }


        public override IList<SettingItem> Settings
        {
            get
            {
                var settings = base.Settings;

                #region ===== 100x ï¿½ Sessions =====
                settings.Add(new SettingItemInteger(KEY_SESS, 0)
                {
                    Text = KEY_SESS,
                    SortIndex = 1000,
                    Minimum = 0,
                    Maximum = 1
                });

                settings.Add(new SettingItemInteger(KEY_SESS_COUNT, 0)
                {
                    Text = KEY_SESS_COUNT,
                    SortIndex = 1000,
                    Minimum = 0,
                    Maximum = 3,
                    Relation = new SettingItemRelationVisibility(KEY_SESS, 1)
                });

                settings.Add(new SettingItemInteger(KEY_SESS_USEDEFAULT, _UseDefaultSessions)
                {
                    Text = KEY_SESS_USEDEFAULT,
                    SortIndex = 1000,
                    Minimum = 0,
                    Maximum = 1,
                    Value = _UseDefaultSessions
                });

                settings.Add(new SettingItemInteger("UTC Offset Hours", _UtcOffsetHours)
                {
                    Text = "UTC Offset Hours (e.g. -4 for EDT, -5 for EST)",
                    SortIndex = 1001,
                    Minimum = -12,
                    Maximum = 12,
                    Value = _UtcOffsetHours,
                    Relation = new SettingItemRelationVisibility(KEY_SESS_USEDEFAULT, 1)
                });

                for (int i = 0; i < 3; i++)
                {
                    SettingItemRelationVisibility relation =
                        (i == 0) ? new SettingItemRelationVisibility(KEY_SESS_COUNT, 1, 2, 3) :
                        (i == 1) ? new SettingItemRelationVisibility(KEY_SESS_COUNT, 2, 3) :
                                   new SettingItemRelationVisibility(KEY_SESS_COUNT, 3);

                    SimpleSessionUtc current = (this._CustomSessions.Count > i) ? this._CustomSessions[i] : null;

                    // Use sensible defaults with UTC offset (prevents "same start and end time" warnings)
                    // Default: 9:30 AM - 5:00 PM in user's specified timezone (before 5-6 PM break)
                    int utcStart = 9 * 60 + 30 - (_UtcOffsetHours * 60); // 9:30 AM adjusted to UTC
                    int utcEnd = 17 * 60 - (_UtcOffsetHours * 60);       // 5:00 PM adjusted to UTC
                    DateTime sessionStart = DateTime.Today.AddMinutes(utcStart);
                    DateTime sessionEnd = DateTime.Today.AddMinutes(utcEnd);

                    if (current != null)
                    {
                        sessionStart = DateTime.Today.AddHours(current.Open.Hour).AddMinutes(current.Open.Minute);
                        sessionEnd = DateTime.Today.AddHours(current.Close.Hour).AddMinutes(current.Close.Minute);
                    }

                    settings.Add(new SettingItemDateTime($"session{i + 1}Start", sessionStart)
                    {
                        Text = $"Session {i + 1} start (UTC)",
                        SortIndex = 1001 + i * 2,
                        Relation = relation
                    });

                    settings.Add(new SettingItemDateTime($"session{i + 1}End", sessionEnd)
                    {
                        Text = $"Session {i + 1} end (UTC)",
                        SortIndex = 1002 + i * 2,
                        Relation = relation
                    });

                    foreach (DayOfWeek day in Enum.GetValues(typeof(DayOfWeek)))
                    {
                        // Read saved day value from _sessionDays dictionary
                        int dayValue = 1; // Default to enabled if not found
                        if (_sessionDays.ContainsKey(i) && _sessionDays[i] != null)
                        {
                            dayValue = _sessionDays[i].Contains(day) ? 1 : 0;
                        }

                        settings.Add(new SettingItemInteger($"session{i + 1}On{day}", dayValue)
                        {
                            Text = $"Session {i + 1} on {day}",
                            SortIndex = 1003 + i * 2,
                            Minimum = 0,
                            Maximum = 1,
                            Relation = relation
                        });
                    }
                }
                #endregion

                #region ===== 300x ï¿½ Strategy =====
                settings.Add(new SettingItemInteger(KEY_STRAT, _uiShowStrat)
                {
                    Text = KEY_STRAT,
                    SortIndex = 3000,
                    Minimum = 0,
                    Maximum = 1
                });

                settings.Add(new SettingItemDouble("Quantity", _quantity)
                {
                    Text = "Quantity",
                    SortIndex = 3001,
                    Minimum = 0.0001,
                    Maximum = 1_000_000,
                    Increment = 0.0001,
                    Relation = new SettingItemRelationVisibility(KEY_STRAT, 1)
                });

                settings.Add(new SettingItemDouble("Min SL In Ticks", _minSlInTicks)
                {
                    Text = "Min SL In Ticks",
                    SortIndex = 3003,
                    Minimum = 1,
                    Maximum = double.MaxValue,
                    Increment = 1,
                    Relation = new SettingItemRelationVisibility(KEY_STRAT, 1)
                });

                settings.Add(new SettingItemDouble("Max SL In Ticks", _maxSlInTicks)
                {
                    Text = "Max SL In Ticks",
                    SortIndex = 3004,
                    Minimum = 1,
                    Maximum = double.MaxValue,
                    Increment = 1,
                    Relation = new SettingItemRelationVisibility(KEY_STRAT, 1)
                });

                settings.Add(new SettingItemInteger("SL Mode", _uiSlMode)
                {
                    Text = "SL Mode (0=Prev Candle, 1=ATR Distance, 2=ATR Trailing)",
                    SortIndex = 3005,
                    Minimum = 0,
                    Maximum = 2,
                    Relation = new SettingItemRelationVisibility(KEY_STRAT, 1)
                });

                settings.Add(new SettingItemInteger("ATR Trailing Multiplier", _uiAtrTrailingMult)
                {
                    Text = "ATR Trailing Multiplier (for SL mode 2)",
                    SortIndex = 3005,
                    Minimum = 1,
                    Maximum = 10,
                    Relation = new SettingItemRelationVisibility("SL Mode", 2)
                });

                settings.Add(new SettingItemDouble("Min Tp In Ticks", _minTpInTicks)
                {
                    Text = "Min TP In Ticks",
                    SortIndex = 3006,
                    Minimum = 1,
                    Maximum = double.MaxValue,
                    Increment = 1,
                    Relation = new SettingItemRelationVisibility(KEY_STRAT, 1)
                });

                settings.Add(new SettingItemDouble("Max Tp In Ticks", _maxTpInTicks)
                {
                    Text = "Max TP In Ticks",
                    SortIndex = 3007,
                    Minimum = 1,
                    Maximum = double.MaxValue,
                    Increment = 1,
                    Relation = new SettingItemRelationVisibility(KEY_STRAT, 1)
                });

                settings.Add(new SettingItemInteger("TP Mode", _uiTpMode)
                {
                    Text = "TP Mode (0=Dynamic/Session Levels, 1=Fixed Distance)",
                    SortIndex = 3007,
                    Minimum = 0,
                    Maximum = 1,
                    Relation = new SettingItemRelationVisibility(KEY_STRAT, 1)
                });

                settings.Add(new SettingItemInteger("Fixed TP Distance (ticks)", _uiFixedTpInTicks)
                {
                    Text = "Fixed TP Distance (ticks)",
                    SortIndex = 3007,
                    Minimum = 1,
                    Maximum = 1000,
                    Relation = new SettingItemRelationVisibility("TP Mode", 1)
                });

                settings.Add(new SettingItemInteger("Max Open Positions", _maxOpen)
                {
                    Text = "Max Open Positions",
                    SortIndex = 3008,
                    Minimum = 1,
                    Maximum = 100,
                    Relation = new SettingItemRelationVisibility(KEY_STRAT, 1)
                });

                settings.Add(new SettingItemDouble("Max Session Loss USD", _maxSessionLossUsd)
                {
                    Text = "Max Session Loss USD",
                    SortIndex = 3009,
                    Minimum = 1,
                    Maximum = double.MaxValue,
                    Increment = 0.01,
                    Relation = new SettingItemRelationVisibility(KEY_STRAT, 1)
                });

                settings.Add(new SettingItemInteger("Verbosity Frequency", _verbosityFrequency)
                {
                    Text = "Verbosity Frequency",
                    SortIndex = 3010,
                    Minimum = 0,
                    Maximum = 1000,
                    Relation = new SettingItemRelationVisibility(KEY_STRAT, 1)
                });

                settings.Add(new SettingItemInteger("Exposure Reconcile Interval", _exposureReconcileInterval)
                {
                    Text = "Exposure Reconcile Interval (ticks)",
                    SortIndex = 30105,
                    Minimum = 0,
                    Maximum = 10000,
                    Relation = new SettingItemRelationVisibility(KEY_STRAT, 1)
                });

                settings.Add(new SettingItemInteger("Slippage ATR Period", _slippageAtrPeriod)
                {
                    Text = "Slippage ATR Period",
                    SortIndex = 3011,
                    Minimum = 1,
                    Maximum = int.MaxValue,
                    Relation = new SettingItemRelationVisibility(KEY_STRAT, 1)
                });

                settings.Add(new SettingItemDouble("ATR Slippage Multiplier", _uiAtrSlippageMultiplier)
                {
                    Text = "ATR Slippage Multiplier",
                    SortIndex = 3012,
                    Minimum = 0.0,
                    Maximum = 2.0,
                    DecimalPlaces = 2,
                    Increment = 0.01,
                    Relation = new SettingItemRelationVisibility(KEY_STRAT, 1)
                });

                settings.Add(new SettingItemInteger("Enable Heavy Metrics", _enableHeavyMetrics)
                {
                    Text = "Enable Heavy Metrics",
                    Description = "Enable Sharpe Ratio, Profit StdDev, Max Drawdown calculations (slower but more detailed)",
                    SortIndex = 3013,
                    Minimum = 0,
                    Maximum = 1,
                    Increment = 1,
                    Relation = new SettingItemRelationVisibility(KEY_STRAT, 1)
                });
                
                // Offset Entry Settings
                settings.Add(new SettingItemInteger("Use Offset Entry", _useOffsetEntry)
                {
                    Text = "Use Offset Entry (ATR-based delay)",
                    SortIndex = 3016,
                    Minimum = 0,
                    Maximum = 1,
                    Relation = new SettingItemRelationVisibility(KEY_STRAT, 1)
                });

                settings.Add(new SettingItemInteger("Use Limit Orders for Offset", _useOffsetLimitOrders)
                {
                    Text = "Use Limit Orders (vs Market)",
                    SortIndex = 3017,
                    Minimum = 0,
                    Maximum = 1,
                    Relation = new SettingItemRelationVisibility("Use Offset Entry", 1)
                });

                settings.Add(new SettingItemDouble("Offset ATR Multiplier", _offsetAtrMultiplier)
                {
                    Text = "Offset ATR Multiplier",
                    SortIndex = 3018,
                    Minimum = 0.0,
                    Maximum = 5.0,
                    Increment = 0.05,
                    DecimalPlaces = 2,
                    Relation = new SettingItemRelationVisibility("Use Offset Entry", 1)
                });

                settings.Add(new SettingItemInteger("Offset ATR Period", _offsetAtrPeriod)
                {
                    Text = "Offset ATR Period",
                    SortIndex = 3019,
                    Minimum = 1,
                    Maximum = 200,
                    Relation = new SettingItemRelationVisibility("Use Offset Entry", 1)
                });
                
                // Lot System group
                settings.Add(new SettingItemInteger("Use Lot System", _useLotSystem)
                {
                    Text = "Use Lot System",
                    SortIndex = 3013,
                    Minimum = 0,
                    Maximum = 1,
                    Relation = new SettingItemRelationVisibility(KEY_STRAT, 1)
                });

                settings.Add(new SettingItemInteger("Lot Minimum", _lotMin)
                {
                    Text = "Lot Minimum",
                    SortIndex = 3014,
                    Minimum = 0,
                    Maximum = int.MaxValue,
                    Relation = new SettingItemRelationVisibility("Use Lot System", 1)
                });

                settings.Add(new SettingItemInteger("Lot Step", _lotStep)
                {
                    Text = "Lot Step",
                    SortIndex = 3015,
                    Minimum = 0,
                    Maximum = int.MaxValue,
                    Relation = new SettingItemRelationVisibility("Use Lot System", 1)
                });

                #endregion

                #region ===== 320x ï¿½ Entry Conditions =====
                settings.Add(new SettingItemInteger("######## Entry Conditions ######", 1)
                {
                    Text = "######## Entry Conditions ######",
                    SortIndex = 3020,
                    Minimum = 0,
                    Maximum = 1,
                    Relation = new SettingItemRelationVisibility(KEY_STRAT, 1)
                });

                settings.Add(new SettingItemInteger("Entry: Min Conditions", _entryMinConditions)
                {
                    Text = "Entry: Min Trade Signal",
                    SortIndex = 3021,
                    Minimum = 0,
                    Maximum = 6,
                    Relation = new SettingItemRelationVisibility("######## Entry Conditions ######", 1)
                });

                settings.Add(new SettingItemInteger("Entry Use: RVOL", _entryUseRVOL)
                {
                    Text = "Entry Use: RVOL ï¿½ Normalized RVOL momentum",
                    SortIndex = 3022,
                    Minimum = 0,
                    Maximum = 1,
                    Relation = new SettingItemRelationVisibility("######## Entry Conditions ######", 1)
                });

                settings.Add(new SettingItemInteger("Entry Use: VDPS", _entryUseVDPS)
                {
                    Text = "Entry Use: VDPS ï¿½ Price/Delta Ratio (APAVD)",
                    SortIndex = 3023,
                    Minimum = 0,
                    Maximum = 1,
                    Relation = new SettingItemRelationVisibility("######## Entry Conditions ######", 1)
                });

                settings.Add(new SettingItemInteger("Entry Use: VDstrong", _entryUseVDStrong)
                {
                    Text = "Entry Use: VDstrong ï¿½ Delta Strength (|VD| vs avg |VD|)",
                    SortIndex = 3024,
                    Minimum = 0,
                    Maximum = 1,
                    Relation = new SettingItemRelationVisibility("######## Entry Conditions ######", 1)
                });

                settings.Add(new SettingItemInteger("Entry Use: HMA", _entryUseHMA)
                {
                    Text = "Entry Use: HMA ï¿½ HMA Direction (Close vs HMA)",
                    SortIndex = 3025,
                    Minimum = 0,
                    Maximum = 1,
                    Relation = new SettingItemRelationVisibility("######## Entry Conditions ######", 1)
                });

                settings.Add(new SettingItemInteger("Entry Use: VDtV", _entryUseVDtV)
                {
                    Text = "Entry Use: VDtV ï¿½ Delta-to-Volume Ratio (|VD|/Volume)",
                    SortIndex = 3026,
                    Minimum = 0,
                    Maximum = 1,
                    Relation = new SettingItemRelationVisibility("######## Entry Conditions ######", 1)
                });

                settings.Add(new SettingItemInteger("Entry Use: VDP", _entryUseVDP)
                {
                    Text = "Entry Use: VDP ï¿½ VD-Price Divergence",
                    SortIndex = 3027,
                    Minimum = 0,
                    Maximum = 1,
                    Relation = new SettingItemRelationVisibility("######## Entry Conditions ######", 1)
                });
                #endregion

                #region ===== 330x ï¿½ Exit Conditions =====
                settings.Add(new SettingItemInteger("######## Exit Conditions ######", 1)
                {
                    Text = "######## Exit Conditions ######",
                    SortIndex = 3030,
                    Minimum = 0,
                    Maximum = 1,
                    Relation = new SettingItemRelationVisibility(KEY_STRAT, 1)
                });

                settings.Add(new SettingItemInteger("Exit: Min Conditions", _exitMinConditions)
                {
                    Text = "Exit: Min Close Signal",
                    SortIndex = 3031,
                    Minimum = 0,
                    Maximum = 6,
                    Relation = new SettingItemRelationVisibility("######## Exit Conditions ######", 1)
                });

                settings.Add(new SettingItemInteger("Exit Use: RVOL", _exitUseRVOL)
                {
                    Text = "Exit Use: RVOL Normalized RVOL momentum",
                    SortIndex = 3032,
                    Minimum = 0,
                    Maximum = 1,
                    Relation = new SettingItemRelationVisibility("######## Exit Conditions ######", 1)
                });

                settings.Add(new SettingItemInteger("Exit Use: VDPS", _exitUseVDPS)
                {
                    Text = "Exit Use: VDPS Price/Delta Ratio (APAVD)",
                    SortIndex = 3033,
                    Minimum = 0,
                    Maximum = 1,
                    Relation = new SettingItemRelationVisibility("######## Exit Conditions ######", 1)
                });

                settings.Add(new SettingItemInteger("Exit Use: VDstrong", _exitUseVDStrong)
                {
                    Text = "Exit Use: VDstrong Delta Strength (|VD| vs avg |VD|)",
                    SortIndex = 3034,
                    Minimum = 0,
                    Maximum = 1,
                    Relation = new SettingItemRelationVisibility("######## Exit Conditions ######", 1)
                });

                settings.Add(new SettingItemInteger("Exit Use: HMA", _exitUseHMA)
                {
                    Text = "Exit Use: HMA Direction (Close vs HMA)",
                    SortIndex = 3035,
                    Minimum = 0,
                    Maximum = 1,
                    Relation = new SettingItemRelationVisibility("######## Exit Conditions ######", 1)
                });

                settings.Add(new SettingItemInteger("Exit Use: VDtV", _exitUseVDtV)
                {
                    Text = "Exit Use: VDtV ï¿½ Delta-to-Volume Ratio (|VD|/Volume)",
                    SortIndex = 3036,
                    Minimum = 0,
                    Maximum = 1,
                    Relation = new SettingItemRelationVisibility("######## Exit Conditions ######", 1)
                });

                settings.Add(new SettingItemInteger("Exit Use: VDP", _exitUseVDP)
                {
                    Text = "Exit Use: VDP ï¿½ VD-Price Divergence",
                    SortIndex = 3037,
                    Minimum = 0,
                    Maximum = 1,
                    Relation = new SettingItemRelationVisibility("######## Exit Conditions ######", 1)
                });
                #endregion

                #region ===== 400x ï¿½ ATR =====
                settings.Add(new SettingItemInteger(KEY_ATR, _uiShowAtr)
                {
                    Text = KEY_ATR,
                    SortIndex = 4000,
                    Minimum = 0,
                    Maximum = 1
                });

                settings.Add(new SettingItemInteger(nameof(_uiRvolShortLen), _uiRvolShortLen)
                {
                    Text = "RVOL Short Window Length",
                    SortIndex = 4001,
                    Minimum = 2,
                    Maximum = 200,
                    Relation = new SettingItemRelationVisibility(KEY_ATR, 1)
                });

                settings.Add(new SettingItemInteger(nameof(_uiRvolLongLen), _uiRvolLongLen)
                {
                    Text = "RVOL Long Window Length",
                    SortIndex = 4002,
                    Minimum = 5,
                    Maximum = 500,
                    Relation = new SettingItemRelationVisibility(KEY_ATR, 1)
                });

                settings.Add(new SettingItemInteger(nameof(_uiAtrLenRvol), _uiAtrLenRvol)
                {
                    Text = "ATR Length (RVOL Entry Condition)",
                    SortIndex = 4003,
                    Minimum = 2,
                    Maximum = int.MaxValue,
                    Relation = new SettingItemRelationVisibility(KEY_ATR, 1)
                });

                settings.Add(new SettingItemInteger(nameof(_uiAtrLenHma), _uiAtrLenHma)
                {
                    Text = "ATR Length (HMA Composite Price Direction)",
                    SortIndex = 4004,
                    Minimum = 2,
                    Maximum = int.MaxValue,
                    Relation = new SettingItemRelationVisibility(KEY_ATR, 1)
                });

                // HMA-related settings
                settings.Add(new SettingItemInteger(nameof(_uiHmaUsePrice), _uiHmaUsePrice)
                {
                    Text = "Use Price for HMA",
                    SortIndex = 4005,
                    Minimum = 0,
                    Maximum = 1,
                    Relation = new SettingItemRelationVisibility(KEY_ATR, 1)
                });

                settings.Add(new SettingItemInteger(nameof(_uiHmaLenComposite), _uiHmaLenComposite)
                {
                    Text = "HMA Length (Composite)",
                    SortIndex = 4006,
                    Minimum = 2,
                    Maximum = int.MaxValue,
                    Relation = new SettingItemRelationVisibility(KEY_ATR, 1)
                });

                settings.Add(new SettingItemInteger(nameof(_uiHmaLenPure), _uiHmaLenPure)
                {
                    Text = "HMA Length (Pure)",
                    SortIndex = 4007,
                    Minimum = 2,
                    Maximum = int.MaxValue,
                    Relation = new SettingItemRelationVisibility(KEY_ATR, 1)
                });

                settings.Add(new SettingItemInteger(nameof(_uiUseCompositeHmaForDirection), _uiUseCompositeHmaForDirection)
                {
                    Text = "Use Composite HMA for Direction",
                    SortIndex = 4008,
                    Minimum = 0,
                    Maximum = 1,
                    Relation = new SettingItemRelationVisibility(KEY_ATR, 1)
                });

                settings.Add(new SettingItemInteger(nameof(_uiAtrNormalize), _uiAtrNormalize)
                {
                    Text = "Use ATR Normalization",
                    SortIndex = 4009,
                    Minimum = 0,
                    Maximum = 1,
                    Relation = new SettingItemRelationVisibility(KEY_ATR, 1)
                });

                settings.Add(new SettingItemDouble(nameof(_uiAtrSlopeThr), _uiAtrSlopeThr)
                {
                    Text = "Slope Threshold (norm.)",
                    SortIndex = 4010,
                    Minimum = 0.0,
                    Maximum = double.MaxValue,
                    DecimalPlaces = 1,
                    Increment = 0.1,
                    Relation = new SettingItemRelationVisibility(KEY_ATR, 1)
                });

                settings.Add(new SettingItemInteger(nameof(_uiUseAtrScaledHma), _uiUseAtrScaledHma)
                {
                    Text = "Use ATR-scaled HMA",
                    SortIndex = 4011,
                    Minimum = 0,
                    Maximum = 1,
                    Relation = new SettingItemRelationVisibility(KEY_ATR, 1)
                });
                #endregion

                #region ===== 500x ï¿½ Delta =====
                settings.Add(new SettingItemInteger(KEY_DELTA, _uiShowDelta)
                {
                    Text = KEY_DELTA,
                    SortIndex = 5000,
                    Minimum = 0,
                    Maximum = 1
                });

                settings.Add(new SettingItemInteger(nameof(_uiDeltaUseMedian), _uiDeltaUseMedian)
                {
                    Text = "Delta: Use Median",
                    SortIndex = 5001,
                    Minimum = 0,
                    Maximum = 1,
                    Relation = new SettingItemRelationVisibility(KEY_DELTA, 1)
                });

                settings.Add(new SettingItemInteger(nameof(_uiUseTickBasedDelta), _uiUseTickBasedDelta)
                {
                    Text = "Use Tick-Based Volume Delta (Up-ticks minus Down-ticks)",
                    SortIndex = 5001,
                    Minimum = 0,
                    Maximum = 1,
                    Relation = new SettingItemRelationVisibility(KEY_DELTA, 1)
                });

                settings.Add(new SettingItemInteger(nameof(_uiDeltaLookback), _uiDeltaLookback)
                {
                    Text = "Delta: Lookback",
                    SortIndex = 5002,
                    Minimum = 5,
                    Maximum = int.MaxValue,
                    Relation = new SettingItemRelationVisibility(KEY_DELTA, 1)
                });

                settings.Add(new SettingItemDouble(nameof(_uiDeltaThresholdMult), _uiDeltaThresholdMult)
                {
                    Text = "Delta: Threshold Multiplier",
                    SortIndex = 5003,
                    Minimum = 0.1,
                    Maximum = double.MaxValue,
                    Increment = 0.01,
                    DecimalPlaces = 2,
                    Relation = new SettingItemRelationVisibility(KEY_DELTA, 1)
                });

                settings.Add(new SettingItemInteger(nameof(_uiDeltaStrengthLookback), _uiDeltaStrengthLookback)
                {
                    Text = "Delta Strength: Lookback",
                    SortIndex = 5004,
                    Minimum = 5,
                    Maximum = int.MaxValue,
                    Relation = new SettingItemRelationVisibility(KEY_DELTA, 1)
                });

                settings.Add(new SettingItemDouble(nameof(_uiDeltaStrengthMult), _uiDeltaStrengthMult)
                {
                    Text = "Delta Strength: Threshold Multiplier",
                    SortIndex = 5005,
                    Minimum = 0.1,
                    Maximum = double.MaxValue,
                    Increment = 0.01,
                    DecimalPlaces = 2,
                    Relation = new SettingItemRelationVisibility(KEY_DELTA, 1)
                });

                settings.Add(new SettingItemDouble(nameof(_uiDeltaDivergenceMult), _uiDeltaDivergenceMult)
                {
                    Text = "VD Divergence: Threshold Multiplier",
                    SortIndex = 5006,
                    Minimum = 0.1,
                    Maximum = double.MaxValue,
                    Increment = 0.01,
                    DecimalPlaces = 2,
                    Relation = new SettingItemRelationVisibility(KEY_DELTA, 1)
                });

                settings.Add(new SettingItemDouble(nameof(_uiDeltaVDtVMult), _uiDeltaVDtVMult)
                {
                    Text = "VDtV Threshold Multiplier",
                    SortIndex = 5007,
                    Minimum = 0.1,
                    Maximum = double.MaxValue,
                    Increment = 0.01,
                    DecimalPlaces = 2,
                    Relation = new SettingItemRelationVisibility(KEY_DELTA, 1)
                });

                settings.Add(new SettingItemInteger(nameof(_uiVDtVLookback), _uiVDtVLookback)
                {
                    Text = "VDtV Lookback",
                    SortIndex = 5008,
                    Minimum = 5,
                    Maximum = int.MaxValue,
                    Relation = new SettingItemRelationVisibility(KEY_DELTA, 1)
                });
                #endregion

                #region ===== 600x Snapshot =====
                settings.Add(new SettingItemInteger(KEY_SNAPSHOT, _uiShowSnapshots)
                {
                    Text = KEY_SNAPSHOT,
                    SortIndex = 6000,
                    Minimum = 0,
                    Maximum = 1
                });

                settings.Add(new SettingItemString(nameof(_uiSnapshotFileName), _uiSnapshotFileName)
                {
                    Text = "Snapshot: File name (without extension)",
                    SortIndex = 6001,
                    Relation = new SettingItemRelationVisibility(KEY_SNAPSHOT, 1)
                });

                settings.Add(new SettingItemInteger(nameof(_uiSnapshotSaveRequest), _uiSnapshotSaveRequest)
                {
                    Text = "Snapshot: Save current settings",
                    SortIndex = 6002,
                    Minimum = 0,
                    Maximum = 1,
                    Relation = new SettingItemRelationVisibility(KEY_SNAPSHOT, 1)
                });

                settings.Add(new SettingItemString(nameof(_uiSnapshotFolderPath), _uiSnapshotFolderPath)
                {
                    Text = "Snapshot: Directory path (read-only)",
                    SortIndex = 6003,
                    Relation = new SettingItemRelationVisibility(KEY_SNAPSHOT, 1)
                });

                settings.Add(new SettingItemString(nameof(_uiSnapshotLoadFileName), _uiSnapshotLoadFileName)
                {
                    Text = "Snapshot: File name to load",
                    SortIndex = 6004,
                    Relation = new SettingItemRelationVisibility(KEY_SNAPSHOT, 1)
                });

                settings.Add(new SettingItemInteger(nameof(_uiSnapshotLoadRequest), _uiSnapshotLoadRequest)
                {
                    Text = "Snapshot: Apply selected file",
                    SortIndex = 6005,
                    Minimum = 0,
                    Maximum = 1,
                    Relation = new SettingItemRelationVisibility(KEY_SNAPSHOT, 1)
                });
                #endregion

                return settings;
            }

            set
            {
                base.Settings = value;

                try
                {
                    // ===== Sessions =====
                    if (value.TryGetValue(KEY_SESS_COUNT, out int sessCount))
                        _CustomSessionsCount = Math.Max(0, Math.Min(3, sessCount));

                    if (value.TryGetValue(KEY_SESS_USEDEFAULT, out int useDefault))
                        _UseDefaultSessions = Math.Max(0, Math.Min(1, useDefault));

                    if (value.TryGetValue("UTC Offset Hours", out int utcOffset))
                        _UtcOffsetHours = Math.Max(-12, Math.Min(12, utcOffset));

                    _CustomSessions.Clear();
                    _sessionDays.Clear();


                    TimeZoneInfo _etZone;

                    try
                    {
                        // Windows:
                        _etZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
                    }
                    catch
                    {
                        // Linux / macOS / Docker:
                        _etZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
                    }

                    TradingPlatform.BusinessLayer.TimeZone selectedZone = default;
                    bool hasSelectedZone = false;
                    try
                    {
                        selectedZone = Core.Instance.TimeUtils.SelectedTimeZone;
                        hasSelectedZone = true;
                    }
                    catch
                    {
                        hasSelectedZone = false;
                    }

                    TimeZoneInfo localZone;
                    try { localZone = TimeZoneInfo.Local; }
                    catch { localZone = _etZone; }

                    DateTime ConvertToUtc(DateTime value)
                    {
                        if (value.Kind == DateTimeKind.Utc)
                            return value;

                        if (value.Kind == DateTimeKind.Local)
                        {
                            try
                            {
                                return value.ToUniversalTime();
                            }
                            catch
                            {
                                // fall through
                            }
                        }

                        var unspecified = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);

                        if (hasSelectedZone)
                        {
                            try
                            {
                                return Core.Instance.TimeUtils.ConvertFromTimeZoneToUTC(unspecified, selectedZone);
                            }
                            catch
                            {
                                // fall through to system conversion
                            }
                        }

                        try
                        {
                            return TimeZoneInfo.ConvertTimeToUtc(unspecified, localZone);
                        }
                        catch
                        {
                            return TimeZoneInfo.ConvertTimeToUtc(unspecified, _etZone);
                        }
                    }

                    for (int i = 0; i < _CustomSessionsCount; i++)
                    {
                        try
                        {
                            // Default to 9:30 AM - 5:00 PM in user's specified timezone (before 5-6 PM break)
                            int utcStartMin = 9 * 60 + 30 - (_UtcOffsetHours * 60); // 9:30 AM adjusted to UTC
                            int utcEndMin = 17 * 60 - (_UtcOffsetHours * 60);       // 5:00 PM adjusted to UTC
                            DateTime startDateTime = DateTime.UtcNow.Date.AddMinutes(utcStartMin); 
                            DateTime endDateTime = DateTime.UtcNow.Date.AddMinutes(utcEndMin);
                            //fix change timezone to ET





                            if (value.TryGetValue($"session{i + 1}Start", out DateTime startDt))
                                startDateTime = ConvertToUtc(startDt);

                            if (value.TryGetValue($"session{i + 1}End", out DateTime endDt))
                                endDateTime = ConvertToUtc(endDt);

                            TimeOnly start = TimeOnly.FromDateTime(startDateTime);
                            TimeOnly end = TimeOnly.FromDateTime(endDateTime);

                            if (start == end)
                            {
                                AppLog.Error("DivergentStr", "SessionValidation", $"Warning: Session {i + 1} has same start and end time");
                            }

                            List<DayOfWeek> activeDays = new List<DayOfWeek>();
                            foreach (DayOfWeek day in Enum.GetValues(typeof(DayOfWeek)))
                            {
                                if (value.TryGetValue($"session{i + 1}On{day}", out int dayActive) && dayActive != 0)
                                {
                                    activeDays.Add(day);
                                }
                            }

                            if (activeDays.Count == 0)
                            {
                                activeDays.AddRange(new[]
                                {
                        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
                        DayOfWeek.Thursday, DayOfWeek.Friday
                    });
                                AppLog.System("DivergentStr", "SessionValidation", $"Session {i + 1}: No days selected, defaulting to weekdays");
                            }

                            _sessionDays[i] = activeDays;
                            _CustomSessions.Add(new SimpleSessionUtc($"CustomSession{i + 1}", activeDays, start, end));
                        }
                        catch (Exception ex)
                        {
                            AppLog.Error("DivergentStr", "SessionCreation", $"Error creating session {i + 1}: {ex.Message}");
                        }
                    }

                    // ===== Strategy =====
                    if (value.TryGetValue("Quantity", out double qty))
                        _quantity = Math.Max(0.0001, qty);

                    if (value.TryGetValue("Min SL In Ticks", out double minSl))
                        _minSlInTicks = Math.Max(1, minSl);

                    if (value.TryGetValue("Max SL In Ticks", out double maxSl))
                        _maxSlInTicks = Math.Max(_minSlInTicks, maxSl);

                    if (value.TryGetValue("SL Mode", out int slMode))
                        _uiSlMode = Math.Max(0, Math.Min(2, slMode));

                    if (value.TryGetValue("ATR Trailing Multiplier", out int atrTrailMult))
                        _uiAtrTrailingMult = Math.Max(1, Math.Min(10, atrTrailMult));

                    if (value.TryGetValue("TP Mode", out int tpMode))
                        _uiTpMode = Math.Max(0, Math.Min(1, tpMode));

                    if (value.TryGetValue("Fixed TP Distance (ticks)", out int fixedTp))
                        _uiFixedTpInTicks = Math.Max(1, Math.Min(1000, fixedTp));

                    if (value.TryGetValue("Min Tp In Ticks", out double minTp))
                        _minTpInTicks = Math.Max(1, minTp);

                    if (value.TryGetValue("Max Tp In Ticks", out double maxTp))
                        _maxTpInTicks = Math.Max(_minTpInTicks, Math.Max(1, maxTp));

                    if (value.TryGetValue("Max Open Positions", out int maxOpen))
                        _maxOpen = Math.Max(1, maxOpen);

                    if (value.TryGetValue("Max Session Loss USD", out double msu))
                        _maxSessionLossUsd = msu;

                    if (value.TryGetValue("Verbosity Frequency", out int vf))
                        _verbosityFrequency = Math.Max(0, vf);

                    if (value.TryGetValue("Exposure Reconcile Interval", out int eri))
                        _exposureReconcileInterval = Math.Max(0, eri);

                    if (value.TryGetValue("Slippage ATR Period", out int sap))
                        _slippageAtrPeriod = Math.Max(1, sap);

                    if (value.TryGetValue("Debug", out int debugMode))
                    {
                        _debugMode = Math.Max(0, Math.Min(1, debugMode));
                        Debug = debugMode != 0;
                    }

                    // ===== Offset Entry =====
                    if (value.TryGetValue("Use Offset Entry", out int useOffset))
                        _useOffsetEntry = Math.Max(0, Math.Min(1, useOffset));

                    if (value.TryGetValue("Use Limit Orders for Offset", out int useLimit))
                        _useOffsetLimitOrders = Math.Max(0, Math.Min(1, useLimit));

                    if (value.TryGetValue("Offset ATR Multiplier", out double offsetMult))
                        _offsetAtrMultiplier = Math.Max(0.0, Math.Min(5.0, offsetMult));

                    if (value.TryGetValue("Offset ATR Period", out int offsetPeriod))
                        _offsetAtrPeriod = Math.Max(1, offsetPeriod);

                    // ===== Lot System =====
                    if (value.TryGetValue("Use Lot System", out int useLot))
                        _useLotSystem = Math.Max(0, Math.Min(1, useLot));

                    if (value.TryGetValue("Lot Minimum", out int lotMin))
                        _lotMin = Math.Max(0, lotMin);

                    if (value.TryGetValue("Lot Step", out int lotStep))
                        _lotStep = Math.Max(0, lotStep);

                    // ===== Entry Conditions =====
                    if (value.TryGetValue("Entry: Min Conditions", out int eMin))
                        _entryMinConditions = Math.Max(0, eMin);

                    if (value.TryGetValue("Entry Use: RVOL", out int eRvol))
                        _entryUseRVOL = Math.Max(0, Math.Min(1, eRvol));

                    if (value.TryGetValue("Entry Use: VDPS", out int eVDPS))
                        _entryUseVDPS = Math.Max(0, Math.Min(1, eVDPS));

                    if (value.TryGetValue("Entry Use: VDstrong", out int eVDstrong))
                        _entryUseVDStrong = Math.Max(0, Math.Min(1, eVDstrong));

                    if (value.TryGetValue("Entry Use: HMA", out int eHma))
                        _entryUseHMA = Math.Max(0, Math.Min(1, eHma));

                    if (value.TryGetValue("Entry Use: VDtV", out int eVDtV))
                        _entryUseVDtV = Math.Max(0, Math.Min(1, eVDtV));

                    if (value.TryGetValue("Entry Use: VDP", out int eVDP))
                        _entryUseVDP = Math.Max(0, Math.Min(1, eVDP));

                    // ===== Exit Conditions =====
                    if (value.TryGetValue("Exit: Min Conditions", out int xMin))
                        _exitMinConditions = Math.Max(0, xMin);

                    if (value.TryGetValue("Exit Use: RVOL", out int xRvol))
                        _exitUseRVOL = Math.Max(0, Math.Min(1, xRvol));

                    if (value.TryGetValue("Exit Use: VDPS", out int xVDPS))
                        _exitUseVDPS = Math.Max(0, Math.Min(1, xVDPS));

                    if (value.TryGetValue("Exit Use: VDstrong", out int xVDstrong))
                        _exitUseVDStrong = Math.Max(0, Math.Min(1, xVDstrong));

                    if (value.TryGetValue("Exit Use: HMA", out int xHma))
                        _exitUseHMA = Math.Max(0, Math.Min(1, xHma));

                    if (value.TryGetValue("Exit Use: VDtV", out int xVDtV))
                        _exitUseVDtV = Math.Max(0, Math.Min(1, xVDtV));

                    if (value.TryGetValue("Exit Use: VDP", out int xVDP))
                        _exitUseVDP = Math.Max(0, Math.Min(1, xVDP));

                    // ===== ATR =====
                    if (value.TryGetValue(nameof(_uiRvolShortLen), out int rvolShort))
                        _uiRvolShortLen = Math.Max(2, Math.Min(200, rvolShort));

                    if (value.TryGetValue(nameof(_uiRvolLongLen), out int rvolLong))
                        _uiRvolLongLen = Math.Max(5, Math.Min(500, rvolLong));

                    if (value.TryGetValue(nameof(_uiAtrLenRvol), out int atrLenRvol))
                        _uiAtrLenRvol = Math.Max(2, atrLenRvol);

                    if (value.TryGetValue(nameof(_uiAtrLenHma), out int atrLenHma))
                        _uiAtrLenHma = Math.Max(2, atrLenHma);

                    if (value.TryGetValue(nameof(_uiHmaUsePrice), out int hmaUsePrice))
                        _uiHmaUsePrice = Math.Max(0, Math.Min(1, hmaUsePrice));

                    // New separate HMA lengths
                    if (value.TryGetValue(nameof(_uiHmaLenComposite), out int hmaLenComp))
                        _uiHmaLenComposite = Math.Max(2, Math.Min(200, hmaLenComp));

                    if (value.TryGetValue(nameof(_uiHmaLenPure), out int hmaLenPure))
                        _uiHmaLenPure = Math.Max(2, Math.Min(200, hmaLenPure));

                    // Backward compatibility: if old key exists, apply to both
                    if (value.TryGetValue("HMA Length", out int hmaLenOld))
                    {
                        _uiHmaLenComposite = Math.Max(2, Math.Min(200, hmaLenOld));
                        _uiHmaLenPure = Math.Max(2, Math.Min(200, hmaLenOld));
                    }

                    if (value.TryGetValue(nameof(_uiUseCompositeHmaForDirection), out int useCompositeForDir))
                        _uiUseCompositeHmaForDirection = Math.Max(0, Math.Min(1, useCompositeForDir));

                    if (value.TryGetValue(nameof(_uiAtrNormalize), out int atrNorm))
                        _uiAtrNormalize = Math.Max(0, Math.Min(1, atrNorm));

                    if (value.TryGetValue(nameof(_uiAtrSlopeThr), out double atrThr))
                        _uiAtrSlopeThr = atrThr;

                    if (value.TryGetValue(nameof(_uiUseAtrScaledHma), out int useAtrScaledHma))
                        _uiUseAtrScaledHma = Math.Max(0, Math.Min(1, useAtrScaledHma));

                    if (value.TryGetValue("ATR Slippage Multiplier", out double atrSlip))
                        _uiAtrSlippageMultiplier = Math.Max(0.0, Math.Min(2.0, atrSlip));

                    if (value.TryGetValue("Enable Heavy Metrics", out int ehm))
                        _enableHeavyMetrics = Math.Max(0, Math.Min(1, ehm));

                    // ===== Delta =====
                    if (value.TryGetValue(KEY_DELTA, out int showDelta))
                        _uiShowDelta = Math.Max(0, Math.Min(1, showDelta));

                    if (value.TryGetValue("Force Volume Ready", out int forceVolumeReady))
                        _uiForceVolumeReady = Math.Max(0, Math.Min(1, forceVolumeReady));

                    if (value.TryGetValue(nameof(_uiDeltaUseMedian), out int dMed))
                        _uiDeltaUseMedian = Math.Max(0, Math.Min(1, dMed));

                    if (value.TryGetValue(nameof(_uiUseTickBasedDelta), out int useTickDelta))
                        _uiUseTickBasedDelta = Math.Max(0, Math.Min(1, useTickDelta));

                    if (value.TryGetValue(nameof(_uiDeltaLookback), out int dLb))
                        _uiDeltaLookback = Math.Max(5, Math.Min(1000, dLb));

                    if (value.TryGetValue(nameof(_uiDeltaThresholdMult), out double dTh))
                        _uiDeltaThresholdMult = dTh;

                    if (value.TryGetValue(nameof(_uiDeltaStrengthLookback), out int dSLb))
                        _uiDeltaStrengthLookback = Math.Max(5, Math.Min(1000, dSLb));

                    if (value.TryGetValue(nameof(_uiDeltaStrengthMult), out double dSTh))
                        _uiDeltaStrengthMult = dSTh;

                    if (value.TryGetValue(nameof(_uiDeltaDivergenceMult), out double dDivTh))
                        _uiDeltaDivergenceMult = dDivTh;

                    if (value.TryGetValue(nameof(_uiDeltaVDtVMult), out double vdtvTh))
                        _uiDeltaVDtVMult = vdtvTh;

                    if (value.TryGetValue(nameof(_uiVDtVLookback), out int vdtvLb))
                        _uiVDtVLookback = Math.Max(5, Math.Min(1000, vdtvLb));

                    // ===== Snapshot =====
                    if (value.TryGetValue(KEY_SNAPSHOT, out int showSnapshots))
                        _uiShowSnapshots = Math.Max(0, Math.Min(1, showSnapshots));

                    if (value.TryGetValue(nameof(_uiSnapshotFileName), out string snapshotName))
                        _uiSnapshotFileName = SanitizeSnapshotName(snapshotName, allowEmpty: true);

                    if (value.TryGetValue(nameof(_uiSnapshotSaveRequest), out int saveRequest))
                        _uiSnapshotSaveRequest = Math.Max(0, Math.Min(1, saveRequest));

                    if (value.TryGetValue(nameof(_uiSnapshotFolderPath), out string folderPath))
                        _uiSnapshotFolderPath = SnapshotDirectoryRoot;

                    if (value.TryGetValue(nameof(_uiSnapshotLoadFileName), out string loadName))
                        _uiSnapshotLoadFileName = SanitizeSnapshotName(loadName, allowEmpty: true);

                    if (value.TryGetValue(nameof(_uiSnapshotLoadRequest), out int loadRequest))
                        _uiSnapshotLoadRequest = Math.Max(0, Math.Min(1, loadRequest));
                }
                catch (Exception ex)
                {
                    AppLog.Error("DivergentStr", "Settings", $"Error updating settings: {ex.Message}");

                    // Reset a valori sicuri
                    _CustomSessionsCount = 0;
                    _CustomSessions.Clear();
                    _sessionDays.Clear();
                }
            }

        }




        private static string EnsureSnapshotDirectory()
        {
            if (!Directory.Exists(SnapshotDirectoryRoot))
                Directory.CreateDirectory(SnapshotDirectoryRoot);
            return SnapshotDirectoryRoot;
        }

        private static string AppendSnapshotExtension(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return string.Empty;

            return name.EndsWith(SnapshotExtension, StringComparison.OrdinalIgnoreCase)
                ? name
                : name + SnapshotExtension;
        }

        private static string SanitizeSnapshotName(string raw, bool allowEmpty = false)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return allowEmpty ? string.Empty : string.Empty;

            var trimmed = raw.Trim();
            var invalid = Path.GetInvalidFileNameChars();
            var sanitizedChars = trimmed.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
            var sanitized = new string(sanitizedChars);
            return string.IsNullOrWhiteSpace(sanitized) && !allowEmpty
                ? string.Empty
                : sanitized;
        }

        private void UpdateSettingItemValue(string name, object value)
        {
            if (string.IsNullOrWhiteSpace(name))
                return;

            var item = base.Settings?.FirstOrDefault(s => string.Equals(s?.Name, name, StringComparison.Ordinal));
            if (item == null)
                return;

            switch (item)
            {
                case SettingItemBoolean boolItem when value is bool boolVal:
                    {
                        bool current = boolItem.Value is bool boolCurrent
                            ? boolCurrent
                            : (boolItem.Value == null ? false : Convert.ToBoolean(boolItem.Value, CultureInfo.InvariantCulture));
                        if (current != boolVal)
                            boolItem.Value = boolVal;
                    }
                    break;
                case SettingItemInteger intItem when value is int intVal:
                    {
                        int current = intItem.Value is int intCurrent
                            ? intCurrent
                            : Convert.ToInt32(intItem.Value ?? 0, CultureInfo.InvariantCulture);
                        if (current != intVal)
                            intItem.Value = intVal;
                    }
                    break;
                case SettingItemDouble doubleItem when value is double doubleVal:
                    {
                        double current = doubleItem.Value is double doubleCurrent
                            ? doubleCurrent
                            : Convert.ToDouble(doubleItem.Value ?? 0.0, CultureInfo.InvariantCulture);
                        if (!current.Equals(doubleVal))
                            doubleItem.Value = doubleVal;
                    }
                    break;
                case SettingItemDateTime dateItem when value is DateTime dateVal:
                    {
                        DateTime current = dateItem.Value is DateTime dateCurrent
                            ? dateCurrent
                            : Convert.ToDateTime(dateItem.Value ?? DateTime.MinValue, CultureInfo.InvariantCulture);
                        if (current != dateVal)
                            dateItem.Value = dateVal;
                    }
                    break;
                case SettingItemString stringItem when value is string strVal:
                    {
                        var current = stringItem.Value as string ?? string.Empty;
                        if (!string.Equals(current, strVal, StringComparison.Ordinal))
                            stringItem.Value = strVal;
                    }
                    break;
            }
        }

        protected override void OnSettingsUpdated()
        {
            base.OnSettingsUpdated();

            try
            {
                var sanitizedSaveName = SanitizeSnapshotName(_uiSnapshotFileName, allowEmpty: true);
                if (!string.Equals(_uiSnapshotFileName, sanitizedSaveName, StringComparison.Ordinal))
                {
                    _uiSnapshotFileName = sanitizedSaveName;
                    UpdateSettingItemValue(nameof(_uiSnapshotFileName), _uiSnapshotFileName);
                }

                var sanitizedLoadName = SanitizeSnapshotName(_uiSnapshotLoadFileName, allowEmpty: true);
                if (!string.Equals(_uiSnapshotLoadFileName, sanitizedLoadName, StringComparison.Ordinal))
                {
                    _uiSnapshotLoadFileName = sanitizedLoadName;
                    UpdateSettingItemValue(nameof(_uiSnapshotLoadFileName), _uiSnapshotLoadFileName);
                }

                if (!string.Equals(_uiSnapshotFolderPath, SnapshotDirectoryRoot, StringComparison.Ordinal))
                {
                    _uiSnapshotFolderPath = SnapshotDirectoryRoot;
                    UpdateSettingItemValue(nameof(_uiSnapshotFolderPath), _uiSnapshotFolderPath);
                }

                if (_uiSnapshotSaveRequest != 0)
                {
                    _uiSnapshotSaveRequest = 0;
                    UpdateSettingItemValue(nameof(_uiSnapshotSaveRequest), 0);
                    SaveSettingsSnapshot(_uiSnapshotFileName);
                }

                if (_uiSnapshotLoadRequest != 0)
                {
                    _uiSnapshotLoadRequest = 0;
                    UpdateSettingItemValue(nameof(_uiSnapshotLoadRequest), 0);

                    if (!string.IsNullOrWhiteSpace(_uiSnapshotLoadFileName))
                        LoadSettingsSnapshot(_uiSnapshotLoadFileName);
                    else
                        AppLog.Error("DivergentStr", "SnapshotLoad", "Snapshot file name is empty. Provide a valid name before loading.");
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("DivergentStr", "SettingsUpdated", $"Error while processing snapshot actions: {ex.Message}");
            }
        }

        private void SaveSettingsSnapshot(string requestedName)
        {
            try
            {
                string directory = EnsureSnapshotDirectory();
                string sanitizedName = SanitizeSnapshotName(requestedName);
                if (string.IsNullOrWhiteSpace(sanitizedName))
                    sanitizedName = $"snapshot_{DateTime.UtcNow:yyyyMMdd_HHmmss}";

                _uiSnapshotFileName = sanitizedName;

                string filePath = Path.Combine(directory, AppendSnapshotExtension(sanitizedName));
                var snapshot = CaptureCurrentSettingsSnapshot();

                var options = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(filePath, JsonSerializer.Serialize(snapshot, options));

                AppLog.System("DivergentStr", "SnapshotSave", $"Saved settings snapshot '{sanitizedName}' ({snapshot.Count} entries) to {filePath}");
            }
            catch (Exception ex)
            {
                AppLog.Error("DivergentStr", "SnapshotSave", $"Failed to save snapshot '{requestedName}': {ex.Message}");
            }
        }

        private void LoadSettingsSnapshot(string requestedName)
        {
            try
            {
                string directory = EnsureSnapshotDirectory();
                string sanitizedName = SanitizeSnapshotName(requestedName);

                if (string.IsNullOrWhiteSpace(sanitizedName))
                {
                    AppLog.Error("DivergentStr", "SnapshotLoad", "Snapshot file name is empty. Provide a valid name before loading.");
                    return;
                }

                _uiSnapshotLoadFileName = sanitizedName;

                string filePath = Path.Combine(directory, AppendSnapshotExtension(sanitizedName));
                if (!File.Exists(filePath))
                {
                    AppLog.Error("DivergentStr", "SnapshotLoad", $"Snapshot '{sanitizedName}' not found in {directory}");
                    return;
                }

                var json = File.ReadAllText(filePath);
                var snapshot = JsonSerializer.Deserialize<List<SnapshotEntry>>(json) ?? new List<SnapshotEntry>();

                var items = new List<SettingItem>();
                foreach (var entry in snapshot)
                {
                    if (entry == null || SnapshotTransientNames.Contains(entry.Name ?? string.Empty))
                        continue;

                    var settingItem = CreateSettingItemFromEntry(entry);
                    if (settingItem != null)
                        items.Add(settingItem);
                }

                if (items.Count == 0)
                {
                    AppLog.Error("DivergentStr", "SnapshotLoad", $"Snapshot '{sanitizedName}' does not contain valid settings.");
                    return;
                }

                this.Settings = items;
                AppLog.System("DivergentStr", "SnapshotLoad", $"Applied settings snapshot '{sanitizedName}' ({items.Count} entries).");
            }
            catch (Exception ex)
            {
                AppLog.Error("DivergentStr", "SnapshotLoad", $"Failed to load snapshot '{requestedName}': {ex.Message}");
            }
        }

        private List<SnapshotEntry> CaptureCurrentSettingsSnapshot()
        {
            var snapshot = new List<SnapshotEntry>();
            foreach (var item in this.Settings)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Name))
                    continue;

                if (SnapshotTransientNames.Contains(item.Name))
                    continue;

                switch (item)
                {
                    case SettingItemBoolean boolItem:
                        {
                            bool boolValue = boolItem.Value is bool b
                                ? b
                                : Convert.ToBoolean(boolItem.Value, CultureInfo.InvariantCulture);
                            snapshot.Add(SnapshotEntry.Create(item.Name, "Boolean", boolValue ? bool.TrueString : bool.FalseString));
                        }
                        break;
                    case SettingItemInteger intItem:
                        {
                            int intValue = intItem.Value is int i
                                ? i
                                : Convert.ToInt32(intItem.Value, CultureInfo.InvariantCulture);
                            snapshot.Add(SnapshotEntry.Create(item.Name, "Int32", intValue.ToString(CultureInfo.InvariantCulture)));
                        }
                        break;
                    case SettingItemDouble doubleItem:
                        {
                            double doubleValue = doubleItem.Value is double d
                                ? d
                                : Convert.ToDouble(doubleItem.Value, CultureInfo.InvariantCulture);
                            snapshot.Add(SnapshotEntry.Create(item.Name, "Double", doubleValue.ToString("G17", CultureInfo.InvariantCulture)));
                        }
                        break;
                    case SettingItemDateTime dateItem:
                        {
                            DateTime dtValue = dateItem.Value is DateTime dt
                                ? dt
                                : Convert.ToDateTime(dateItem.Value, CultureInfo.InvariantCulture);
                            snapshot.Add(SnapshotEntry.Create(item.Name, "DateTime", dtValue.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)));
                        }
                        break;
                    case SettingItemString stringItem:
                        snapshot.Add(SnapshotEntry.Create(item.Name, "String", stringItem.Value as string ?? string.Empty));
                        break;
                }
            }
            return snapshot;
        }

        private static SettingItem CreateSettingItemFromEntry(SnapshotEntry entry)
        {
            if (string.IsNullOrWhiteSpace(entry?.Name))
                return null;

            var type = entry.Type ?? string.Empty;
            var value = entry.Value ?? string.Empty;

            try
            {
                switch (type)
                {
                    case "Boolean":
                        if (bool.TryParse(value, out var b))
                            return new SettingItemBoolean(entry.Name, b);
                        break;
                    case "Int32":
                    case "Int64":
                        if (int.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var i))
                            return new SettingItemInteger(entry.Name, i);
                        break;
                    case "Double":
                    case "Single":
                    case "Decimal":
                        if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
                            return new SettingItemDouble(entry.Name, d);
                        break;
                    case "DateTime":
                        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
                            return new SettingItemDateTime(entry.Name, dt);
                        break;
                    case "String":
                        return new SettingItemString(entry.Name, value);
                    default:
                        // Attempt best-effort parsing
                        if (bool.TryParse(value, out var fallbackBool))
                            return new SettingItemBoolean(entry.Name, fallbackBool);
                        if (int.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var fallbackInt))
                            return new SettingItemInteger(entry.Name, fallbackInt);
                        if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var fallbackDouble))
                            return new SettingItemDouble(entry.Name, fallbackDouble);
                        return new SettingItemString(entry.Name, value);
                }
            }
            catch
            {
                // ignored - will fall through and return null
            }

            return null;
        }

        private sealed class SnapshotEntry
        {
            public string Name { get; set; }
            public string Type { get; set; }
            public string Value { get; set; }

            public static SnapshotEntry Create(string name, string type, string value) =>
                new SnapshotEntry
                {
                    Name = name,
                    Type = type,
                    Value = value
                };
        }

        protected override void OnInitializeMetrics(Meter meter)
        {
            base.OnInitializeMetrics(meter);

            meter.CreateObservableGauge("DivergentStrV0_1_account_balance_usd",
                () => _Account?.Balance ?? 0.0,
                "$", "Account Balance (USD)");

            meter.CreateObservableGauge("DivergentStrV0_1_exposure_base_usd",
                () =>
                {
                    try
                    {
                        double valueOrDefault = _conditionable?.Metrics?.ExposedAmount ?? 0;
                        if (_conditionable is RowanStrategy rs &&
                            rs.HistoryProvider?.HistoricalData != null &&
                            rs.HistoryProvider.HistoricalData.Count > 0)
                        {
                            double price = rs.HistoryProvider.HistoricalData[0, SeekOriginHistory.Begin][PriceType.Close];
                            return valueOrDefault * Math.Abs(price);
                        }
                    }
                    catch { }
                    return 0.0;
                },
                "$", "Exposure in base currency (USD)");

            meter.CreateObservableGauge("DivergentStrV0_1_net_pnl_usd",
                () => _conditionable?.Metrics?.NetProfit ?? 0,
                "$", "Gross PnL (USD)");

            meter.CreateObservableGauge("DivergentStrV0_1_trade_session_active_flag",
                () => StaticSessionManager.CurrentStatus == Status.Active ? 1 : 0,
                "flag", "Trade Session Active (1/0)");

            // Lot system related metrics
            meter.CreateObservableGauge("DivergentStrV0_1_use_lot_system_flag",
                () => _useLotSystem != 0 ? 1 : 0,
                "flag", "Use Lot System Enabled (1/0)");

            meter.CreateObservableGauge("DivergentStrV0_1_lot_min",
                () => (double)Math.Max(0, this._Symbol.MinLot),
                "lots", "Min Lot");

            meter.CreateObservableGauge("DivergentStrV0_1_lot_min",
                () => (double)Math.Max(0, this._Symbol.LotStep),
                "lots", "Step Lot");

            meter.CreateObservableGauge("DivergentStrV0_1_symbol_lot_size",
               () => {
                   try { return _Symbol?.LotSize ?? 0.0; } catch { return 0.0; }
               },
               "units", "Symbol Lot Size");

            meter.CreateObservableGauge("DivergentStrV0_1_lots_total",
                () => (double)Math.Max(0, _lotMin + _lotStep*this._Symbol.LotStep),
                "lots", "Total Lots");


            meter.CreateObservableGauge("DivergentStrV0_1_lot_selection_value",
                () => {
                    try
                    {
                        double lots = Math.Max(0.0, _lotMin + _lotStep*this._Symbol.LotStep);
                        double lotSize = _Symbol?.LotSize ?? 0.0;
                        return lots * lotSize;
                    }
                    catch { return 0.0; }
                },
                "$", "Asset Size");
        }
        #endregion
    }
}















