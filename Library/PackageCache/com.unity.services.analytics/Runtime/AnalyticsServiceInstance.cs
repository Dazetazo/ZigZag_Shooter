using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Services.Analytics.Data;
using Unity.Services.Analytics.Internal;
using Unity.Services.Analytics.Platform;
using UnityEngine;

namespace Unity.Services.Analytics
{
    internal interface IAnalyticsServiceSystemCalls
    {
        DateTime UtcNow { get; }
    }

    internal class AnalyticsServiceSystemCalls : IAnalyticsServiceSystemCalls
    {
        public DateTime UtcNow
        {
            get { return DateTime.UtcNow; }
        }
    }

    internal interface IUnstructuredEventRecorder
    {
        void CustomData(string eventName,
            IDictionary<string, object> eventParams,
            int? eventVersion,
            bool isStandardEvent,
            string callingMethodIdentifier);
    }

    partial class AnalyticsServiceInstance : IAnalyticsService, IUnstructuredEventRecorder
    {
        internal enum ConsentFlow
        {
            Neither,
            Old,
            New
        }

        public string PrivacyUrl => "https://unity.com/legal/game-player-and-app-user-privacy-policy";

        const string k_ForgetCallingId = "com.unity.services.analytics.Events." + nameof(OptOut);
        const string k_StartUpCallingId = "com.unity.services.analytics.Events.Startup";
        internal const string k_InvokedByUserCallingId = "com.unity.services.analytics.Events.UserInvoked";

        readonly TimeSpan k_BackgroundSessionRefreshPeriod = TimeSpan.FromMinutes(5);
        readonly TransactionCurrencyConverter converter = new TransactionCurrencyConverter();

        readonly IIdentityManager m_UserIdentity;
        readonly ISessionManager m_Session;
        readonly IDataGenerator m_DataGenerator;
        readonly ICoreStatsHelper m_CoreStatsHelper;
        readonly IConsentTracker m_ConsentTracker;
        readonly IDispatcher m_DataDispatcher;
        readonly IAnalyticsForgetter m_AnalyticsForgetter;
        readonly IAnalyticsServiceSystemCalls m_SystemCalls;
        readonly IAnalyticsContainer m_Container;

        internal IBuffer m_DataBuffer;

        public string SessionID { get { return m_Session.SessionId; } }

        int m_BufferLengthAtLastGameRunning;
        DateTime m_ApplicationPauseTime;

        bool m_IsActive;
        ConsentFlow m_ConsentFlow;

        /// <summary>
        /// This is for internal unit test usage only.
        /// In the real world, use Activate() and Deactivate().
        /// </summary>
        internal bool Active
        {
            get { return m_IsActive; }
            set { m_IsActive = value; }
        }

        /// <summary>
        /// This is for internal unit test usage only.
        /// In the real world, flow is selected by calling OptIn() or CheckForRequiredConsents().
        /// </summary>
        internal ConsentFlow SelectedConsentFlow
        {
            get { return m_ConsentFlow; }
            set { m_ConsentFlow = value; }
        }

        public string GetAnalyticsUserID()
        {
            return m_UserIdentity.UserId;
        }

        internal AnalyticsServiceInstance(IDataGenerator dataGenerator,
                                          IBuffer realBuffer,
                                          ICoreStatsHelper coreStatsHelper,
                                          IConsentTracker consentTracker,
                                          IDispatcher dispatcher,
                                          IAnalyticsForgetter forgetter,
                                          IIdentityManager userIdentity,
                                          string environment,
                                          IAnalyticsServiceSystemCalls systemCalls,
                                          IAnalyticsContainer container,
                                          ISessionManager session)
        {
            m_DataGenerator = dataGenerator;
            m_SystemCalls = systemCalls;

            m_CoreStatsHelper = coreStatsHelper;
            m_ConsentTracker = consentTracker;
            m_DataDispatcher = dispatcher;
            m_Container = container;

            m_DataBuffer = realBuffer;
            m_DataDispatcher.SetBuffer(realBuffer);

            m_IsActive = false;

            m_AnalyticsForgetter = forgetter;

            m_UserIdentity = userIdentity;
            m_Session = session;
        }

        internal void ResumeDataDeletionIfNecessary()
        {
            if (m_AnalyticsForgetter.DeletionInProgress)
            {
                DeactivateWithDataDeletionRequest();
            }
        }

        async Task InitializeUser()
        {
            try
            {
                await m_ConsentTracker.CheckGeoIP();

                if (m_ConsentTracker.IsGeoIpChecked() && (m_ConsentTracker.IsConsentDenied() || m_ConsentTracker.IsOptingOutInProgress()))
                {
                    OptOut();
                }
            }
#if UNITY_ANALYTICS_EVENT_LOGS
            catch (ConsentCheckException e)
            {
                Debug.Log("Initial GeoIP lookup fail: " + e.Message);
            }
#else
            catch (ConsentCheckException)
            {
                // Do nothing: we do not want to disturb the player because there is no action the game can take.
            }
#endif
        }

        public void StartDataCollection()
        {
            // The New flow allows "opt out and back in again", so this method can be activated
            // repeatedly within a single session. It should do nothing if the SDK is already
            // active, but otherwise (re)activate the SDK as normal.
            if (m_ConsentFlow == ConsentFlow.Neither ||
                m_ConsentFlow == ConsentFlow.New)
            {
                m_ConsentFlow = ConsentFlow.New;

                if (!m_IsActive)
                {
                    // In case you had previously requested data deletion, you must now be able to request it again.
                    m_AnalyticsForgetter.ResetDataDeletionStatus();
                    m_CoreStatsHelper.SetCoreStatsConsent(true);

                    Activate();
                }
            }
            else if (m_ConsentFlow == ConsentFlow.Old)
            {
                throw new NotSupportedException("The OptIn method cannot be used under the old consent flow.");
            }
        }

        void Activate()
        {
            if (!m_IsActive)
            {
                m_IsActive = true;
                m_Container.Enable();
                m_DataBuffer.LoadFromDisk();

                RecordStartupEvents();

                Flush();
            }
        }

        public void StopDataCollection()
        {
            if (m_ConsentFlow == ConsentFlow.New)
            {
                if (m_IsActive)
                {
                    m_DataDispatcher.Flush();
                    Deactivate();
                }
            }
            else if (m_ConsentFlow == ConsentFlow.Old)
            {
                throw new NotSupportedException("The StopDataCollection() method cannot be used under the old consent flow. Please see the migration guide for more information: https://docs.unity.com/ugs/en-us/manual/analytics/manual/sdk5-migration-guide");
            }
            else
            {
                throw new NotSupportedException("The StopDataCollection() method cannot be used before StartDataCollection() has been called.");
            }
        }

        internal void DeactivateWithDataDeletionRequest()
        {
            m_DataBuffer.ClearBuffer();
            m_DataBuffer.ClearDiskCache();
            m_Container.Enable();
            m_AnalyticsForgetter.AttemptToForget(m_UserIdentity.UserId, m_UserIdentity.InstallId, m_UserIdentity.PlayerId, BufferX.SerializeDateTime(DateTime.Now), k_ForgetCallingId, DataDeletionCompleted);

            Deactivate();
        }

        void DataDeletionCompleted()
        {
            if (!m_IsActive)
            {
                m_Container.Disable();
            }
        }

        void Deactivate()
        {
            if (m_IsActive)
            {
                m_IsActive = false;

                if ((m_ConsentFlow == ConsentFlow.New && !m_AnalyticsForgetter.DeletionInProgress) ||
                    (m_ConsentFlow == ConsentFlow.Old && !m_ConsentTracker.IsOptingOutInProgress()))
                {
                    // Only disable the container if opting out is not in progress. Otherwise, leave it
                    // running so that the heartbeat can re-attempt the deletion request upload until
                    // it succeeds.
                    m_Container.Disable();
                }
            }

            m_CoreStatsHelper.SetCoreStatsConsent(false);
        }

        bool m_StartUpEventsRecorded = false;
        void RecordStartupEvents()
        {
            if (!m_StartUpEventsRecorded)
            {
                // Only record start-up events once in a session, even if the player opts in/out/in again.
                m_StartUpEventsRecorded = true;

                // Startup Events.
                m_DataGenerator.SdkStartup(k_StartUpCallingId);
                m_DataGenerator.ClientDevice(k_StartUpCallingId);

#if UNITY_DOTSRUNTIME
                var isTiny = true;
#else
                var isTiny = false;
#endif

                m_DataGenerator.GameStarted(k_StartUpCallingId, Application.buildGUID, SystemInfo.operatingSystem, isTiny, DebugDevice.IsDebugDevice(), Locale.AnalyticsRegionLanguageCode());

                if (m_UserIdentity.IsNewPlayer)
                {
                    m_DataGenerator.NewPlayer(k_StartUpCallingId, SystemInfo.deviceModel);
                }
            }
        }

        internal void ApplicationPaused(bool paused)
        {
            if (paused)
            {
                m_ApplicationPauseTime = m_SystemCalls.UtcNow;
#if UNITY_ANALYTICS_DEVELOPMENT
                Debug.Log("Analytics SDK detected application pause at: " + m_ApplicationPauseTime.ToString());
#endif
            }
            else
            {
                DateTime now = m_SystemCalls.UtcNow;

#if UNITY_ANALYTICS_DEVELOPMENT
                Debug.Log("Analytics SDK detected application unpause at: " + now);
#endif
                if (now > m_ApplicationPauseTime + k_BackgroundSessionRefreshPeriod)
                {
                    m_Session.StartNewSession();
                }
            }
        }

        internal int AutoflushPeriodMultiplier
        {
            get { return Mathf.Clamp(1 + m_DataDispatcher.ConsecutiveFailedUploadCount, 1, 8); }
        }

        public void Flush()
        {
            if (m_IsActive)
            {
                switch (m_ConsentFlow)
                {
                    case ConsentFlow.Old:
                        if (m_ConsentTracker.IsGeoIpChecked() && m_ConsentTracker.IsConsentGiven())
                        {
                            m_DataDispatcher.Flush();
                        }
                        else
                        {
                            // Also, check if the consent was definitely checked and given at this point.
                            Debug.LogWarning("Required consent wasn't checked and given when trying to dispatch events, the events cannot be sent.");
                        }

                        if (m_ConsentTracker.IsOptingOutInProgress())
                        {
                            m_AnalyticsForgetter.AttemptToForget(m_UserIdentity.UserId, m_UserIdentity.InstallId, m_UserIdentity.PlayerId, BufferX.SerializeDateTime(DateTime.Now), k_ForgetCallingId, OldForgetMeEventUploaded);
                        }
                        break;
                    case ConsentFlow.New:
                        // No need for conditional guard, m_IsActive is only true if we are clear to flush.
                        m_DataDispatcher.Flush();
                        break;
                }
            }
            else if (m_AnalyticsForgetter.DeletionInProgress)
            {
                DeactivateWithDataDeletionRequest();
            }
        }

        public void RequestDataDeletion()
        {
            DeactivateWithDataDeletionRequest();
        }

        [Obsolete("This mechanism is no longer supported and will be removed in a future version. Use the new Core IAnalyticsStandardEventComponent API instead.")]
        public void RecordInternalEvent(Internal.Event eventToRecord)
        {
            if (m_IsActive)
            {
                m_DataBuffer.PushEvent(eventToRecord);
            }
        }

        internal void ApplicationQuit()
        {
            if (m_IsActive)
            {
                m_DataGenerator.GameEnded("com.unity.services.analytics.Events.Shutdown", DataGenerator.SessionEndState.QUIT);

                // Flush to disk before attempting final upload, in case we do not have enough time during teardown
                // to make the request and/or determine its success (e.g. if we shut down offline)
                m_DataBuffer.FlushToDisk();

                Flush();
            }
        }

        internal void RecordGameRunningIfNecessary()
        {
            if (m_IsActive)
            {
                if (m_DataBuffer.Length == 0 || m_DataBuffer.Length == m_BufferLengthAtLastGameRunning)
                {
                    m_DataGenerator.GameRunning("com.unity.services.analytics.AnalyticsServiceInstance.RecordGameRunningIfNecessary");
                    m_BufferLengthAtLastGameRunning = m_DataBuffer.Length;
                }
                else
                {
                    m_BufferLengthAtLastGameRunning = m_DataBuffer.Length;
                }
            }
        }

        [Obsolete]
        public void AcquisitionSource(AcquisitionSourceParameters acquisitionSourceParameters)
        {
            if (m_IsActive)
            {
                m_DataGenerator.AcquisitionSource("com.unity.services.analytics.events.acquisitionSource", acquisitionSourceParameters);
            }
#if UNITY_ANALYTICS_EVENT_LOGS
            else
            {
                Debug.Log("Did not record acquisitionSource event because player has not opted in.");
            }
#endif
        }

        [Obsolete]
        public void AdImpression(AdImpressionParameters parameters)
        {
            if (m_IsActive)
            {
                m_DataGenerator.AdImpression("com.unity.services.analytics.events.adimpression", parameters);
            }
#if UNITY_ANALYTICS_EVENT_LOGS
            else
            {
                Debug.Log("Did not record adImpression event because player has not opted in.");
            }
#endif
        }

        [Obsolete]
        public void Transaction(TransactionParameters parameters)
        {
            if (m_IsActive)
            {
                m_DataGenerator.Transaction("com.unity.services.analytics.events.transaction", parameters);
            }
#if UNITY_ANALYTICS_EVENT_LOGS
            else
            {
                Debug.Log("Did not record transaction event because player has not opted in.");
            }
#endif
        }

        [Obsolete]
        public void TransactionFailed(TransactionFailedParameters parameters)
        {
            if (m_IsActive)
            {
                m_DataGenerator.TransactionFailed("com.unity.services.analytics.events.TransactionFailed", parameters);
            }
#if UNITY_ANALYTICS_EVENT_LOGS
            else
            {
                Debug.Log("Did not record transactionFailed event because player has not opted in.");
            }
#endif
        }

        public long ConvertCurrencyToMinorUnits(string currencyCode, double value)
        {
            return converter.Convert(currencyCode, value);
        }

        public void CustomData(string eventName)
        {
            CustomData(eventName, null);
        }

        public void CustomData(string eventName, IDictionary<string, object> eventParams)
        {
            CustomData(eventName, eventParams, null, false, "AnalyticsServiceInstance.CustomData");
        }

        public void CustomData(string eventName,
            IDictionary<string, object> eventParams,
            int? eventVersion,
            bool isStandardEvent,
            string callingMethodIdentifier)
        {
            if (m_IsActive)
            {
                if (isStandardEvent)
                {
                    m_DataBuffer.PushStandardEventStart(eventName, eventVersion.Value);
                    m_DataGenerator.PushCommonParams(callingMethodIdentifier);
                }
                else
                {
                    m_DataBuffer.PushCustomEventStart(eventName);
                }

                if (eventParams != null)
                {
                    foreach (KeyValuePair<string, object> paramPair in eventParams)
                    {
                        m_DataBuffer.PushObject(paramPair.Key, paramPair.Value);
                    }
                }

                m_DataBuffer.PushEndEvent();
            }
#if UNITY_ANALYTICS_EVENT_LOGS
            else
            {
                Debug.Log("Did not record custom event " + eventName + " because player has not opted in.");
            }
#endif
        }

        public void RecordEvent(string name)
        {
            if (m_IsActive)
            {
                m_DataGenerator.PushEmptyEvent(name);
            }
#if UNITY_ANALYTICS_EVENT_LOGS
            else
            {
                Debug.Log("Did not record event " + name + " because player has not opted in.");
            }
#endif
        }

        public void RecordEvent(Event e)
        {
            RecordEvent(e, k_InvokedByUserCallingId);
        }

        internal void RecordEvent(Event e, string callingMethodIdentifier)
        {
            if (m_IsActive)
            {
                m_DataGenerator.PushEvent(callingMethodIdentifier, e);
            }
#if UNITY_ANALYTICS_EVENT_LOGS
            else
            {
                Debug.Log("Did not record custom event " + e.Name + " because player has not opted in.");
            }
#endif
        }

        public async Task SetAnalyticsEnabled(bool enabled)
        {
            if (enabled && !m_IsActive)
            {
                Activate();
            }
            else if (!enabled && m_IsActive)
            {
                Deactivate();
            }

            // For backwards compatibility.
            await Task.CompletedTask;
        }
    }
}
