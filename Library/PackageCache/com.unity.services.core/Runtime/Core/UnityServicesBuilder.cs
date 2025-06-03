namespace Unity.Services.Core
{
    static class UnityServicesBuilder
    {
        internal delegate IUnityServices CreationDelegate();
        internal static CreationDelegate InstanceCreationDelegate { get; set; }

        public static IUnityServices Create()
        {
            if (InstanceCreationDelegate == null)
            {
                throw new ServicesCreationException($"Error creating services instance. The creation delegate has not been initialized.");
            }

            return InstanceCreationDelegate();
        }
    }
}
