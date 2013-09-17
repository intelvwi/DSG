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
    /// <summary>
    /// QuarkPublisher
    /// Description: Stores all SyncConnectors subscribed actively and passively to a quark. Only quarks that belong to this sync process.
    /// </summary>
    public class QuarkPublisher
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
        /// Removes connector from active and passive quark subscriptions, if it exists. 
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
        /// Returns all subscribers with QuarkName (both active and passive)
        /// </summary>
        /// <returns>Union of the active and passive syncconnectors subscribed to this quark</returns>
        public HashSet<SyncConnector> GetAllQuarkSubscribers()
        {
            HashSet<SyncConnector> subscribers = new HashSet<SyncConnector>(m_activeQuarkSubscribers);
            subscribers.UnionWith(m_passiveQuarkSubscribers);
            return subscribers;
        }

    }
    
    /// <summary>
    /// Actor
    /// Description: Stores all active and passive quarks an actor is subscribed to. It is the reverse lookup used in Quark Publisher.
    /// </summary>
    public class Actor
    {
        public HashSet<SyncQuark> ActiveQuarks = new HashSet<SyncQuark>();
        public HashSet<SyncQuark> PassiveQuarks = new HashSet<SyncQuark>();
        public HashSet<SyncQuark> AllQuarks
        {
            get
            {
                HashSet<SyncQuark> allQuarks = new HashSet<SyncQuark>();
                allQuarks.UnionWith(ActiveQuarks);
                allQuarks.UnionWith(PassiveQuarks);
                return allQuarks;
            }
        }
    }

    /// <summary>
    /// QuarkManager
    /// Description: Class responsible for dealing with all quark-related methods and data. 
    /// </summary>
    public class QuarkManager
    {
        private static string LogHeader = "[QUARKMANAGER]";
        
        // Parent address and port to connect to. Currently only one, TBD multiple parents
        private string m_parentAddress;
        private int m_parentPort;

        // Dictionary of objects that has left the quark set for this actor. This is to prevent conflicting messages once the object has 
        // detected as crossed.
        private Dictionary<UUID,bool> m_leftQuarks = new Dictionary<UUID,bool>();
        public Dictionary<UUID,bool> LeftQuarks
        {
            get { return m_leftQuarks; }
        }

        // Quark size in meters, X and Y
        private int m_quarkSizeX;
        private int m_quarkSizeY;

        private SyncInfoManager m_syncInfoManager;

        public static ILog m_log;

        // This actor's active and passive quarks, as determined from config file (coded string format). Keeping stored for sending to others.
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

        // Transformed strings into SyncQuarks. 
        private Dictionary<string, SyncQuark> m_activeQuarkSet = new Dictionary<string, SyncQuark>();
        private Dictionary<string, SyncQuark> m_passiveQuarkSet = new Dictionary<string, SyncQuark>();
        public Dictionary<string, SyncQuark> ActiveQuarkDictionary
        {
            get { return m_activeQuarkSet; }
        }

        public Dictionary<string, SyncQuark> PassiveQuarkDictionary
        {
            get { return m_passiveQuarkSet; }
        }

        private Dictionary<SyncConnector, Actor> m_actorSubscriptions = new Dictionary<SyncConnector, Actor>();
        public Dictionary<SyncConnector, Actor> ActorSubscriptions
        {
            get { return m_actorSubscriptions; }
        }

        private Dictionary<string, QuarkPublisher> m_quarkSubscriptions = new Dictionary<string,QuarkPublisher>();
        public Dictionary<string, QuarkPublisher> QuarkSubscriptions
        {
            get { return m_quarkSubscriptions; }
        }


        RegionSyncModule m_regionSyncModule;

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
            m_syncInfoManager = m_regionSyncModule.InfoManager;
            
            // Size of quarks
            m_quarkSizeX = config.GetInt("SyncQuarkSizeX", 256);
            m_quarkSizeY = config.GetInt("SyncQuarkSizeY", 256);

            // Set SyncQuark objects size variabe
            SyncQuark.SizeX = m_quarkSizeX;
            SyncQuark.SizeY = m_quarkSizeY;

            // Read string from config file
            ActiveQuarkSubscription = config.GetString("SyncActiveQuarks", String.Empty);
            // If its not in the Simulator settings, look for it in the Region settings
            if (ActiveQuarkSubscription == String.Empty)
            {
                ActiveQuarkSubscription = m_regionSyncModule.Scene.RegionInfo.GetOtherSetting("SyncActiveQuarks");
                if (ActiveQuarkSubscription == null)
                    ActiveQuarkSubscription = String.Empty;
            }
            ActiveQuarkSubscription = ActiveQuarkSubscription.Trim();

            PassiveQuarkSubscription = config.GetString("SyncPassiveQuarks", String.Empty);
            // If its not in the Simulator settings, look for it in the Region settings
            if (PassiveQuarkSubscription == String.Empty)
            {
                PassiveQuarkSubscription = m_regionSyncModule.Scene.RegionInfo.GetOtherSetting("SyncPassiveQuarks");
                if (PassiveQuarkSubscription == null)
                    PassiveQuarkSubscription = String.Empty;
            }
            PassiveQuarkSubscription = PassiveQuarkSubscription.Trim();

            // Parse into hashsets
            //First, decode subscription for active quarks
            HashSet<SyncQuark> activeQuarks = DecodeSyncQuarks(ActiveQuarkSubscription);
            
            //Then decode subscription for passive quarks
            HashSet<SyncQuark> passiveQuarks = DecodeSyncQuarks(PassiveQuarkSubscription);

            foreach (SyncQuark quark in activeQuarks)
            {
                if (quark.ValidQuark)
                {
                    m_activeQuarkSet.Add(quark.QuarkName, quark);
                }
            }

            foreach (SyncQuark quark in passiveQuarks)
            {
                if (quark.ValidQuark)
                {
                    m_passiveQuarkSet.Add(quark.QuarkName, quark);
                }
            }

            // Register my active quarks with a quark service.
            // TODO: Finish a quark service server which all actors can connect to, give their identification, and receive the list of parents
            // they should connect to. Currently, connecting to parents as determined in the config file.
            // "The union of the returned quark sets of these sync process should be a superset as the querying process’s quark set."

            // This part retrieves the information from the config file for a parent to connect to, or assumes 127.0.0.1. This should be 
            // thrown away once a quark service is in place.
            #region THROWMEAWAY
            // STUB! Gets parent node address from config file in simulator
            m_parentAddress = config.GetString("ParentAddress", "");
            m_parentPort = config.GetInt("ParentPort", -1);

            List<RegionSyncListenerInfo> superSetQuarks = new List<RegionSyncListenerInfo>();
            if (m_parentAddress.Length == 0)
            {
                m_parentAddress = "127.0.0.1";
                m_parentPort = 15000;
            }
            RegionSyncListenerInfo test_parent = new RegionSyncListenerInfo(m_parentAddress,m_parentPort);
            superSetQuarks.Add(test_parent);
            #endregion

            if (!m_regionSyncModule.IsRoot)
            {
                foreach (RegionSyncListenerInfo rsli in superSetQuarks)
                {
                    SyncConnector syncConnector = m_regionSyncModule.StartNewSyncConnector(rsli);
                    if (syncConnector == null)
                        m_log.ErrorFormat("{0}: Failed to connecto to parent sync {1}",LogHeader,rsli.ToString());
                    else
                        m_log.WarnFormat("{0}: Success creating a connection to parent: {1}",LogHeader,rsli.ToString());
                }

                if (!m_regionSyncModule.IsSyncingWithOtherSyncNodes())
                {
                    m_log.ErrorFormat("{0}: Failed to start at least one sync connector. Not syncing.", LogHeader);
                    return;
                }
                m_log.WarnFormat("{0}: Finished loading quarks: SyncActiveQuarks:{1} and SyncPassiveQuarks:{2}", LogHeader, ActiveQuarkSubscription, PassiveQuarkSubscription);
            }
            else
            {
                m_log.WarnFormat("{0}: Root process started.", LogHeader);
            }
        }


        /// <summary>
        /// Decode the set of quarks under the same subscription. 
        /// Format: "xl[-xr] or x, yl[-yr] or y/.../..."
        /// </summary>
        /// <param name="quarksInput"></param>
        /// <returns></returns>
        private HashSet<SyncQuark> DecodeSyncQuarks(string quarksInput)
        {
            if (quarksInput.Equals(String.Empty) || quarksInput.Equals("[]"))
                return new HashSet<SyncQuark>();

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
            HashSet<SyncQuark> quarksOutput = new HashSet<SyncQuark>();

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

                        quarksOutput.Add(new SyncQuark(quarkName));
                    }
                }
            }

            return quarksOutput;
        }

        #endregion // Quark Registration

        #region Dynamic Quark Subscriptions

        // When called, adds the list off active and passive quarks (string-encoded) to the list of quarks subscribed by this actor respectively.
        // Additionally, informs neighbors of the new subscriptions and request for the data in the new quarks being added, if necessary.
        public bool AddNewQuark(string activeStr, string passiveStr)
        {
            HashSet<SyncQuark> active = DecodeSyncQuarks(activeStr);
            HashSet<SyncQuark> passive = DecodeSyncQuarks(passiveStr);

            if (!QuarkChangeSanityCheck(active, passive))
            {
                m_log.ErrorFormat("{0}: Could not add the new quarks to this actor. No parent connectors with quarks in common. Aborting.");
                return false;
            }

            foreach (SyncQuark quark in active)
            {
                if (quark.ValidQuark)
                {
                    m_log.DebugFormat("Add record for active quark {0}", quark.QuarkName);
                    m_activeQuarkSet.Add(quark.QuarkName, quark);
                }
            }

            foreach (SyncQuark quark in passive)
            {
                if (quark.ValidQuark)
                {
                    m_log.DebugFormat("Add record for passive quark {0}", quark.QuarkName);
                    m_passiveQuarkSet.Add(quark.QuarkName, quark);
                }
            }
            m_regionSyncModule.SendSyncMessageAll(new SyncMsgQuarkSubAdd(m_regionSyncModule,active,passive));
            return true;
        }

        // Checks if the new list of quarks are a possible set before broadcasting to neighbors. 
        private bool QuarkChangeSanityCheck(HashSet<SyncQuark> active, HashSet<SyncQuark> passive)
        {
            bool failed = true;
            HashSet<SyncQuark> all = new HashSet<SyncQuark>();
            all.UnionWith(active);
            all.UnionWith(passive);

            foreach (SyncQuark quark in all)
            {
                foreach (Actor actor in m_actorSubscriptions.Values)
                {
                    if (actor.ActiveQuarks.Contains(quark) || actor.PassiveQuarks.Contains(quark))
                        failed = false;
                }
            }
            
            if (failed)
                return false;
            return true;
        }

        #endregion // Dynamic Quark Subscriptions

        #region QuarkSubscriptions
        // Adds the specified SyncConnector to the QuarkSubscription object as a passive subscriber.
        public void AddPassiveSubscription(SyncConnector connector, string quarkName)
        {
            m_quarkSubscriptions[quarkName].AddPassiveSubscriber(connector);
        }

        // Adds the specified SyncConnector to the QuarkSubscription object as an active subscriber.
        public void AddActiveSubscription(SyncConnector connector, string quarkName)
        {
            m_quarkSubscriptions[quarkName].AddActiveSubscriber(connector);
        }

        // Iterates over every quark subscription and deletes the connector reference from it. 
        // TODO: This could be made faster by using the Actor reverse-lookup dictionary instead.
        public void RemoveSubscription(SyncConnector connector)
        {
            foreach (KeyValuePair<string, QuarkPublisher> subscription in m_quarkSubscriptions)
            {
                subscription.Value.RemoveSubscriber(connector);
            }
        }

        // Removes the connector from the quark subscription list of name QuarkName
        public void RemoveSubscription(SyncConnector connector, string quarkName)
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
                //m_log.WarnFormat("GetQuarkSubscribers: There should be at least one subscription (parent) here");
                return new HashSet<SyncConnector>();
            }
        }
        #endregion // Quark Subscribers

        #region QuarkObjects

        // Is the quarkName in this actor's active quark set?
        public bool IsInActiveQuark(string quarkName)
        {
            return m_activeQuarkSet.ContainsKey(quarkName);
        }

        // Is the quarkName in this actor's passive quark set?
        public bool IsInPassiveQuark(string quarkName)
        {
            return m_passiveQuarkSet.ContainsKey(quarkName);
        }

        // If presence or prim of UUID syncObjectID is crossing boundaries, returns true. Otherwise, just updates the SyncInfo for the UUID.
        public bool UpdateQuarkLocation(UUID syncObjectID, HashSet<SyncableProperties.Type> updatedProperties)
        {
            if (!m_syncInfoManager.SyncInfoExists(syncObjectID))
            {
                m_log.DebugFormat("{0}: UpdateQuarkLocation could not find sync info for object {1}. It might be gone.", LogHeader, syncObjectID);
                return false;
            }
            SyncInfoBase sib = m_syncInfoManager.GetSyncInfo(syncObjectID);
            ScenePresence sp = m_regionSyncModule.Scene.GetScenePresence(syncObjectID);
            if (sp != null)
            {
                return UpdateScenePresenceQuarkLocation(sp,ref sib, updatedProperties);
            }

            SceneObjectPart sop = m_regionSyncModule.Scene.GetSceneObjectPart(syncObjectID);
            if (sop != null)
            {
                SceneObjectGroup sogFromSop = m_regionSyncModule.Scene.GetSceneObjectGroup(syncObjectID);
                // Check to see if the crossing prim is a root object. We don't care for non-root prim crossings (for now)
                if (sogFromSop != null && sogFromSop.RootPart.UUID == syncObjectID)
                    return UpdatePrimQuarkLocation(sogFromSop, ref sib, updatedProperties);
                else
                    return false;
            }

            SceneObjectGroup sog = m_regionSyncModule.Scene.GetSceneObjectGroup(syncObjectID);
            if (sog != null)
            {
                return UpdatePrimQuarkLocation(sog,ref sib, updatedProperties);
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
                // Someday see if a better design is possible for spatially large linksets.
                Vector3 spLoc = sp.AbsolutePosition;
                // m_log.WarnFormat("{0}: Absolute Position after updated properties: {1}", LogHeader, spLoc);
                string currentQuarkName = SyncQuark.GetQuarkNameByPosition(spLoc);
                if (sib != null)
                {
                    if (currentQuarkName != sib.CurQuark.QuarkName)
                    {
                        sib.PrevQuark = sib.CurQuark;
                        sib.CurQuark = new SyncQuark(spLoc);

                        // Inform SyncInfoBase to update the previous and current quark synced properties
                        long ts = RegionSyncModule.NowTicks();
                        HashSet<SyncableProperties.Type> quarkTypes = new HashSet<SyncableProperties.Type>();
                        quarkTypes.Add(SyncableProperties.Type.PreviousQuark);
                        quarkTypes.Add(SyncableProperties.Type.CurrentQuark);

                        // Stores the new position in the SyncInfoPrim, so it can be packed and sent to other actors.
                        sib.UpdatePropertiesByLocal(sib.UUID, quarkTypes, ts, m_regionSyncModule.SyncID);
                        ret = true;
                    }
                }
            }
            return ret;
        }

        // Check to see if current quark has changed (based on GroupPosition).
        // If changed, update the quarks in the PrimSyncInfo and return 'true'.
        public bool UpdatePrimQuarkLocation(SceneObjectGroup sog, ref SyncInfoBase sib, HashSet<SyncableProperties.Type> updatedProperties)
        {
            bool ret = false;
            if (updatedProperties.Contains(SyncableProperties.Type.AbsolutePosition) || updatedProperties.Contains(SyncableProperties.Type.Position)
                || updatedProperties.Contains(SyncableProperties.Type.GroupPosition))
            {
                if (sib != null)
                {
                    // Note that all SOPs in a linkset are in the quark of the root SOP (ie, always using GroupPosition).
                    // Someday see if a better design is possible for spacialy large linksets.
                    Vector3 sogPos = sog.RootPart.GroupPosition;
                    string currentQuarkName = SyncQuark.GetQuarkNameByPosition(sogPos);
                    if (currentQuarkName != sib.CurQuark.QuarkName)
                    {
                        sib.PrevQuark = sib.CurQuark;
                        sib.CurQuark = new SyncQuark(sogPos);

                        // Inform SyncInfoBase to update the previous and current quark synced properties
                        long ts = RegionSyncModule.NowTicks();
                        HashSet <SyncableProperties.Type> quarkTypes = new HashSet<SyncableProperties.Type>();
                        quarkTypes.Add(SyncableProperties.Type.PreviousQuark);
                        quarkTypes.Add(SyncableProperties.Type.CurrentQuark);

                        // Stores the new position in the SyncInfoPresence, so it can be packed and sent to other actors.
                        sib.UpdatePropertiesByLocal(sib.UUID, quarkTypes, ts, m_regionSyncModule.SyncID);
                        ret = true;
                    }
                }
            }
            return ret;
        }

        #endregion
        

        #region QuarkCrossing

        // Called when a quark crossing was detected, either for a scene presence, SOP or SOG
        // Returns true if successfully created and fired a crossing message
        public bool QuarkCrossingUpdate(SyncInfoBase syncInfo, HashSet<SyncableProperties.Type> updatedProperties)
        {
            bool ret = false;
            // Add Previous and Current quark to list of updated properties, since quarks have just changed
            updatedProperties.Add(SyncableProperties.Type.PreviousQuark);
            updatedProperties.Add(SyncableProperties.Type.CurrentQuark);
            ScenePresence sp = m_regionSyncModule.Scene.GetScenePresence(syncInfo.UUID);
            if (sp != null)
            {
                ret = QuarkCrossingPresenceUpdate(sp, (SyncInfoPresence)syncInfo, updatedProperties);
            }
            SceneObjectPart sop = m_regionSyncModule.Scene.GetSceneObjectPart(syncInfo.UUID);
            if (!ret && sop != null)
            {
                ret = QuarkCrossingPrimUpdate(sop, (SyncInfoPrim)syncInfo, updatedProperties);
            }
            SceneObjectGroup sog = m_regionSyncModule.Scene.GetSceneObjectGroup(syncInfo.UUID);
            if (!ret && sog != null)
            {
                ret = QuarkCrossingPrimUpdate(sog.RootPart, (SyncInfoPrim)syncInfo, updatedProperties);
            }
            if (!ret)
                m_log.ErrorFormat("{0}: Something should have crossed, but it was not found as a ScenePresence or SOG. UUID: {1}", LogHeader, syncInfo.UUID);
            return ret;
        }

        private bool QuarkCrossingPresenceUpdate(ScenePresence sp, SyncInfoPresence sip, HashSet<SyncableProperties.Type> updatedProperties)
        {
            bool leavingMyQuarks = !(IsInActiveQuark(sip.CurQuark.QuarkName) || IsInPassiveQuark(sip.CurQuark.QuarkName));
            if (leavingMyQuarks)
                LeftQuarks[sp.UUID] = true;

            HashSet<SyncConnector> actorsNeedFull = new HashSet<SyncConnector>();
            HashSet<SyncConnector> actorsNeedUpdate = new HashSet<SyncConnector>();
            RequiresFullObject(sip.PrevQuark, sip.CurQuark, ref actorsNeedFull, ref actorsNeedUpdate);
            SyncMsgPresenceQuarkCrossing syncMsgFull = null;
            SyncMsgPresenceQuarkCrossing syncMsgUpdate = null;

            if (actorsNeedFull.Count > 0)
            {
                syncMsgFull = new SyncMsgPresenceQuarkCrossing(m_regionSyncModule, sp, updatedProperties, true);
                syncMsgFull.ConvertOut(m_regionSyncModule);
                m_regionSyncModule.SendSyncMessageTo(syncMsgFull, actorsNeedFull);
            }
            if (actorsNeedUpdate.Count > 0)
            {
                syncMsgUpdate = new SyncMsgPresenceQuarkCrossing(m_regionSyncModule, sp, updatedProperties, false);
                syncMsgUpdate.ConvertOut(m_regionSyncModule);
                m_regionSyncModule.SendSyncMessageTo(syncMsgUpdate, actorsNeedUpdate);
            }

            // if the presence is not in the quarks I manage, remove it from the scenegraph
            if (leavingMyQuarks)
            {
                m_log.WarnFormat("{0}: User {1} is leaving quark {2} to quark {3}. Deleting him.", LogHeader, sp.Firstname, sip.PrevQuark.QuarkName, sip.CurQuark.QuarkName);
                if (sp != null)
                {
                    m_syncInfoManager.RemoveSyncInfo(sp.UUID);
                    try
                    {
                        // Removing a client triggers OnRemovePresence. I should only remove the client from this actor, not propagate it.
                        m_regionSyncModule.RememberLocallyGeneratedEvent(SyncMsg.MsgType.QuarkPresenceCrossing);
                        m_regionSyncModule.Scene.IncomingCloseAgent(sp.UUID, true);
                        //m_regionSyncModule.Scene.RemoveClient(sp.UUID, false);
                        m_regionSyncModule.ForgetLocallyGeneratedEvent();
                    }
                    catch (Exception e)
                    {
                        m_log.WarnFormat("{0}: No client to remove from here. {1}", LogHeader, e);
                    }
                }
            }
            return true;
        }

        // The sop is a root prim and is changing quarks (curQuark != prevQuark). This method creates and sends a SyncMsgPrimQuarkCrossing to inform
        // other actors of the crossing.
        // PS: The individual child SOPs will never send out a quark crossing message.
        private bool QuarkCrossingPrimUpdate(SceneObjectPart sop, SyncInfoPrim sip, HashSet<SyncableProperties.Type> updatedProperties)
        {
            // If not in my active or passive quarks, remember so we can delete all reference to it from scene and sync info.
            bool leavingMyQuarks = !(IsInActiveQuark(sip.CurQuark.QuarkName) || IsInPassiveQuark(sip.CurQuark.QuarkName));
            
            HashSet<SyncConnector> actorsNeedFull = new HashSet<SyncConnector>();
            HashSet<SyncConnector> actorsNeedUpdate = new HashSet<SyncConnector>();
            // Which of my neighbors need the full prim, and which need only a simple update? If my neighbor is subscribed to both current quark and 
            // previous quark, only an update is needed.
            RequiresFullObject(sip.PrevQuark, sip.CurQuark, ref actorsNeedFull, ref actorsNeedUpdate);
            SyncMsgPrimQuarkCrossing syncMsgFull = null;
            SyncMsgPrimQuarkCrossing syncMsgUpdate = null;

            if (actorsNeedFull.Count > 0)
            {
                syncMsgFull = new SyncMsgPrimQuarkCrossing(m_regionSyncModule, sop, updatedProperties, true);
                syncMsgFull.ConvertOut(m_regionSyncModule);
                m_regionSyncModule.SendSyncMessageTo(syncMsgFull, actorsNeedFull);
            }
            if (actorsNeedUpdate.Count > 0)
            {
                syncMsgUpdate = new SyncMsgPrimQuarkCrossing(m_regionSyncModule, sop, updatedProperties, false);
                syncMsgUpdate.ConvertOut(m_regionSyncModule);
                m_regionSyncModule.SendSyncMessageTo(syncMsgUpdate, actorsNeedUpdate);
            }

            // if the prim is not in the quarks I manage, remove it from the scenegraph
            if (leavingMyQuarks)
            {
                SceneObjectGroup sog = sop.ParentGroup;
                if (sog != null)
                {
                    foreach(SceneObjectPart part in sog.Parts)
                    {
                        try
                        {
                            m_syncInfoManager.RemoveSyncInfo(part.UUID);
                        }
                        catch (KeyNotFoundException)
                        {
                            m_log.WarnFormat("{0}: Object part {1} did not have a SyncInfoprim.", LogHeader, part.UUID);
                        }
                    }
                    m_regionSyncModule.Scene.DeleteSceneObject(sog, false);
                }
            }
            return true;
        }

        // Returns a list of sync connectors of actors that require a full object, and the list that only requires updated properties.
        public void RequiresFullObject(SyncQuark prevQuark, SyncQuark curQuark, ref HashSet<SyncConnector> full, ref HashSet<SyncConnector> update)
        {
            QuarkPublisher prev = null;
            QuarkPublisher cur = null;
            HashSet<SyncConnector> full_iter = new HashSet<SyncConnector>();

            if (!(full.Count == 0 && update.Count == 0))
            {
                full = new HashSet<SyncConnector>();
                update = new HashSet<SyncConnector>();
            }

            if (m_quarkSubscriptions.TryGetValue(prevQuark.QuarkName, out prev))
                full_iter.UnionWith(prev.ActiveSubscribers);
            if (m_quarkSubscriptions.TryGetValue(curQuark.QuarkName, out cur))
                full_iter.UnionWith(cur.ActiveSubscribers);

            full.UnionWith(full_iter);

            foreach (SyncConnector actor in full_iter)
            {
                // If the actor has previous quark, he falls into code case 1 (object leaving quarks) or 3 (object moving between two quarks in the same actor).
                // No need for a full update in this case.
                if (m_actorSubscriptions[actor].AllQuarks.Contains(prevQuark))
                {
                    // Remove this actor from the list of actors that need full objects
                    full.Remove(actor);
                    update.Add(actor);
                }
            }
        }
        #endregion //QuarkCrossing
    }
}
