using System;

namespace Unity.Services.Analytics
{
    [Obsolete("Please create a TransactionEvent/TransactionFailedEvent and use RecordEvent(...) instead. There is a replacement TransactionVirtualCurrency class there.")]
    public struct VirtualCurrency
    {
        public string VirtualCurrencyName;
        public VirtualCurrencyType VirtualCurrencyType;
        public long VirtualCurrencyAmount;
    }
}
