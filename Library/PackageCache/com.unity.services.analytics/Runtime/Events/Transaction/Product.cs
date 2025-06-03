using System;
using System.Collections.Generic;

namespace Unity.Services.Analytics
{
    [Obsolete("Please create a TransactionEvent/TransactionFailedEvent and use RecordEvent(...) instead. The Product container used by ProductsSpent and ProductsReceived has been replaced by SpentItems/SpentRealCurrency/SpentVirtualCurrencies/etc fields.")]
    public struct Product
    {
        /// <summary>
        /// (Optional) The real currency spent or received as part of this product
        /// </summary>
        public RealCurrency? RealCurrency;

        /// <summary>
        /// (Required) The virtual currencies spent or received as part of this product (can be an empty list if none)
        /// </summary>
        public List<VirtualCurrency> VirtualCurrencies;

        /// <summary>
        /// (Required) The items spent or received as part of this product (can be an empty list if none)
        /// </summary>
        public List<Item> Items;
    }
}
