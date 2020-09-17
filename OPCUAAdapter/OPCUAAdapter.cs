using GSF;
using GSF.Diagnostics;
using GSF.TimeSeries;
using GSF.TimeSeries.Adapters;
using GSF.TimeSeries.Statistics;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OPCUAAdapter
{
    [Description("OPC UA: Get data from OPC UA Server")]
    public class OPCUAAdapter : InputAdapterBase
    {
        private ConfiguredEndpoint m_defaultEndpoint;
        private ApplicationConfiguration m_configuration;
        private Session m_session;
        private const int ReconnectPeriod = 10;
        private SessionReconnectHandler reconnectHandler;
        private Subscription m_subscription;
        private Dictionary<string, IMeasurement> items;
        private long m_measurementsReceived;
        static bool autoAccept;

        public override bool SupportsTemporalProcessing => false;
        /// <summary>
        /// Gets total read or derived measurements over the Modbus poller instance lifetime.
        /// </summary>
        public long MeasurementsReceived => m_measurementsReceived;
        protected override bool UseAsyncConnect => false;

        public override string GetShortStatus(int maxLength)
        {
            return ("Total sent measurements " + ProcessedMeasurements.ToString("N0")).CenterText(maxLength);
        }

        protected override void AttemptConnection()
        {
            string[] cs = ConnectionString.Split(';');
            OnStatusMessage(MessageLevel.Info, $"Connecting to OPC {cs[0]}");
            var selectedEndpoint = CoreClientUtils.SelectEndpoint(cs[0], false, 15000);
            var endpointConfiguration = EndpointConfiguration.Create(m_configuration);
            m_defaultEndpoint = new ConfiguredEndpoint(null, selectedEndpoint, endpointConfiguration);
            m_session = Session.Create(m_configuration, m_defaultEndpoint, false, "OpenHistorian Data Collector", 60000, new UserIdentity(new AnonymousIdentityToken()), null).GetAwaiter().GetResult();

            // register keep alive handler
            m_session.KeepAlive += Client_KeepAlive;
            Subscribe();
            OnStatusMessage(MessageLevel.Info, $"OPC Connected to {cs[0]}");
        }

        protected override void AttemptDisconnection()
        {
            if (m_session.Connected) m_session.Close();
        }

        public override void Initialize()
        {
            ProcessingInterval = 0;

            OnStatusMessage(MessageLevel.Info, $"Initialising OPC for device {Name}");
            base.Initialize();

            // If more data is added to the connection string the it will have to be parsed here (Look at ModbusPoller.cs for example line 456: https://github.com/GridProtectionAlliance/gsf/blob/master/Source/Libraries/Adapters/ModbusAdapters/ModbusPoller.cs)

            // Statistics
            StatisticsEngine.Register(this, "OPCUA", "OPC");

            OutputMeasurements = ParseOutputMeasurements(DataSource, false, $"FILTER ActiveMeasurements WHERE Device = '{Name}'");
            DataTable measurements = DataSource.Tables["ActiveMeasurements"];
            items = OutputMeasurements.ToDictionary(measurement =>
            {
                DataRow[] records = measurements.Select($"ID = '{measurement.Key}'");
                var reference = records[0]["SignalReference"].ToNonNullString();
                OnStatusMessage(MessageLevel.Info, $"Linking OPC item {reference} with tag {measurement.TagName}");
                return reference;
            });


            // OPC Init
            ApplicationInstance application = new ApplicationInstance
            {
                ApplicationName = "OPC UA Client",
                ApplicationType = Opc.Ua.ApplicationType.Client,
                ConfigSectionName = "Opc.Ua.Client"
            };
            ApplicationConfiguration config = application.LoadApplicationConfiguration(false).GetAwaiter().GetResult();
            bool haveAppCertificate = application.CheckApplicationInstanceCertificate(false, 0).GetAwaiter().GetResult();
            if (!haveAppCertificate)
            {
                throw new Exception("Application instance certificate invalid!");
            }

            if (haveAppCertificate)
            {
                config.ApplicationUri = Utils.GetApplicationUriFromCertificate(config.SecurityConfiguration.ApplicationCertificate.Certificate);
                if (config.SecurityConfiguration.AutoAcceptUntrustedCertificates)
                {
                    autoAccept = true;
                }
                config.CertificateValidator.CertificateValidation += new CertificateValidationEventHandler(CertificateValidator_CertificateValidation);
            }
            else
            {
                OnStatusMessage(MessageLevel.Info, $"Missing application certificate, using unsecure connection.");
            }

            m_configuration = config;
            OnStatusMessage(MessageLevel.Info, $"OPC Initialised ({ConnectionString})");
        }
        private void CertificateValidator_CertificateValidation(CertificateValidator validator, CertificateValidationEventArgs e)
        {
            if (e.Error.StatusCode == StatusCodes.BadCertificateUntrusted)
            {
                e.Accept = autoAccept;
                if (autoAccept)
                {
                    OnStatusMessage(MessageLevel.Info, $"Accepted Certificate: {e.Certificate.Subject}");
                }
                else
                {
                    OnStatusMessage(MessageLevel.Info, $"Rejected Certificate: {e.Certificate.Subject}");
                }
            }
        }

        private void Client_KeepAlive(Session sender, KeepAliveEventArgs e)
        {
            if (e.Status != null && ServiceResult.IsNotGood(e.Status))
            {
                //log.InfoFormat("{0} {1}/{2}", e.Status, sender.OutstandingRequestCount, sender.DefunctRequestCount);

                if (reconnectHandler == null)
                {
                    reconnectHandler = new SessionReconnectHandler();
                    reconnectHandler.BeginReconnect(sender, ReconnectPeriod * 1000, Client_ReconnectComplete);
                }
            }
        }
        private void Client_ReconnectComplete(object sender, EventArgs e)
        {
            // ignore callbacks from discarded objects.
            if (!Object.ReferenceEquals(sender, reconnectHandler))
            {
                return;
            }

            m_session = reconnectHandler.Session;
            reconnectHandler.Dispose();
            reconnectHandler = null;
        }
        private void Subscribe()
        {
            ReferenceDescriptionCollection references;
            Byte[] continuationPoint;

            //references = session.FetchReferences(ObjectIds.ObjectsFolder);
            m_session.Browse(null, null, ObjectIds.ObjectsFolder, 0u, BrowseDirection.Forward, ReferenceTypeIds.HierarchicalReferences, true, (uint)NodeClass.Variable | (uint)NodeClass.Object | (uint)NodeClass.Method, out continuationPoint, out references);

            m_subscription = new Subscription(m_session.DefaultSubscription) { PublishingInterval = 500 };

            Browse(references);
            m_session.AddSubscription(m_subscription);
            m_subscription.Create();
        }

        private void Browse(ReferenceDescriptionCollection references)
        {
            ReferenceDescriptionCollection r;
            Byte[] continuationPoint;
            foreach (var node in references.Where(o => o.DisplayName.Text != "Server"))
            {
                if (node.NodeClass == NodeClass.Variable)
                {
                    if (items.Any(o => o.Key == node.NodeId.ToString().ToUpper()))
                    {
                        var monitoredItem = new MonitoredItem(m_subscription.DefaultItem) { DisplayName = node.DisplayName.Text, StartNodeId = (NodeId)node.NodeId };
                        monitoredItem.Notification += OnValueChange;
                        m_subscription.AddItem(monitoredItem);
                        OnStatusMessage(MessageLevel.Info, $"OPC item {node.NodeId} linked.");
                    }
                }
                else if (node.NodeClass == NodeClass.Object)
                {
                    m_session.Browse(null, null, (NodeId)node.NodeId, 0u, BrowseDirection.Forward, ReferenceTypeIds.HierarchicalReferences, true, (uint)NodeClass.Variable | (uint)NodeClass.Object | (uint)NodeClass.Method, out continuationPoint, out r);
                    Browse(r);
                }
            }
        }

        private void OnValueChange(MonitoredItem opcItem, MonitoredItemNotificationEventArgs e)
        {
            List<IMeasurement> measurements = new List<IMeasurement>();
            foreach (var value in opcItem.DequeueValues())
            {
                var key = opcItem.StartNodeId.ToString().ToUpper();
                if (items.ContainsKey(key))
                {
                    var item = items[key];
                    if (item != null)
                    {
                        var measurement = Measurement.Clone(item, Convert.ToDouble(value.Value), value.SourceTimestamp.ToUniversalTime().Ticks);
                        measurement.StateFlags = MeasurementStateFlags.Normal;
                        measurements.Add(measurement);
                    }
                }
                else OnStatusMessage(MessageLevel.Warning, $"OPC key {key} not found.");
            }
            OnNewMeasurements(measurements);
            m_measurementsReceived += measurements.Count;
            //OnStatusMessage(MessageLevel.Info, $"Updating OPC values.");
        }
        private class OpcItem
        {
            public string Reference { get; set; }
            public IMeasurement Measurement { get; set; }

        }
    }
}
