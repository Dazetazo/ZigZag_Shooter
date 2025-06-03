using System;
using System.Globalization;

namespace Unity.Services.Analytics.Internal.Platform
{
    [Obsolete("This should not be public. Do not use it.")]
    public static class UserCountry
    {
        [Obsolete("This should not be public. Do not use it.")]
        public static string Name()
        {
            // User country cannot be reliably deduced from any setting we have available here
            // without using location services, so we return ZZ so the Analytics service will use
            // GeoIP.
            return "";
        }
    }
}
