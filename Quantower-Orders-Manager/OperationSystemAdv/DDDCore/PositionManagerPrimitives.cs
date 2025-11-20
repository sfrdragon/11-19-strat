namespace DivergentStrV0_1.OperationSystemAdv.DDDCore
{
    public enum PositionManagerStatus
    {
        Created,
        Placed,
        ExitOrderPlaced,
        PartialyFilled,
        Filled,
        PartialyClosed,
        Closed,
        Aborted
    }

    public enum OrderTypeSubcomment
    {
        Entry,
        StopLoss,
        TakeProfit
    }
}
