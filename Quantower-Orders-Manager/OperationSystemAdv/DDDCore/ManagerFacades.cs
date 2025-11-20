using System;
using System.Collections.Generic;
using System.Linq;
using TradingPlatform.BusinessLayer;

namespace DivergentStrV0_1.OperationSystemAdv.DDDCore
{
    public enum ManagerType
    {
        PositionBased
    }

    public interface IManagerFacade : IDisposable
    {
        IReadOnlyList<ITpSlItems> Items { get; }
        IReadOnlyList<ITpSlItems> ClosedItems { get; }
        double ExposedAmmount { get; }
        int TradeCount { get; }

        void PlaceEntryOrder(PlaceOrderRequestParameters req, string comment,
            List<PlaceOrderRequestParameters> sl, List<PlaceOrderRequestParameters> tp, object sender = null);

        void UpdateSl(ITpSlItems item, Func<double, double> updateFunction);
        void UpdateTp(ITpSlItems item, Func<double, double> updateFunction);
        void PlanBracket(string comment, double? stopPrice, double? takePrice);
    }

    internal sealed class PositionManagerFacade : IManagerFacade
    {
        private readonly TpSlPositionManager _inner;
        public PositionManagerFacade(TpSlPositionManager inner)
        {
            _inner = inner;
        }

        public double ExposedAmmount => _inner.ExposedAmmount;
        public IReadOnlyList<ITpSlItems> Items => _inner.Items.Cast<ITpSlItems>().ToList();
        public IReadOnlyList<ITpSlItems> ClosedItems => _inner.ClosedItems.Cast<ITpSlItems>().ToList();
        public int TradeCount => _inner.TradeCount;

        public void PlaceEntryOrder(PlaceOrderRequestParameters req, string comment, List<PlaceOrderRequestParameters> sl, List<PlaceOrderRequestParameters> tp, object sender = null)
            => _inner.PlaceEntryOrder(req, comment, sl, tp, sender);

        public void UpdateSl(ITpSlItems item, Func<double, double> updateFunction)
            => _inner.UpdateSl((TpSlItemPosition)item, updateFunction);

        public void UpdateTp(ITpSlItems item, Func<double, double> updateFunction)
            => _inner.UpdateTp((TpSlItemPosition)item, updateFunction);

        public void PlanBracket(string comment, double? stopPrice, double? takePrice)
            => _inner.PlanBracket(comment, stopPrice, takePrice);

        public void Dispose() => _inner.Dispose();
    }

    public static class ManagerFacadeFactory
    {
        public static IManagerFacade Create(ManagerType type)
        {
            if (type != ManagerType.PositionBased)
                global::DivergentStrV0_1.Utils.AppLog.System("Managers", "Manager Creation", $"Requested {type}, using PositionBased manager.");

            return new PositionManagerFacade(new TpSlPositionManager());
        }
    }
}
