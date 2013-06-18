using log4net;
using Nini.Config;
using System;
using System.Collections.Generic;
using OpenMetaverse.StructuredData;
using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Framework;

namespace DSG.RegionSync
{
    class RemotePassiveQuarkSubscription
    {
        private SyncQuark m_quark;
        public  SyncQuark Quark { get { return m_quark; } }
        // All sync connectors that are active owners of this quark. Need to subscribe to all of them.
        private HashSet<SyncConnector> m_syncConnectors;
        public HashSet<SyncConnector> SyncConnector { 
            get { return m_syncConnectors; }
        }

        public RemotePassiveQuarkSubscription(SyncQuark quark, HashSet<SyncConnector> syncConnectors)
        {
            m_quark = quark;
            m_syncConnectors = syncConnectors;
        }

        public bool AddSyncConnector(SyncConnector connector)
        {
            m_syncConnectors.Add(connector);
            return true;
        }

        public bool RemoveSyncConnector(SyncConnector connector)
        {
            if (m_syncConnectors.Contains(connector))
            {
                m_syncConnectors.Remove(connector);
                return true;
            }
            else
                return false;
        }
    }
    /// <summary>
    /// QuarkPublisher
    /// Description: Stores all SyncConnectors subscribed actively and passively to a quark. Only quarks that belong to this sync process.
        /// </summary>
    class QuarkPublisher
    {
        private SyncQuark m_quark;
        private string m_quarkName;

        private HashSet<SyncConnector> m_passiveQuarkSubscribers = new HashSet<SyncConnector>();
        public HashSet<SyncConnector> PassiveSubscribers { get { return m_passiveQuarkSubscribers; } }
        
        private HashSet<SyncConnector> m_activeQuarkSubscribers = new HashSet<SyncConnector>();
        public HashSet<SyncConnector> ActiveSubscribers { get { return m_activeQuarkSubscribers;} }

        public QuarkPublisher(SyncQuark quark)
        {
            m_quarkName = quark.QuarkName;
            m_quark = quark;
        }

        /// <summary>
        /// Adds a new connector to PassiveSubscribers
        /// </summary>
        /// <param name="connector"></param>
        public void AddPassiveSubscriber(SyncConnector connector) {
                m_passiveQuarkSubscribers.Add(connector);
        }

        /// <summary>
        /// Adds a new connector to ActiveSubscribers
        /// </summary>
        /// <param name="connector"></param>
        public void AddActiveSubscriber(SyncConnector connector)
        {
                m_activeQuarkSubscribers.Add(connector);
        }
        
        
        /// <summary>
        /// Iterates over every quark subscription and deletes the connector from it. 
        /// TODO: This seems slow, any way to make it better?
        /// </summary>
        /// <param name="connector"></param>
        public void RemoveSubscriber(SyncConnector connector)
        {
            if (m_activeQuarkSubscribers.Contains(connector)) {
                m_activeQuarkSubscribers.Remove(connector);
            }
            if (m_passiveQuarkSubscribers.Contains(connector)) {
                m_passiveQuarkSubscribers.Remove(connector);
            }
        }

        /// <summary>
        /// Returns all subscribers with quarkName (both active and passive)
        /// </summary>
        /// <returns>Union of the active and passive syncconnectors subscribed to this quark</returns>
        public HashSet<SyncConnector> GetAllQuarkSubscribers()
        {
            HashSet<SyncConnector> subscribers = new HashSet<SyncConnector>(m_activeQuarkSubscribers);
            subscribers.UnionWith(m_passiveQuarkSubscribers);
            return subscribers;
        }

    }
    class QuarkManager
    {
        private static string LogHeader = "[QUARKMANAGER]";
        private Boolean m_detailUpdateDebugLog = false;
        private string m_zeroUUID = UUID.Zero.ToString();

        private int m_quarkSizeX;
        private int m_quarkSizeY;
        HashSet<string> m_syncQuarksPassive = new HashSet<string>();
        HashSet<string> m_syncQuarksActive = new HashSet<string>();
        private SyncInfoManager m_syncInfoManager;

        public static ILog m_log;

        // This actor's active and passive quarks, as determined from config file (coded format). Keeping stored for sending to others.
        private string m_stringEncodedActiveQuarks = String.Empty;
        private string m_stringEncodedPassiveQuarks = String.Empty;
        public string ActiveQuarkSubscription
        {
            get { return m_stringEncodedActiveQuarks; }
            private set { m_stringEncodedActiveQuarks = value; }
        }

        public string PassiveQuarkSubscription
        {
            get { return m_stringEncodedPassiveQuarks; }
            private set { m_stringEncodedPassiveQuarks = value; }
        }

        // Transformed strings into SyncQuark. 
        private Dictionary<string, SyncQuark> m_activeQuarkSet = new Dictionary<string, SyncQuark>();
        private Dictionary<string, SyncQuark> m_passiveQuarkSet = new Dictionary<string, SyncQuark>();

        private Dictionary<string, QuarkPublisher> m_quarkSubscriptions = new Dictionary<string,QuarkPublisher>();
        private Dictionary<string, RemotePassiveQuarkSubscription> m_remoteQuarkPassiveSubscriptions = new Dictionary<string,RemotePassiveQuarkSubscription>();

        RegionSyncModule m_regionSyncModule;

        public HashSet<string> ActiveQuarkStringSet
        {
            get { return m_syncQuarksActive; }
        }

        public HashSet<string> PassiveQuarkStringSet
        {
            get { return m_syncQuarksPassive; }
        }

        #region QuarkRegistration
        
        /// <summary>
        /// Constructor for QuarkManager. 
        /// Description: Quark Manager interprets the active and passive quark settings in config file and register them with 
        /// the Grid service. The grid service replies with a list of sync addresses to connect to (RegionSyncListenerInfo).
        /// The set of of all sync processes should be a super-quark-set of the this quark set, unless this is the root node.
        /// QuarkManager connects to all of the addresses and exchange quark information. 
        /// 
        /// </summary>
        /// <param name="syncModule"></param>
        public QuarkManager(RegionSyncModule syncModule)
        {
            Initialize(syncModule);
        }

        private void Initialize(RegionSyncModule syncModule)
        {
            //initialize some static variables
            IConfig config = syncModule.SysConfig;
            m_regionSyncModule = syncModule;
            m_syncInfoManager = m_regionSyncModule.SyncInfoManager;
            //string regPolicy = config.GetString("QuarkRegistrationPolicy", "AllQuarks");
            
            // Size of quarks
            m_quarkSizeX = config.GetInt("SyncQuarkSizeX", 256);
            m_quarkSizeY = config.GetInt("SyncQuarkSizeY", 256);

            // Set SyncQuark objects size variabe
            SyncQuark.SizeX = m_quarkSizeX;
            SyncQuark.SizeY = m_quarkSizeY;

            // Read string from config file
            ActiveQuarkSubscription = config.GetString("SyncActiveQuarks", String.Empty);
            ActiveQuarkSubscription = ActiveQuarkSubscription.Trim();
            PassiveQuarkSubscription = config.GetString("SyncPassiveQuarks", String.Empty);
            PassiveQuarkSubscription = PassiveQuarkSubscription.Trim();

            // Parse into hashsets
            //First, decode subscription for active quarks
            HashSet<string> activeQuarks = DecodeSyncQuarks(ActiveQuarkSubscription);
            
            //Then decode subscription for passive quarks
            HashSet<string> passiveQuarks = DecodeSyncQuarks(PassiveQuarkSubscription);

            foreach (string quarkLoc in activeQuarks)
            {
                SyncQuark quark = new SyncQuark(quarkLoc);
                if (quark.ValidQuark)
                {
                    m_log.DebugFormat("Add record for active quark {0}", quarkLoc);
                    m_activeQuarkSet.Add(quarkLoc, quark);
                }
            }

            foreach (string quarkLoc in passiveQuarks)
            {
                SyncQuark quark = new SyncQuark(quarkLoc);
                if (quark.ValidQuark)
                {
                    m_log.DebugFormat("Add record for passive quark {0}", quarkLoc);
                    m_passiveQuarkSet.Add(quarkLoc, quark);
                }
            }

            // Register my active quarks with grid service.
            // TODO: Send the coded version, instead of reading one by one and making multiple HTTP requests.
            RegisterSyncQuarksWithGridService();

            // TODO: There should be a Register Passive and Active quarks.
            // Grid Service returns the union of quarks that form a superset of my quark subscription. 
            // "The union of the returned quark sets of these sync process should be a superset as the querying process’s quark set."

            // SOMEHOW (??) I have a list of RegionSyncListenerInfo of my "superset" quarks.
            
            #region THROWMEAWAY
            // STUB!
            List<RegionSyncListenerInfo> superSetQuarks = new List<RegionSyncListenerInfo>();
            RegionSyncListenerInfo test_parent = new RegionSyncListenerInfo("127.0.0.1",15000);
            superSetQuarks.Add(test_parent);
            #endregion

            if (!m_regionSyncModule.IsSyncRelay)
            {
                foreach (RegionSyncListenerInfo rsli in superSetQuarks)
                {
                    SyncConnector syncConnector = m_regionSyncModule.StartNewSyncConnector(rsli);
                    if (syncConnector == null)
                        m_log.ErrorFormat("Failed to connecto to parent sync {0} (provided to QuarkManager by GridService)", rsli.ToString());
                    else
                        m_log.WarnFormat("Success creating SyncConnecting to {0}",rsli.ToString());
                    if (!m_regionSyncModule.IsSyncingWithOtherSyncNodes())
                    {
                        m_log.Error("Failed to start at least one sync connector. Not syncing.");
                        return;
                    }
                }
                m_log.WarnFormat("Finished loading quarks: SyncActiveQuarks:{0} and SyncPassiveQuarks:{1}", ActiveQuarkSubscription, PassiveQuarkSubscription);
            }
            else
            {
                m_log.Warn("QuarkManager: This is the hub, so no quark registration required.");
            }
        }

        private HashSet<string> DecodeSyncQuarks(string quarksInput)
        {
            if (quarksInput.Equals(String.Empty))
                return new HashSet<string>();

            //each input string should be in the format of "xl[-xr] or x, yl[-yr] or y/.../...", 
            //where "xl[-xr],yl[-yr]" specifies a range of quarks (a quark block, where 
            //"xl,yl" is the x,y indices for the lower left corner quark, and "xr,yr" is 
            //the x,y indices for the upper right corner quark.
            //x and y indices of a quark is calculated by floor(x/quark_size), and 
            //floor(y/quark_size), where x,y is one position that is within the quark.
            string interQuarkDelimStr = "/";
            char[] interQuarkDelimeter = interQuarkDelimStr.ToCharArray();
            string[] quarkSet = quarksInput.Split(interQuarkDelimeter);

            string intraQuarkDelimStr = ",";
            char[] intraQuarkDelimeter = intraQuarkDelimStr.ToCharArray();
            string xyDelimStr = "-";
            char[] xyDelimeter = xyDelimStr.ToCharArray();
            HashSet<string> quarksOutput = new HashSet<string>();

            foreach (string quarkString in quarkSet)
            {
                string[] quarkXY = quarkString.Split(intraQuarkDelimeter);
                if (quarkXY.Length < 2)
                {
                    m_log.WarnFormat("DecodeSyncQuarks: Invalid quark configuration: {0}", quarkString);
                    continue;
                }
                string qX = quarkXY[0];
                string qY = quarkXY[1];

                //Are X,Y specified as "xl[-xr],yl[-yr]", "x,y", "xl[-xr],y", or "x,yl[-yr]"?
                string[] xRange = qX.Split(xyDelimeter);
                int xLow = 0, xHigh = -1;
                if (xRange.Length == 2)
                {
                    int.TryParse(xRange[0], out xLow);
                    int.TryParse(xRange[1], out xHigh);
                }
                else if (xRange.Length == 1)
                {
                    int.TryParse(xRange[0], out xLow);
                    xHigh = xLow;
                }
                else
                {
                    m_log.WarnFormat("DecodeSyncQuarks: Invalid quark configuration: {0}", quarkString);
                }

                string[] yRange = qY.Split(xyDelimeter);
                int yLow = 0, yHigh = -1;
                if (yRange.Length == 2)
                {
                    int.TryParse(yRange[0], out yLow);
                    int.TryParse(yRange[1], out yHigh);
                }
                else if (yRange.Length == 1)
                {
                    int.TryParse(yRange[0], out yLow);
                    yHigh = yLow;
                }
                else
                {
                    m_log.WarnFormat("DecodeSyncQuarks: Invalid quark configuration: {0}", quarkString);
                }

                for (int x = xLow; x <= xHigh; x++)
                {
                    for (int y = yLow; y <= yHigh; y++)
                    {
                        string quarkName = String.Format("{0},{1}", x, y);

                        quarksOutput.Add(quarkName);
                    }
                }
            }

            return quarksOutput;
        }

        /// <summary>
        /// Register with the grid service of the quarks that this sync node operates on.
        /// A sync node that is a non-leaf node in the topology must call this function.
        /// </summary>
        /// <returns></returns>
        public bool RegisterSyncQuarksWithGridService()
        {
            //TODO: register all quarks with one registration requests, instead of 
            //one request for each quark

            bool allRegisteredSuccessfully = true;

            //Only active quarks should be registered with Grid Service
            foreach (KeyValuePair<string, SyncQuark> quark in m_activeQuarkSet)
            {
                if (!m_regionSyncModule.Scene.GridService.RegisterQuark(m_regionSyncModule.ActorID, quark.Value.QuarkLocX, quark.Value.QuarkLocX))
                {
                    allRegisteredSuccessfully = false;
                    m_log.WarnFormat("[SYNC MANAGER]: Failure registering quark {0}", quark.Value.QuarkName);
                }

            }
            return allRegisteredSuccessfully;
        }

        #endregion // QuarkRegistration

        #region InterQuarkCommunication

        /// <summary>
        /// Called in the initialization of a sync process, connecting to a parent process,
        /// and is exchaning quark set information. The child process should send in both
        /// its active and passve quarks, since all these quarks need to receive sync updates
        /// from parents. The parent process, on the other hand, should only reply back the 
        /// active quarks -- updates applied to passive quarks are retained locally and not 
        /// for further propagating
        /// </summary>
        /// <returns></returns>
        public OSDMap CreateQuarkSubscriptionMessageArgs()
        {
            OSDMap ret = new OSDMap();
            ret = EncodeQuarkSubscriptionsForParent();
            return ret;
        }

        /// <summary>
        /// Encode quark subscriptions.
        /// </summary>
        /// <param name="ack">Indicate if the encoded message would be sent
        /// back to a child process as an ACK. </param>
        /// <returns></returns>
        public OSDMap EncodeQuarkSubscriptionsForParent()
        {
            //Subscription sent to a parent node -- efficiently, it should be the
            //set of quarks that this node wants to receive updates from the parent
            //(e.g. the intersection of their quark sets). For now, we haven't 
            //optimize the code for that customization yet -- the child simply sends
            //out subscriptions for all quarks it covers, the parent will do an 
            //intersection on the quarks.

            OSDArray activeQuarks = new OSDArray();
            OSDArray passiveQuarks = new OSDArray();
            
            foreach (string aQuark in m_activeQuarkSet.Keys)
                activeQuarks.Add(aQuark);
            foreach (string pQuark in m_passiveQuarkSet.Keys)
                passiveQuarks.Add(pQuark);

            OSDMap subscriptionEncoding = new OSDMap();
            subscriptionEncoding["ActiveQuarks"] = activeQuarks;
            subscriptionEncoding["PassiveQuarks"] = passiveQuarks;
            return subscriptionEncoding;
        }

        /// <summary>
        /// Encode subscription to updates in the quarks the child subscribed to.
        /// Assertion: A parent node is interested in all updates from the quark 
        /// in a child process, forwarded in at full speed.
        /// Sending both passive and active. Doc says only active is necessary.
        /// </summary>
        /// <returns></returns>
        public OSDMap EncodeQuarkSubscriptionsForChild()
        {
            OSDArray activeQuarks = new OSDArray();
            OSDArray passiveQuarks = new OSDArray();

            foreach (string aQuark in m_activeQuarkSet.Keys)
                activeQuarks.Add(aQuark);
            foreach (string pQuark in m_passiveQuarkSet.Keys)
                passiveQuarks.Add(pQuark);

            OSDMap subscriptionEncoding = new OSDMap();
            subscriptionEncoding["ActiveQuarks"] = activeQuarks;
            subscriptionEncoding["PassiveQuarks"] = passiveQuarks;

            return subscriptionEncoding;
        }

        private Dictionary<string, List<SyncQuark>> DecodeQuarkSubscriptionsFromMsg(OSDMap data)
        {
            Dictionary<string, List<SyncQuark>> decodedSubscriptions = new Dictionary<string,List<SyncQuark>>();
            OSDArray aQuarks = (OSDArray)data["ActiveQuarks"]; ;
            OSDArray pQuarks = (OSDArray)data["PassiveQuarks"];;
            List<SyncQuark> activeQuarks = new List<SyncQuark>();
            List<SyncQuark> passiveQuarks = new List<SyncQuark>();

            foreach (OSD quark in aQuarks)
            {
                activeQuarks.Add(new SyncQuark(quark.AsString()));
            }

            foreach (OSD quark in pQuarks)
            {
                passiveQuarks.Add(new SyncQuark(quark.AsString()));
            }

            decodedSubscriptions["Active"] = activeQuarks;
            decodedSubscriptions["Passive"] = passiveQuarks;

            return decodedSubscriptions;
        }

        public void HandleSyncQuarksExchange(OSDMap data, string senderActorID, SyncConnector syncConnector, bool incomingNotification)
        {
            m_log.WarnFormat("Received this data for HandleSyncQuarksExchange: {0}",data.ToString());
            Dictionary<string, List<SyncQuark>> othersideQuarkSubs = DecodeQuarkSubscriptionsFromMsg(data);
            foreach (SyncQuark quark in othersideQuarkSubs["Active"])
            {
                if (!m_quarkSubscriptions.ContainsKey(quark.QuarkName))
                    m_quarkSubscriptions[quark.QuarkName] = new QuarkPublisher(quark);
                m_quarkSubscriptions[quark.QuarkName].AddActiveSubscriber(syncConnector);
            }
            foreach (SyncQuark quark in othersideQuarkSubs["Passive"])
            {
                if (!m_quarkSubscriptions.ContainsKey(quark.QuarkName))
                    m_quarkSubscriptions[quark.QuarkName] = new QuarkPublisher(quark);
                m_quarkSubscriptions[quark.QuarkName].AddPassiveSubscriber(syncConnector);
            }
            if (incomingNotification)
            {
                m_regionSyncModule.AddSyncConnector(syncConnector);
                OSDMap quarkData = EncodeQuarkSubscriptionsForChild();
                string quarkDataString = OSDParser.SerializeJsonString(quarkData);
                SymmetricSyncMessage reply = new SymmetricSyncMessage(SymmetricSyncMessage.MsgType.SyncQuarksSubscriptionAck, quarkDataString);
                //m_regionSyncModule.DetailedUpdateWrite("SndSubrAck", m_zeroUUID, DateTime.Now.Ticks, quarkDataString, syncConnector.OtherSideActorID, reply.Length);
                syncConnector.ImmediateOutgoingMsg(reply);
            }
        }

        #endregion //InterQuarkCommunication

        #region QuarkObjects

        public bool IsInActiveQuark(string quarkName)
        {
            return m_activeQuarkSet.ContainsKey(quarkName);
        }

        public bool IsInPassiveQuark(string quarkName)
        {
            return m_passiveQuarkSet.ContainsKey(quarkName);
        }

        // If presence or prim is crossing boundaries, returns true. Otherwise, just updates the SyncInfo for the UUID
        public bool UpdateQuarkLocation(UUID syncObjectID, HashSet<SyncableProperties.Type> updatedProperties)
        {
            SyncInfoBase sib = m_syncInfoManager.GetSyncInfo(syncObjectID);
            ScenePresence sp = m_regionSyncModule.Scene.GetScenePresence(syncObjectID);
            if (sp != null)
            {
                return UpdateScenePresenceQuarkLocation(sp,ref sib, updatedProperties);
            }

            SceneObjectPart sop = m_regionSyncModule.Scene.GetSceneObjectPart(syncObjectID);
            if (sop != null)
            {
                return UpdatePrimQuarkLocation(sop,ref sib, updatedProperties);
            }

            SceneObjectGroup sog = m_regionSyncModule.Scene.GetSceneObjectGroup(syncObjectID);
            if (sog != null)
            {
                return UpdateSOGQuarkLocation(sog,ref sib);
            }
            // Was not a scene presence, SOP or SOG.
            return false;
        }

        /// <summary>
        /// Compute whether the scene presence is changing quarks. Return true if crossing a quark boundry
        /// and update the current and previous quarks in the SyncInfo.
        /// </summary>
        /// <param name="sop"></param>
        /// <param name="updatedProperties">The properties that were updated. Used to see if position
        /// changed and if we should check whether quarks possibly changed</param>
        /// <returns>true if the sop is a root prim and it is crossing a quark boundry</returns>
        public bool UpdateScenePresenceQuarkLocation(ScenePresence sp,
                           ref SyncInfoBase sib, HashSet<SyncableProperties.Type> updatedProperties)
        {
            bool ret = false;
            if (updatedProperties.Contains(SyncableProperties.Type.AbsolutePosition))
            {
                // Note that all SOPs in a linkset are in the quark of the root SOP (ie, always using GroupPosition).
                // Someday see if a better design is possible for spacialy large linksets.
                Vector3 spLoc = sp.AbsolutePosition;
                string currentQuarkName = SyncQuark.GetQuarkNameByPosition(spLoc);
                if (currentQuarkName != sib.CurQuark.QuarkName)
                {
                    // If we are not in the same quark as we used to be, remember where we were
                    SyncQuark currentQuark = new SyncQuark(spLoc);
                    sib.PrevQuark = sib.CurQuark;
                    sib.CurQuark = currentQuark;
                    ret = true;
                }
            }
            return ret;
        }

        /// <summary>
        /// Compute whether the sop is changing quarks. Return true if crossing a quark boundry
        /// and update the current and previous quarks in the PrimSyncInfo.
        /// </summary>
        /// <param name="sop"></param>
        /// <param name="updatedProperties">The properties that were updated. Used to see if position
        /// changed and if we should check whether quarks possibly changed</param>
        /// <returns>true if the sop is a root prim and it is crossing a quark boundry</returns>
        public bool UpdatePrimQuarkLocation(SceneObjectPart sop,
                            ref SyncInfoBase sib, HashSet<SyncableProperties.Type> updatedProperties)
        {
            bool ret = false;
            if (updatedProperties.Contains(SyncableProperties.Type.Position)
                    || updatedProperties.Contains(SyncableProperties.Type.GroupPosition))
            {
                // Note that all SOPs in a linkset are in the quark of the root SOP (ie, always using GroupPosition).
                // Someday see if a better design is possible for spacialy large linksets.
                Vector3 groupLoc = sop.GroupPosition;
                string currentQuarkName = SyncQuark.GetQuarkNameByPosition(groupLoc);
                if (currentQuarkName != sib.CurQuark.QuarkName)
                {
                    // If we are not in the same quark as we used to be, remember where we were
                    SyncQuark currentQuark = new SyncQuark(groupLoc);
                    sib.PrevQuark = sib.CurQuark;
                    sib.CurQuark = currentQuark;
                    ret = true;
                }
            }
            return ret;
        }

        // Check to see if current quark has changed (based on GroupPosition).
        // If changed, update the quarks in the PrimSyncInfo and return 'true'.
        public bool UpdateSOGQuarkLocation(SceneObjectGroup sog, ref SyncInfoBase sib)
        {
            bool ret = false;
            if (sib != null)
            {
                SyncQuark currentQuark = new SyncQuark(sog.RootPart.GroupPosition);
                if (!currentQuark.Equals(sib.CurQuark))
                {
                    // If we are not in the same quark as we used to be, remember where we were
                    sib.PrevQuark = sib.CurQuark;
                    sib.CurQuark = currentQuark;
                    ret = true;
                }
            }
            return ret;
        }

        public bool UpdateSOGQuarkLocation(SceneObjectGroup sog, ref SyncInfoBase sib, string preQuark, string curQuark)
        {
            bool ret = false;
            if (sib != null)
            {
                SyncQuark currentQuark = new SyncQuark(curQuark);
                if (!currentQuark.Equals(sib.CurQuark))
                {
                    // If we are not in the same quark as we used to be, remember where we were
                    sib.PrevQuark = sib.CurQuark;
                    sib.CurQuark = currentQuark;

                    //m_log.DebugFormat("UpdatePrimQuarkLocation: {0} from quark {1} to {2}", sog.Name, psi.PrevQuark, psi.CurQuark);

                    ret = true;
                }
            }
            return ret;
        }
        #endregion

        #region QuarkSubscriptions
        public void AddPassiveSubscription(SyncConnector connector, string quarkName) 
        {

            m_quarkSubscriptions[quarkName].AddPassiveSubscriber(connector);
        }

        public void AddActiveSubscription(SyncConnector connector, string quarkName)
        {
            m_quarkSubscriptions[quarkName].AddActiveSubscriber(connector);
        }

        // Iterates over every quark subscription and deletes the connector reference from it. 
        // Homework: Is there a better way to do this?
        public void RemoveSubscription(SyncConnector connector)
        {
            foreach (KeyValuePair<string,QuarkPublisher> subscription in m_quarkSubscriptions)
            {
                subscription.Value.RemoveSubscriber(connector);
            }
        }

        // Removes the connector from the quark subscription list of name quarkName
        public void RemoveSubscription(SyncConnector connector,string quarkName) 
        {
            m_quarkSubscriptions[quarkName].RemoveSubscriber(connector);
        }

        // Returns the hashset of all syncconnectors subscribed to this Quark
        public HashSet<SyncConnector> GetQuarkSubscribers(string quarkName)
        {
            if (m_quarkSubscriptions.ContainsKey(quarkName))
                return m_quarkSubscriptions[quarkName].GetAllQuarkSubscribers();
            else
            {
                m_log.WarnFormat("GetQuarkSubscribers: There should be at least one subscription (parent) here");
                return new HashSet<SyncConnector>();
            }
        }
        #endregion // QuarkSubscriptions
        
        /// <summary>
        /// Decode the set of quarks under the same subscription. 
        /// Format: "xl[-xr] or x, yl[-yr] or y/.../..."
        /// </summary>
        /// <param name="quarksInput"></param>
        /// <returns></returns>

        #region QuarkCrossing

        // Called when a quark crossing was detected, either for a scene presence, SOP or SOG
        public SymmetricSyncMessage QuarkCrossingUpdate(SyncInfoBase syncInfo, HashSet<SyncableProperties.Type> updatedProperties)
        {
            SymmetricSyncMessage ret = null;
            ScenePresence sp = m_regionSyncModule.Scene.GetScenePresence(syncInfo.UUID);
            if (sp != null)
            {
                ret = QuarkCrossingSPUpdate(sp, (SyncInfoPresence)syncInfo, updatedProperties);
            }
            SceneObjectPart sop = m_regionSyncModule.Scene.GetSceneObjectPart(syncInfo.UUID);
            if (sop != null)
            {
                ret = QuarkCrossingSOPUpdate(sop, (SyncInfoPrim)syncInfo, updatedProperties);
            }
            SceneObjectGroup sog = m_regionSyncModule.Scene.GetSceneObjectGroup(syncInfo.UUID);
            if (sog != null)
            {
                ret = QuarkCrossingSOGUpdate(sog, (SyncInfoPrim)syncInfo, updatedProperties);
            }
            return ret;
        }

        private SymmetricSyncMessage QuarkCrossingSPUpdate(ScenePresence sp, SyncInfoPresence sip, HashSet<SyncableProperties.Type> updatedProperties)
        {
            bool leavingMyQuarks = !IsInActiveQuark(sip.CurQuark.QuarkName);
            SymmetricSyncMessage syncMsg = null;
            OSDMap syncData = CreateSPQuarkCrossingMessage(sp, sip, updatedProperties);
            if (syncData != null && syncData.Count > 0)
            {
                // Send the update message to all the connectors to the quarks
                syncMsg = new SymmetricSyncMessage(SymmetricSyncMessage.MsgType.QuarkCrossingSPFullUpdate, OSDParser.SerializeJsonString(syncData));
            }
            return syncMsg;
        }

        private SymmetricSyncMessage QuarkCrossingSOPUpdate(SceneObjectPart sop, SyncInfoPrim sip, HashSet<SyncableProperties.Type> updatedProperties)
        {
            m_log.Warn("SOP ATTEMPTING TO CROSS WITHOUT SOG");
            return null;
        }

        private SymmetricSyncMessage QuarkCrossingSOGUpdate(SceneObjectGroup sog, SyncInfoPrim sip, HashSet<SyncableProperties.Type> updatedProperties)
        {
            // The sop is a root prim and is changing quarks (curQuark != prevQuark)
            // This sends the information necessary to create the whole SOG in the target quark.
            // The individual child SOPs will never send out a quark crossing message.
            
            // m_log.DebugFormat("{0}: SendPrimPropertyUpdates: quark changing: c/p={1}/{2}, obj={3}",
            //                 LogHeader, psi.CurQuark.QuarkName, psi.PrevQuark.QuarkName, 
            //                 sog == null ? "sog is null" : sog.UUID.ToString());
            
            bool leavingMyQuarks = !IsInActiveQuark(sip.CurQuark.QuarkName);
            //string updateCode = leavingMyQuarks ? "SendUpdatD" : "SendUpdatX";
            SymmetricSyncMessage syncMsg = null;
            OSDMap syncData = CreateSOGQuarkCrossingMessage(sog, sip, updatedProperties);
            if (syncData != null && syncData.Count > 0)
            {
                // Send the update message to all the connectors to the quarks
                syncMsg = new SymmetricSyncMessage(SymmetricSyncMessage.MsgType.QuarkCrossingFullUpdate, OSDParser.SerializeJsonString(syncData));
            }

            TriggerSOGQuarkCrossingEvent(sip.CurQuark.QuarkName, sip.PrevQuark.QuarkName, sog);

            // if the prim is not in the quarks I manage, remove it from the scenegraph
            if (leavingMyQuarks)
            {
                // m_log.DebugFormat("{0}: SendPrimPropertyUpdates: not in my quark. Deleting object. sog={1}, sop={2}", LogHeader, sog.UUID, sop.UUID);
                // ?? How do I delete scene object? Is there more I need to do?
                // DeleteSceneObjectBySync(sog);
                m_regionSyncModule.Scene.DeleteSceneObject(sog, false);
            }
            return syncMsg;
        }

        // Given an SOG and sync information, create a QuarkCrossing message
        private OSDMap CreateSPQuarkCrossingMessage(ScenePresence sp, SyncInfoPresence sip, HashSet<SyncableProperties.Type> updatedProperties)
        {
            OSDMap ret = null;

            try
            {
                // If moving across quark boundries we pack both the sop update and the
                // full sog just in case the receiver has to create a new object (moving into a quark)
                ret = m_syncInfoManager.EncodeProperties(sp.UUID, updatedProperties);
                ret["FULL-SP"] = m_regionSyncModule.EncodeScenePresence(sp);
                // pass the quark names so the receiver can decide what to do with this update
                ret["curQuark"] = OSD.FromString(sip.CurQuark.QuarkName);
                ret["prevQuark"] = OSD.FromString(sip.PrevQuark.QuarkName);
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("{0}: CreateSPQuarkCrossingMessage: Error in encoding SOG {1}, {2}: {3}", LogHeader, sp.Name, sp.UUID, e);
                ret = null;
            }
            return ret;
        }

        // Given an SOG and sync information, create a QuarkCrossing message
        private OSDMap CreateSOGQuarkCrossingMessage(SceneObjectGroup sog, SyncInfoPrim sip, HashSet<SyncableProperties.Type> updatedProperties)
        {
            OSDMap ret = null;

            try
            {
                // If moving across quark boundries we pack both the sop update and the
                // full sog just in case the receiver has to create a new object (moving into a quark)
                ret = m_syncInfoManager.EncodeProperties(sog.RootPart.UUID, updatedProperties);
                ret["FULL-SOG"] = m_regionSyncModule.EncodeSceneObject(sog);
                // pass the quark names so the receiver can decide what to do with this update
                ret["curQuark"] = OSD.FromString(sip.CurQuark.QuarkName);
                ret["prevQuark"] = OSD.FromString(sip.PrevQuark.QuarkName);
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("{0}: CreateQuarkSOGCrossingMessage: Error in encoding SOG {1}, {2}: {3}", LogHeader, sog.Name, sog.UUID, e);
                ret = null;
            }
            return ret;
        }

        private void TriggerSPQuarkCrossingEvent(string curQuark, string preQuark, ScenePresence sp)
        {
            //Trigger the quark-crossing event, so that local modules can get it 
            EventManager.QuarkCrossingType crossingType = EventManager.QuarkCrossingType.Unknown;
            //string curQuark = primSyncInfo.CurQuark.QuarkName;
            //string preQuark = primSyncInfo.PrevQuark.QuarkName;
            bool curQuarkIsActive = IsInActiveQuark(curQuark);
            bool preQuarkIsActive = IsInActiveQuark(preQuark);

            if (preQuarkIsActive && curQuarkIsActive)
                crossingType = EventManager.QuarkCrossingType.ActiveToActive;
            else if (preQuarkIsActive && !curQuarkIsActive)
                crossingType = EventManager.QuarkCrossingType.ActiveToPassive;
            else if (!preQuarkIsActive && curQuarkIsActive)
                crossingType = EventManager.QuarkCrossingType.PassiveToActive;
            else if (!preQuarkIsActive && !curQuarkIsActive)
                crossingType = EventManager.QuarkCrossingType.PassiveToPassive;

            m_regionSyncModule.Scene.EventManager.TriggerSPQuarkCrossing(curQuark, preQuark, sp, crossingType);
        }

        private void TriggerSOGQuarkCrossingEvent(string curQuark, string preQuark, SceneObjectGroup sog)
        {
            //Trigger the quark-crossing event, so that local modules can get it 
            EventManager.QuarkCrossingType crossingType = EventManager.QuarkCrossingType.Unknown;
            //string curQuark = primSyncInfo.CurQuark.QuarkName;
            //string preQuark = primSyncInfo.PrevQuark.QuarkName;
            bool curQuarkIsActive = IsInActiveQuark(curQuark);
            bool preQuarkIsActive = IsInActiveQuark(preQuark);

            if (preQuarkIsActive && curQuarkIsActive)
                crossingType = EventManager.QuarkCrossingType.ActiveToActive;
            else if (preQuarkIsActive && !curQuarkIsActive)
                crossingType = EventManager.QuarkCrossingType.ActiveToPassive;
            else if (!preQuarkIsActive && curQuarkIsActive)
                crossingType = EventManager.QuarkCrossingType.PassiveToActive;
            else if (!preQuarkIsActive && !curQuarkIsActive)
                crossingType = EventManager.QuarkCrossingType.PassiveToPassive;

            m_regionSyncModule.Scene.EventManager.TriggerSOGQuarkCrossing(curQuark, preQuark, sog, crossingType);
        }
        
        #endregion //QuarkCrossing

        #region EventHandlingQuark
        /// <summary>
        /// The prim properties changed and it has moved across quark boundries. There are several
        /// conditions:
        /// <ol>
        /// <li>both current and prev quarks are active. Just update the changed properties</li>
        /// <li>the current quark is not managed by me. Delete the sog from the scenegraph</li>
        /// <li>the current quark is not managed by me. Delete the sog from the scenegraph</li>
        /// </ol>
        /// </summary>
        /// <param name="msg"></param>
        /// <param name="senderActorID"></param>
        public void HandleQuarkCrossingFullUpdate(SymmetricSyncMessage msg, string senderActorID)
        {
            OSDMap data = RegionSyncModule.DeserializeMessage(msg);

            UUID primUUID = data["primUUID"].AsUUID();
            SceneObjectPart sop = m_regionSyncModule.Scene.GetSceneObjectPart(primUUID);
            SceneObjectGroup sog = null;

            string currentQuarkName = data["curQuark"].AsString();
            string previousQuarkName = data["prevQuark"].AsString();
            HashSet<SyncableProperties.Type> propertiesUpdated = new HashSet<SyncableProperties.Type>();
            Dictionary<UUID,SyncInfoBase> propertiesSyncInfo = null;

            bool toDeleteDueToQuarkCrossing = false;

            int bgnTime = Util.EnvironmentTickCount();

            int casecode = sop == null ? 0 : 1;
            casecode += IsInActiveQuark(currentQuarkName) ? 2 : 0;
            switch (casecode)
            {
                case 0: // current is not our quark and we don't know about the object
                    m_log.WarnFormat("{0}: HandleQuarkCrossingFullUpdate: Case 0", LogHeader);
                    // Can happen when linksets are deleted. Just ignore it.
                    // m_log.DebugFormat("{0}: HandleQuarkCrossingFullUpdate({1}): not our current and object not in scenegraph: c/p={2}/{3}, obj={4}", 
                    //                 LogHeader, senderActorID, currentQuarkName, previousQuarkName, primUUID);
                    // DSG DEBUG
                    m_regionSyncModule.DetailedUpdateWrite("RecUpdate0", primUUID, DateTime.Now.Ticks, m_zeroUUID, senderActorID, msg.Data.Length);
                    // END DSG DEBUG
                    break;
                case 1: // current is not our quark and we know about the object
                    // Remove the object from the scenegraph
                    m_log.WarnFormat("{0}: HandleQuarkCrossingFullUpdate: Case 1", LogHeader);
                    sog = sop.ParentGroup;
                    if (sog == null)
                    {
                        m_log.ErrorFormat("{0}: HandleQuarkCrossingFullUpdate({1}): Trying to remove from scenegraph. Got SOP but SOG not found. : c/p={2}/{3}, obj={4}",
                                    LogHeader, senderActorID, currentQuarkName, previousQuarkName, sop.UUID);
                        break;
                    }
                    if (m_detailUpdateDebugLog)
                        m_log.DebugFormat("{0}: HandleQuarkCrossingFullUpdate({1}): removing sog from scenegraph: c/p={2}/{3}, obj={4}",
                                    LogHeader, senderActorID, currentQuarkName, previousQuarkName, sog.UUID);

                    // Before deleting it, flush out any updates that may have been queued on this object
                    // Have to send updates for all the children of this SOG
                    m_log.WarnFormat("{0}: HandleQuarkCrossingFullUpdate: TODO: Force all SOPs to be pushed", LogHeader);                    /*
                    sog.ForEachPart(delegate(SceneObjectPart sopp)
                    {
                        // if there are updates for this prim, send them out right now
                        if (!sopp.IsRoot)
                            ForcePropertyUpdateSend(sopp);
                            // ?? Fix this
                    });
                     * */

                    //m_regionSyncModule.HandleUpdatedProperties(data, senderActorID, SymmetricSyncMessage.MsgType.UpdatedProperties);

                    /*
                    // Apply the received property updates
                    SceneObjectGroup decodedsog;
                    m_regionSyncModule.DecodeSceneObject(data, out decodedsog, out propertiesSyncInfo);
                    if (propertiesSyncInfo.Count > 0)
                    {
                        //Add the list of SyncInfo to SyncInfoManager
                        foreach (KeyValuePair<UUID, SyncInfoBase> kvp in propertiesSyncInfo)
                            m_syncInfoManager.InsertSyncInfo(kvp.Key, kvp.Value);
                        // DSG DEBUG
                        // m_regionSyncModule.DetailedUpdateLogging(sop.UUID, propertiesUpdated, propertiesSyncInfo, "RecUpdate1", senderActorID, msg.Data.Length);
                        // END DSG DEBUG
                    }

                    // relay the border crossing message to connectors to the previous quark if not a leaf node
                    //ForwardQuarkCrossingPropertyUpdateMessage(sog);
                    ForwardQuarkCrossingPropertyUpdateMessage(sog, senderActorID);
                    */

                    // Remove the scene object group
                    //this.DeleteSceneObjectBySync(sog);
                    

                    //We need to remove the SOG, but let's remove at the end of this 
                    //function call, after we have triggered quark-crossing event,
                    //and whoever subscribe to the event has got a chance to operate 
                    //on the object before it is deleted
                    // ?? How do I make this work?
                    toDeleteDueToQuarkCrossing = true;
                    break;
                case 2: // current is one of our quarks and we don't know about the object
                    // The object is not in our scenegraph. We need to add it but this is not like a new object.
                    m_log.WarnFormat("{0}: HandleQuarkCrossingFullUpdate: Case 2", LogHeader);
                    if (m_detailUpdateDebugLog)
                        m_log.DebugFormat("{0}: HandleQuarkCrossingFullUpdate({1}): adding new object to scenegraph: c/p={2}/{3}, obj={4}",
                                    LogHeader, senderActorID, currentQuarkName, previousQuarkName, primUUID);
                    m_regionSyncModule.DetailedUpdateWrite("RecUpdate2", primUUID, DateTime.Now.Ticks, m_zeroUUID, senderActorID, msg.Length);

                    // ??
                    //m_regionSyncModule.HandleSyncNewObject((OSDMap)data["FULL-SOG"], senderActorID);
                    // int endTime2 = Util.EnvironmentTickCountSubtract(bgnTime);
                    // DetailedUpdateWrite("TimeUpdat2", primUUID, DateTime.Now.Ticks, "", senderActorID, endTime2);
                    break;
                case 3: // current is one of our quarks and we know about the object
                    // This is just an update
                    // This happens when an object moves across a border and we have both quarks in our set. It just moves.
                    m_log.WarnFormat("{0}: HandleQuarkCrossingFullUpdate: Case 3", LogHeader);
                    if (m_detailUpdateDebugLog)
                        m_log.DebugFormat("{0}: HandleQuarkCrossingFullUpdate({1}): just an update: c/p={2}/{3}, obj={4}",
                                    LogHeader, senderActorID, currentQuarkName, previousQuarkName, sop.UUID);
                    /*
                    propertiesSyncInfo = m_syncInfoManager.DecodeProperties(data);

                    if (propertiesSyncInfo.Count > 0)
                    {
                        propertiesUpdated = m_primSyncInfoManager.UpdatePrimSyncInfoBySync(sop, propertiesSyncInfo);
                    }
                    */
                    // m_regionSyncModule.DetailedUpdateLogging(sop.UUID, propertiesUpdated, propertiesSyncInfo, "RecUpdate3", senderActorID, msg.Data.Length);

                    // Pass the quark crossing event down the chain
                    sog = sop.ParentGroup;
                    if (sog != null)
                    {
                        SyncInfoBase sib = m_syncInfoManager.GetSyncInfo(sog.UUID);
                        // make sure the current quarks for the group are correct
                        UpdateSOGQuarkLocation(sog, ref sib, previousQuarkName, currentQuarkName);
                        // send the quark crossing message to downstream systems
                        if (m_regionSyncModule.IsSyncRelay)
                            ForwardQuarkCrossingPropertyUpdateMessage(sog, senderActorID);
                    }
                    break;
            }

            if (sop != null && sop.ParentGroup != null)
            {
                if (propertiesUpdated.Count > 0 &&
                        (propertiesUpdated.Contains(SyncableProperties.Type.Position) ||
                        propertiesUpdated.Contains(SyncableProperties.Type.GroupPosition)))
                    TriggerSOGQuarkCrossingEvent(currentQuarkName, previousQuarkName, sop.ParentGroup);
            }

            if (toDeleteDueToQuarkCrossing && sog != null)
            {
                //DeleteSceneObjectBySync(sog);
                int endTime1 = Util.EnvironmentTickCountSubtract(bgnTime);
                m_regionSyncModule.DetailedUpdateWrite("TimeUpdat1", primUUID, DateTime.Now.Ticks, "", senderActorID, endTime1);
            }

            return;
        }

        public void HandleQuarkCrossingSPFullUpdate(SymmetricSyncMessage msg, string senderActorID)
        {
            OSDMap data = RegionSyncModule.DeserializeMessage(msg);
            UUID primUUID = data["presenceUUID"].AsUUID();
            m_log.WarnFormat("{0}: HandleQuarkCrossingSPFullUpdate: NOT YET IMPLEMENTED", LogHeader);
            ScenePresence sp = m_regionSyncModule.Scene.GetScenePresence(primUUID);

            string currentQuarkName = data["curQuark"].AsString();
            string previousQuarkName = data["prevQuark"].AsString();
            HashSet<SyncableProperties.Type> propertiesUpdated = new HashSet<SyncableProperties.Type>();
            //Dictionary<UUID,SyncInfoBase> propertiesSyncInfo = null;

            //bool toDeleteDueToQuarkCrossing = false;

            int bgnTime = Util.EnvironmentTickCount();

            int casecode = sp == null ? 0 : 1;
            casecode += IsInActiveQuark(currentQuarkName) ? 2 : 0;
            switch (casecode)
            {
                case 0:
                    m_log.WarnFormat("{0}: HandleQuarkCrossingSPFullUpdate: Case 0", LogHeader);
                    break;
                case 1:
                    m_log.WarnFormat("{0}: HandleQuarkCrossingSPFullUpdate: Case 1", LogHeader);
                    break;
                case 2:
                    m_log.WarnFormat("{0}: HandleQuarkCrossingSPFullUpdate: Case 2", LogHeader);
                    break;
                case 3:
                    m_log.WarnFormat("{0}: HandleQuarkCrossingSPFullUpdate: Case 3", LogHeader);
                    break;

            }
        }

        // create and send a quark crossing event for the passed sog is we are a relay node
        //private void ForwardQuarkCrossingPropertyUpdateMessage(SceneObjectGroup sog)
        private void ForwardQuarkCrossingPropertyUpdateMessage(SceneObjectGroup sog, string senderActorID)
        {
            m_log.WarnFormat("{0}: ForwardQuarkCrossingPropertyUpdateMessage", LogHeader);
            /*
            HashSet<SyncableProperties.Type> propertiesUpdated;
            if (m_regionSyncModule.IsSyncRelay)
            {
                lock (m_primPropertyUpdateLock)
                {
                    // snatch the list of updated properties for this SOG. Remove from list so won't be sent later.
                    if (m_primPropertyUpdates.TryGetValue(sog.UUID, out propertiesUpdated))
                    {
                        m_primPropertyUpdates.Remove(sog.UUID);
                    }
                    else
                    {
                        propertiesUpdated = new HashSet<SceneObjectPartSyncProperties>();
                    }
                }
                PrimSyncInfo psi = m_primSyncInfoManager.GetPrimSyncInfo(sog.RootPart.UUID);
                if (psi != null)
                {
                    OSDMap syncData = CreateQuarkCrossingMessage(sog, psi, propertiesUpdated);

                    // Send the update message to all the connectors to the quarks
                    if (syncData != null && syncData.Count > 0)
                    {
                        SymmetricSyncMessage syncMsg = new SymmetricSyncMessage(SymmetricSyncMessage.MsgType.QuarkCrossingFullUpdate, OSDParser.SerializeJsonString(syncData));
                        m_syncManager.ForwardQuarkCrossingPrimUpdates(sog, syncMsg, "SendUpdat2", propertiesUpdated, senderActorID);
                    }
                }
            }
             * */
        }
        #endregion //EventHandlingQuark


    }
}
