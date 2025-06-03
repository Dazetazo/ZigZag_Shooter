using System;

namespace Unity.Services.Analytics
{
    [Obsolete("Please create a TransactionEvent/TransactionFailedEvent and use RecordEvent(...) instead. There is a replacement TransactionItem class there.")]
    public struct Item
    {
        public string ItemName;
        public string ItemType;
        public long ItemAmount;
    }
}
