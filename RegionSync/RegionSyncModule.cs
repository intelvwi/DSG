/*
 * -----------------------------------------------------------------
 * Copyright (c) 2012 Intel Corporation
 * All rights reserved.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are
 * met:
 *
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *
 *     * Redistributions in binary form must reproduce the above
 *       copyright notice, this list of conditions and the following
 *       disclaimer in the documentation and/or other materials provided
 *       with the distribution.
 *
 *     * Neither the name of the Intel Corporation nor the names of its
 *       contributors may be used to endorse or promote products derived
 *       from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
 * "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
 * LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
 * A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE INTEL OR
 * CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL,
 * EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO,
 * PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR
 * PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF
 * LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
 * NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 *
 * EXPORT LAWS: THIS LICENSE ADDS NO RESTRICTIONS TO THE EXPORT LAWS OF
 * YOUR JURISDICTION. It is licensee's responsibility to comply with any
 * export regulations applicable in licensee's jurisdiction. Under
 * CURRENT (May 2000) U.S. export regulations this software is eligible
 * for export from the U.S. and can be downloaded by or otherwise
 * exported or reexported worldwide EXCEPT to U.S. embargoed destinations
 * which include Cuba, Iraq, Libya, North Korea, Iran, Syria, Sudan,
 * Afghanistan and any other country to which the U.S. has embargoed
 * goods and services.
 * -----------------------------------------------------------------
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Client;
using OpenSim.Framework.Monitoring;
using OpenSim.Region.CoreModules.Framework.InterfaceCommander;
using Logging = OpenSim.Region.CoreModules.Framework.Statistics.Logging;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Serialization;
using OpenSim.Region.Physics.Manager;
using OpenSim.Services.Interfaces;
using log4net;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Text;
using System.Collections;

using System.IO;
using System.Xml;
using Mono.Addins;
using OpenMetaverse.StructuredData;

using System.Diagnostics;

[assembly: Addin("RegionSyncModule", "0.1")]
[assembly: AddinDependency("OpenSim", "0.5")]

/////////////////////////////////////////////////////////////////////////////////////////////
//KittyL: created 12/17/2010, to start DSG Symmetric Synch implementation
/////////////////////////////////////////////////////////////////////////////////////////////
namespace DSG.RegionSync
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "RegionSyncModule")]
    public class RegionSyncModule : INonSharedRegionModule, ICommandableModule
    {
        #region INonSharedRegionModule

        // Statistics gathering
        public SyncStatisticCollector StatCollector { get; private set; }

        private object m_stats = new object();
        private int m_statMsgsIn = 0;
        private int m_statMsgsOut = 0;
        private int m_statEventIn = 0;
        private int m_statEventOut = 0;

        public void Initialise(IConfigSource config)
        {
            m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

            m_sysConfig = config.Configs["RegionSyncModule"];
            Active = false;
            if (m_sysConfig == null)
            {
                m_log.InfoFormat("{0}: No RegionSyncModule config section found. Shutting down.", LogHeader);
                return;
            }
            else if (!m_sysConfig.GetBoolean("Enabled", false))
            {
                m_log.InfoFormat("{0}: RegionSyncModule is not enabled. Shutting down.", LogHeader);
                return;
            }

            ActorID = m_sysConfig.GetString("ActorID", "");
            if (ActorID == "")
            {
                m_log.Error("ActorID not defined in [RegionSyncModule] section in config file. Shutting down.");
                return;
            }

            // For now, the sync relay and sync listener are both the same (the hub)
            IsSyncRelay = m_isSyncListenerLocal = m_sysConfig.GetBoolean("IsHub", false);
            //IsSyncRelay = m_sysConfig.GetBoolean("IsSyncRelay", false);
            //m_isSyncListenerLocal = m_sysConfig.GetBoolean("IsSyncListenerLocal", false);

            Active = true;

            LogHeader += ActorID;
            // m_log.WarnFormat("{0}: Initialised for actor {1}", LogHeader, ActorID);

            //The ActorType configuration will be read in and process by an ActorSyncModule, not here.

            // parameters for statistic logging
            StatCollector = new SyncStatisticCollector(m_sysConfig);

            // parameters for detailed synchronization message logging
            if (m_sysConfig.GetBoolean("DetailLogEnabled", false))
            {
                bool flush = m_sysConfig.GetBoolean("FlushWrites", false);
                string dir = m_sysConfig.GetString("DetailLogDirectory", ".");
                string hdr = m_sysConfig.GetString("DetailLogPrefix", "log-%ACTORID%-");
                hdr = hdr.Replace("%ACTORID%", ActorID);
                int mins = m_sysConfig.GetInt("DetailLogMaxFileTimeMin", 5);
                m_detailedLog = new Logging.LogWriter(dir, hdr, mins, flush);
                m_detailedLog.ErrorLogger = m_log;  // pass in logger for debugging (can be removed later)
                m_log.InfoFormat("{0}: DetailLog enabled. Dir={1}, pref={2}, maxTime={3}", LogHeader, dir, hdr, mins);
            }
            else
            {
                // create an empty, disabled logger so everyone doesn't need to check for null
                m_detailedLog = new Logging.LogWriter();
                m_log.InfoFormat("{0}: DetailLog disabled.", LogHeader);
            }
            // Whether to include the names of the changed properties in the log (slow but useful)
            m_detailedPropertyValues = m_sysConfig.GetBoolean("DetailPropertyValues", false);


            //configuration for logging updateLoop timing and delays, we'll use the same parameters as DetailLog
            if (m_sysConfig.GetBoolean("UpdateLoopLogEnabled", false))
            {
                string dir = m_sysConfig.GetString("DetailLogDirectory", ".");
                string hdr = m_sysConfig.GetString("DetailLogPrefix", "log-%ACTORID%-");
                hdr = hdr.Replace("%ACTORID%", ActorID);
                hdr = hdr + "-UpdateLoop";
                int mins = m_sysConfig.GetInt("DetailLogMaxFileTimeMin", 5);
                bool flush = m_sysConfig.GetBoolean("FlushWrites", false);
                m_updateLoopLog = new Logging.LogWriter(dir, hdr, mins, false);
                m_updateLoopLog.ErrorLogger = m_log;  // pass in logger for debugging (can be removed later)
                m_log.InfoFormat("{0}: DetailLog enabled. Dir={1}, pref={2}, maxTime={3}", LogHeader, dir, hdr, mins);
            }
            else
            {
                m_updateLoopLog = new Logging.LogWriter();
                m_log.InfoFormat("{0}: UpdateLoopLog disabled.", LogHeader);
            }

            //initialize SyncInfoManager
            SyncInfoManager.DebugLog = m_log;
            SyncInfoPrim.DebugLog = m_log;
            SyncedProperty.DebugLog = m_log;
            int syncInfoAgeOutSeconds = m_sysConfig.GetInt("PrimSyncInfoAgeOutSeconds", 300); //unit of seconds
            TimeSpan tSpan = new TimeSpan(0, 0, syncInfoAgeOutSeconds);
            m_SyncInfoManager = new SyncInfoManager(this, tSpan.Ticks);

            //this is temp solution for reducing collision events for country fair demo
            m_reportCollisions = m_sysConfig.GetString("ReportCollisions", "All");
        }

        //Called after Initialise()
        public void AddRegion(Scene scene)
        {
            // m_log.WarnFormat("{0} AddRegion: region = {1}", LogHeader, scene.RegionInfo.RegionName);

            if (!Active)
                return;

            //connect with scene
            Scene = scene;

            // Setup the command line interface
            Scene.EventManager.OnPluginConsole += EventManager_OnPluginConsole;
            Scene.EventManager.OnRegionHeartbeatEnd += SyncOutUpdates;
            InstallInterfaces();

            // Add region name to various logging headers so we know where the log messages come from
            LogHeader += "/" + scene.RegionInfo.RegionName;
            m_detailedLog.LogFileHeader = m_detailedLog.LogFileHeader + scene.RegionInfo.RegionName + "-";

            if (StatCollector != null)
            {
                StatCollector.SpecifyRegion(Scene.RegionInfo.RegionName);
            }

            SyncID = GetSyncID();
        }

        //public IDSGActorSyncModule ActorModule { get; private set; }
        //public DSGActorType ActorType { get; private set; }

        //Called after AddRegion() has been called for all region modules of the scene
        public void RegionLoaded(Scene scene)
        {
            if (!Active)
                return;

            // Terrain sync info
            TerrainSyncInfo = new TerrainSyncInfo(Scene, ActorID);

            IEstateModule estate = Scene.RequestModuleInterface<IEstateModule>();
            if (estate != null)
                estate.OnRegionInfoChange += new ChangeDelegate(estate_OnRegionInfoChange);

            //ActorModule = Scene.RequestModuleInterface<IDSGActorSyncModule>();
            //if (ActorModule == null)
            //    throw (new NullReferenceException("Could not get IDSGActorSyncModule interface from Scene for region " + Scene.RegionInfo.RegionName));
            //ActorType = ActorModule.ActorType;
            
            Scene.EventManager.OnObjectAddedToScene             += OnObjectAddedToScene;
            Scene.EventManager.OnSceneObjectLoaded              += OnObjectAddedToScene;
            Scene.EventManager.OnObjectBeingRemovedFromScene    += OnObjectBeingRemovedFromScene;
            Scene.EventManager.OnSceneObjectPartUpdated         += OnSceneObjectPartUpdated;

            Scene.EventManager.OnNewPresence                    += OnNewPresence;
            Scene.EventManager.OnRemovePresence                 += OnRemovePresence;
            Scene.EventManager.OnScenePresenceUpdated           += OnScenePresenceUpdated;
            Scene.EventManager.OnAvatarAppearanceChange         += OnAvatarAppearanceChange;

            Scene.EventManager.OnRegionStarted                  += OnRegionStarted;
            Scene.EventManager.OnTerrainTainted                 += OnTerrainTainted;

            Scene.EventManager.OnNewScript                      += OnLocalNewScript;
            Scene.EventManager.OnUpdateScript                   += OnLocalUpdateScript;
            Scene.EventManager.OnScriptReset                    += OnLocalScriptReset;
            Scene.EventManager.OnChatBroadcast                  += OnLocalChatBroadcast;
            Scene.EventManager.OnChatFromClient                 += OnLocalChatFromClient;
            Scene.EventManager.OnChatFromWorld                  += OnLocalChatFromWorld;
            Scene.EventManager.OnAttach                         += OnLocalAttach;
            Scene.EventManager.OnObjectGrab                     += OnLocalGrabObject;
            Scene.EventManager.OnObjectGrabbing                 += OnLocalObjectGrabbing;
            Scene.EventManager.OnObjectDeGrab                   += OnLocalDeGrabObject;
            Scene.EventManager.OnScriptColliderStart            += OnLocalScriptCollidingStart;
            Scene.EventManager.OnScriptColliding                += OnLocalScriptColliding;
            Scene.EventManager.OnScriptCollidingEnd             += OnLocalScriptCollidingEnd;
            Scene.EventManager.OnScriptLandColliderStart        += OnLocalScriptLandCollidingStart;
            Scene.EventManager.OnScriptLandColliding            += OnLocalScriptLandColliding;
            Scene.EventManager.OnScriptLandColliderEnd          += OnLocalScriptLandCollidingEnd;
        }

        // This is called just before the first heartbeat of the region. Everything should be loaded and ready to simulate.
        private void OnRegionStarted(Scene scene)
        {
            if (m_isSyncListenerLocal)
            {
                m_log.Warn(LogHeader + " Starting Sync - Sync listener is local");
                if (m_localSyncListener != null && m_localSyncListener.IsListening)
                {
                    m_log.Warn(LogHeader + " RegionSyncListener is local, already started");
                }
                else
                {
                    StartLocalSyncListener();
                }
            }
            else
            {
                m_log.Warn(LogHeader + " Starting Sync - Sync listener is remote");
                if (m_remoteSyncListeners == null)
                {
                    GetRemoteSyncListenerInfo();
                }
                if (StartSyncConnections())
                {
                    DoInitialSync();
                }
            }
        }

        public void RemoveRegion(Scene scene)
        {
            if (!Active)
                return;

            IEstateModule estate = Scene.RequestModuleInterface<IEstateModule>();
            if (estate != null)
                estate.OnRegionInfoChange -= new ChangeDelegate(estate_OnRegionInfoChange);

            //ActorModule = Scene.RequestModuleInterface<IDSGActorSyncModule>();
            //if (ActorModule == null)
            //    throw (new NullReferenceException("Could not get IDSGActorSyncModule interface from Scene for region " + Scene.RegionInfo.RegionName));
            //ActorType = ActorModule.ActorType;
            
            Scene.EventManager.OnObjectAddedToScene             -= OnObjectAddedToScene;
            Scene.EventManager.OnSceneObjectLoaded              -= OnObjectAddedToScene;
            Scene.EventManager.OnObjectBeingRemovedFromScene    -= OnObjectBeingRemovedFromScene;
            Scene.EventManager.OnSceneObjectPartUpdated         -= OnSceneObjectPartUpdated;

            Scene.EventManager.OnNewPresence                    -= OnNewPresence;
            Scene.EventManager.OnRemovePresence                 -= OnRemovePresence;
            Scene.EventManager.OnScenePresenceUpdated           -= OnScenePresenceUpdated;
            Scene.EventManager.OnAvatarAppearanceChange         -= OnScenePresenceUpdated;

            Scene.EventManager.OnRegionStarted                  -= OnRegionStarted;
            Scene.EventManager.OnTerrainTainted                 -= OnTerrainTainted;

            Scene.EventManager.OnNewScript                      -= OnLocalNewScript;
            Scene.EventManager.OnUpdateScript                   -= OnLocalUpdateScript;
            Scene.EventManager.OnScriptReset                    -= OnLocalScriptReset;
            Scene.EventManager.OnChatBroadcast                  -= OnLocalChatBroadcast;
            Scene.EventManager.OnChatFromClient                 -= OnLocalChatFromClient;
            Scene.EventManager.OnChatFromWorld                  -= OnLocalChatFromWorld;
            Scene.EventManager.OnAttach                         -= OnLocalAttach;
            Scene.EventManager.OnObjectGrab                     -= OnLocalGrabObject;
            Scene.EventManager.OnObjectGrabbing                 -= OnLocalObjectGrabbing;
            Scene.EventManager.OnObjectDeGrab                   -= OnLocalDeGrabObject;
            Scene.EventManager.OnScriptColliderStart            -= OnLocalScriptCollidingStart;
            Scene.EventManager.OnScriptColliding                -= OnLocalScriptColliding;
            Scene.EventManager.OnScriptCollidingEnd             -= OnLocalScriptCollidingEnd;
            Scene.EventManager.OnScriptLandColliderStart        -= OnLocalScriptLandCollidingStart;
            Scene.EventManager.OnScriptLandColliding            -= OnLocalScriptLandColliding;
            Scene.EventManager.OnScriptLandColliderEnd          -= OnLocalScriptLandCollidingEnd;
        }

        public void Close()
        {
            if (StatCollector != null)
            {
                StatCollector.Close();
                StatCollector.Dispose();
                StatCollector = null;
            }
            Scene = null;
            if (m_detailedLog != null)
            {
                m_detailedLog.Close();
                m_detailedLog = null;
            }

            if (m_updateLoopLog != null)
            {
                m_updateLoopLog.Close();
                m_updateLoopLog = null;
            }
        }

        public string Name
        {
            get { return "RegionSyncModule"; }
        }

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        #endregion //INonSharedRegionModule

        private class ReceivedSEQ
        {
            //We want to use SortedSet, somehow it is not recoganized by compiler.  
            /// <summary>
            /// The seqs that are bigger than ReceivedContinousSEQMax, but
            /// discreate in the sense that there is cap among them and 
            /// ReceivedContinousSEQMax. In other words, if this list is not empty,
            /// it indicates that some events with a seq number that is bigger 
            /// than ReceivedContinousSEQMax still have not been received.
            /// </summary>
            HashSet<ulong> DiscreteReceivedSEQs = new HashSet<ulong>();
            //SortedDictionary<ulong, Nullable>
            ///the seq number of the event we have received, such that any
            ///seq smaller than this number have all been received.
            ulong ReceivedContinousSEQMax = 0;
            bool hasReceivedSEQ = false;

            public bool IsSEQReceived(ulong seq)
            {
                //since seq # is ulong type, ReceivedContinousSEQMax == 0 alone is
                //not enough to tell if we have received a message with seq 0. so
                //we have hasReceivedSEQ to help us.
                if (seq == 0 && !hasReceivedSEQ && seq == ReceivedContinousSEQMax)
                    return false;

                if (seq <= ReceivedContinousSEQMax)
                    return true;
                if (DiscreteReceivedSEQs.Contains(seq))
                    return true;
                return false;
            }

            public void RecordReceivedSEQ(ulong seq)
            {
                hasReceivedSEQ = true;

                if (seq == 0 && ReceivedContinousSEQMax == 0)
                    return;

                DiscreteReceivedSEQs.Add(seq);
                bool seqFound = true;
                //in worse case, the following loop goes through all elements in
                //DiscreteReceivedSEQs and stops.
                while (seqFound)
                {
                    //See if the number right above ReceivedContinousSEQMax 
                    //is among the received.
                    if (DiscreteReceivedSEQs.Contains(ReceivedContinousSEQMax + 1))
                    {
                        DiscreteReceivedSEQs.Remove(ReceivedContinousSEQMax + 1);
                        ReceivedContinousSEQMax++;
                    }
                    else
                        seqFound = false;
                }
            }
        }

        public class EventsReceived
        {
            Dictionary<string, ReceivedSEQ> ReceivedSEQPerSyncID = new Dictionary<string, ReceivedSEQ>();

            public bool IsSEQReceived(string syncID, ulong seq)
            {
                bool ret = false;
                ReceivedSEQ recVal;
                if (ReceivedSEQPerSyncID.TryGetValue(syncID, out recVal))
                {
                    ret = recVal.IsSEQReceived(seq);
                }
                return ret;
            }

            public void RecordEventReceived(string syncID, ulong seq)
            {
                if (!ReceivedSEQPerSyncID.ContainsKey(syncID))
                {
                    ReceivedSEQ receivedSeq = new ReceivedSEQ();
                    receivedSeq.RecordReceivedSEQ(seq);
                    ReceivedSEQPerSyncID.Add(syncID, receivedSeq);
                }
                else
                {
                    ReceivedSEQPerSyncID[syncID].RecordReceivedSEQ(seq);
                }
            }
        }

        //ActorID might not be in use anymore. Rather, SyncID should be used. 
        //(Synchronization is sync node centric, not actor centric.)
        public string ActorID { get; set; }
        public string SyncID { get; private set; }
        private bool Active { get; set; }
        public bool IsSyncRelay { get; private set; }
        private bool TerrainIsTainted { get; set; }

        private class SyncMessageRecord
        {
            public SyncMsg SyncMessage;
            public long ReceivedTime;
        }
        private List<SyncMessageRecord> m_savedSyncMessage = new List<SyncMessageRecord>();

        // Taint the terrain in this module and return. Usually where there is one terrain update, there are many to follow.
        private void OnTerrainTainted()
        {
            if (!IsSyncingWithOtherSyncNodes())
                return;

            TerrainIsTainted = true;
        }

        // Called each terrain tick to consolidate syncing changes to other actors
        private void CheckTerrainTainted()
        {
            
            if (!TerrainIsTainted)
                return;

            TerrainIsTainted = false;

            string terrain = Scene.Heightmap.SaveToXmlString();
            if (TerrainSyncInfo.LastUpdateValue.Equals(terrain))
                return;

            TerrainSyncInfo.LastUpdateValue = terrain;
            TerrainSyncInfo.LastUpdateActorID = GetSyncID();
            TerrainSyncInfo.LastUpdateTimeStamp = DateTime.UtcNow.Ticks;

            SyncMsgTerrain msg = new SyncMsgTerrain(this, TerrainSyncInfo);

            SendTerrainUpdateToRelevantSyncConnectors(msg, TerrainSyncInfo.LastUpdateActorID);
        }

        private void OnNewPresence(ScenePresence sp)
        {
            UUID uuid = sp.UUID;
            // m_log.WarnFormat("{0} OnNewPresence uuid={1}, name={2}", LogHeader, uuid, sp.Name);

            // If the SP is already in the sync cache, then don't add it and don't sync it.
            if (m_SyncInfoManager.SyncInfoExists(uuid))
            {
                m_log.DebugFormat("{0} OnNewPresence: Sync info already exists for uuid {1}. Done.", LogHeader, uuid);
                return;
            }

            // Add SP to SyncInfoManager
            m_SyncInfoManager.InsertSyncInfo(uuid, DateTime.UtcNow.Ticks, SyncID);

            if (IsSyncingWithOtherSyncNodes())
            {
                SyncMsgNewPresence msg = new SyncMsgNewPresence(this, sp);
                m_log.DebugFormat("{0}: Send NewPresence message for {1} ({2})", LogHeader, sp.Name, sp.UUID);
                SendSpecialUpdateToRelevantSyncConnectors(ActorID, msg);
            }
        }

        private void OnRemovePresence(UUID uuid)
        {
            // m_log.WarnFormat("{0} OnRemovePresence called for {1}", LogHeader, uuid);
            // First, remove from SyncInfoManager's record.
            m_SyncInfoManager.RemoveSyncInfo(uuid);

            SyncMsgRemovedPresence msg = new SyncMsgRemovedPresence(this, uuid);
            SendSpecialUpdateToRelevantSyncConnectors(ActorID, msg);
        }

        /// <summary>
        /// Called when new object is created in local SceneGraph. 
        /// (Adding a new object by receiving a NewObject sync message also triggers this.)
        /// </summary>
        /// <param name="sog"></param>
        private void OnObjectAddedToScene(SceneObjectGroup sog)
        {
            UUID uuid = sog.UUID;
            // m_log.DebugFormat("{0} OnObjectAddedToScene uuid={1}", LogHeader, uuid);

            // If the SOG is already in the sync cache, then don't add it and don't sync it.
            // This also happens if a NewObject message was received and the object was
            //   created by decoding (creates both SOG and syncinfo). For this, it is known
            //   that everything has been initialized.
            // If the sync info is not here, the object was created locally.
            if (m_SyncInfoManager.SyncInfoExists(uuid))
            {
                //m_log.WarnFormat("{0} OnObjectAddedToScene: Sync info already exists for uuid {1}. Done.", LogHeader, uuid);
                return;
            }

            // m_log.WarnFormat("{0} OnObjectAddedToScene: Sync info not found in manager. Adding for uuid {1}.", LogHeader, uuid);

            // Add each SOP in SOG to SyncInfoManager
            foreach (SceneObjectPart part in sog.Parts)
            {
                m_SyncInfoManager.InsertSyncInfo(part.UUID, DateTime.UtcNow.Ticks, SyncID);
            }

            if (IsSyncingWithOtherSyncNodes())
            {
                // if we're syncing with other nodes, send out the message
                SyncMsgNewObject msg = new SyncMsgNewObject(this, sog);
                // m_log.DebugFormat("{0}: Send NewObject message for {1} ({2})", LogHeader, sog.Name, sog.UUID);
                SendSpecialUpdateToRelevantSyncConnectors(ActorID, msg);
            }
        }

        private void OnObjectBeingRemovedFromScene(SceneObjectGroup sog)
        {
            //First, remove from SyncInfoManager's record.
            foreach (SceneObjectPart part in sog.Parts)
            {
                m_SyncInfoManager.RemoveSyncInfo(part.UUID);
            }

            if (!IsSyncingWithOtherSyncNodes())
            {
                //no SyncConnector connected. Do nothing.
                return;
            }

            SyncMsgRemovedObject msg = new SyncMsgRemovedObject(this, sog.UUID, ActorID, false /*softDelete*/);
            msg.ConvertOut(this);
            //m_log.DebugFormat("{0}: Send DeleteObject out for {1},{2}", Scene.RegionInfo.RegionName, sog.Name, sog.UUID);
            SendSpecialUpdateToRelevantSyncConnectors(ActorID, msg);
        }

        private void SyncLinkObject(SceneObjectGroup linkedGroup, SceneObjectPart root, List<SceneObjectPart> children)
        {
            if (children.Count == 0) return;

            //the group is just linked, each part has quite some properties changed 
            //(OffsetPosition, etc). Need to sync the property values in SyncInfoManager
            //first
            foreach (SceneObjectPart part in linkedGroup.Parts)
            {
                m_SyncInfoManager.UpdateSyncInfoByLocal(part.UUID, part.PhysActor == null ? SyncableProperties.NonPhysActorProperties : SyncableProperties.FullUpdateProperties);
            }

            if (IsSyncingWithOtherSyncNodes())
            {
                List<UUID> childrenIDs = new List<UUID>();
                foreach (SceneObjectPart sop in children)
                {
                    childrenIDs.Add(sop.UUID);
                }
                SyncMsgLinkObject msg = new SyncMsgLinkObject(this, linkedGroup, root.UUID, childrenIDs, ActorID);
                SendSpecialUpdateToRelevantSyncConnectors(ActorID, msg);
            }
        }

        private void SyncDeLinkObject(List<SceneObjectPart> prims, List<SceneObjectGroup> beforeDelinkGroups,
                                        List<SceneObjectGroup> afterDelinkGroups)
        {
            if (prims.Count == 0 || beforeDelinkGroups.Count == 0) return;

            // The prims are just delinked, each part has quite some properties changed 
            // (OffsetPosition, etc). Need to sync the property values in SyncInfoManager first.
            foreach (SceneObjectPart part in prims)
            {
                m_SyncInfoManager.UpdateSyncInfoByLocal(part.UUID, part.PhysActor == null ? SyncableProperties.NonPhysActorProperties : SyncableProperties.FullUpdateProperties);
            }

            if (IsSyncingWithOtherSyncNodes())
            {
                List<UUID> delinkPrimIDs = new List<UUID>();
                foreach (SceneObjectPart sop in prims)
                    delinkPrimIDs.Add(sop.UUID);
                List<UUID> beforeDelinkGroupIDs = new List<UUID>();
                foreach (SceneObjectGroup sog in beforeDelinkGroups)
                    beforeDelinkGroupIDs.Add(sog.UUID);

                // TODO: where does 'groupSyncInfo' come from?
                SyncMsgDelinkObject msg = new SyncMsgDelinkObject(this, delinkPrimIDs, beforeDelinkGroupIDs, afterDelinkGroups);

                SendDelinkObjectToRelevantSyncConnectors(ActorID, beforeDelinkGroups, msg);
            }
        }

        public void Debug(String debugMsg)
        {
            m_log.DebugFormat("{0}", debugMsg);
        }

        #region ICommandableModule Members
        private readonly Commander m_commander = new Commander("ssync");
        public ICommander CommandInterface
        {
            get { return m_commander; }
        }
        #endregion

        #region Console Command Interface
        private void InstallInterfaces()
        {
            //for test and debugging purpose
            Command cmdSyncDebug = new Command("debug", CommandIntentions.COMMAND_HAZARDOUS, SyncDebug, "Trigger some debugging functions");

            //for sync state comparison, 
            Command cmdSyncStateDetailReport = new Command("state_detail", CommandIntentions.COMMAND_HAZARDOUS, SyncStateDetailReport, "Trigger synchronization state comparision functions");
            //for sync state comparison, 
            Command cmdSyncStateReport = new Command("state", CommandIntentions.COMMAND_HAZARDOUS, SyncStateReport, "Trigger synchronization state comparision functions");

            // For details property dump of a UUI
            Command cmdSyncDumpUUID = new Command("uuid", CommandIntentions.COMMAND_HAZARDOUS, SyncDumpUUID, "Dump cached and scene property values for a UUID");
            cmdSyncDumpUUID.AddArgument("uuid", "The uuid to print values for", "UUID");
            cmdSyncDumpUUID.AddArgument("full", "Print all values, not just differences", "String");

            m_commander.RegisterCommand("debug", cmdSyncDebug);
            m_commander.RegisterCommand("state_detail", cmdSyncStateDetailReport);
            m_commander.RegisterCommand("state", cmdSyncStateReport);
            m_commander.RegisterCommand("uuid", cmdSyncDumpUUID);

            lock (Scene)
            {
                // Add this to our scene so scripts can call these functions
                Scene.RegisterModuleCommander(m_commander);
            }
        }

        /// <summary>
        /// Processes commandline input. Do not call directly.
        /// </summary>
        /// <param name="args">Commandline arguments</param>
        private void EventManager_OnPluginConsole(string[] args)
        {
            if (args[0] == "ssync")
            {
                if (args.Length == 1)
                {
                    m_commander.ProcessConsoleCommand("help", new string[0]);
                    return;
                }

                string[] tmpArgs = new string[args.Length - 2];
                int i;
                for (i = 2; i < args.Length; i++)
                    tmpArgs[i - 2] = args[i];

                m_commander.ProcessConsoleCommand(args[1], tmpArgs);
            }
        }

        #endregion Console Command Interface

        ///////////////////////////////////////////////////////////////////////
        // Memeber variables
        ///////////////////////////////////////////////////////////////////////

        private static int PortUnknown = -1;
        private static string IPAddrUnknown = String.Empty;

        private static ILog m_log;
        //private bool m_active = true;

        // used in DetailedLogging rather than generating the zero UUID string for many log calls
        private string m_zeroUUID = "00000000-0000-0000-0000-000000000000";

        private Logging.LogWriter m_detailedLog;
        private Boolean m_detailedPropertyValues = false;

        //logging of the timing and delays in the update loop
        private Logging.LogWriter m_updateLoopLog;
       

        private bool m_isSyncListenerLocal = false;
        //private RegionSyncListenerInfo m_localSyncListenerInfo 

        private HashSet<RegionSyncListenerInfo> m_remoteSyncListeners;

        private int m_syncConnectorNum = 0;

        public Scene Scene { get; set; }

        public TerrainSyncInfo TerrainSyncInfo { get; set; }

        private IConfig m_sysConfig = null;
        private static string LogHeader = "[REGION SYNC MODULE]";

        //The list of SyncConnectors. ScenePersistence could have multiple SyncConnectors, each connecting to a differerent actor.
        //An actor could have several SyncConnectors as well, each connecting to a ScenePersistence that hosts a portion of the objects/avatars
        //the actor operates on.
        private HashSet<SyncConnector> m_syncConnectors= new HashSet<SyncConnector>();
        private object m_syncConnectorsLock = new object();

        //seq number for scene events that are sent out to other actors
        private ulong m_eventSeq = 0;

        //Timers for periodically status report has not been implemented yet.
        private System.Timers.Timer m_statsTimer = new System.Timers.Timer(1000);

        private RegionSyncListener m_localSyncListener = null;
        private bool m_synced = false;

        ///////////////////////////////////////////////////////////////////////
        // Memeber variables for per-property timestamp
        ///////////////////////////////////////////////////////////////////////

        private Object m_propertyUpdateLock = new Object();
        private Dictionary<UUID, HashSet<SyncableProperties.Type>> m_propertyUpdates = new Dictionary<UUID, HashSet<SyncableProperties.Type>>();
        private int m_sendingPropertyUpdates = 0;

        string m_reportCollisions = "All";

        private string GetSyncID()
        {
            /*
            if (Scene != null)
            {
                return Scene.RegionInfo.RegionID.ToString();
            }
            else
            {
                return String.Empty;
            }
             * */
            return ActorID;
        }

        private void StatsTimerElapsed(object source, System.Timers.ElapsedEventArgs e)
        {
            //TO BE IMPLEMENTED
            m_log.ErrorFormat("{0}: StatsTimerElapsed -- NOT yet implemented.", LogHeader);
        }

        private void SendTerrainUpdateToRelevantSyncConnectors(SyncMsg syncMsg, string lastUpdateActorID)
        {
            List<SyncConnector> syncConnectors = GetSyncConnectorsForSceneEvents(lastUpdateActorID, syncMsg, null);

            foreach (SyncConnector connector in syncConnectors)
            {
                //m_log.WarnFormat("{0}: Send terrain update to {1}", LogHeader, connector.otherSideActorID);
                connector.ImmediateOutgoingMsg(syncMsg);
            }
        }

        /* UNUSED?
        //ScenePresence updates are sent by enqueuing into each connector's outQueue.
        private void SendAvatarUpdateToRelevantSyncConnectors(ScenePresence sp, SyncMsg syncMsg)
        {
            HashSet<SyncConnector> syncConnectors = GetSyncConnectorsForUpdates();

            foreach (SyncConnector connector in syncConnectors)
                connector.EnqueueOutgoingUpdate(sp.UUID, syncMsg);
        }
         */


        public void SendDelinkObjectToRelevantSyncConnectors(string senderActorID, List<SceneObjectGroup> beforeDelinkGroups, SyncMsg syncMsg)
        {
            HashSet<int> syncConnectorsSent = new HashSet<int>();

            foreach (SceneObjectGroup sog in beforeDelinkGroups)
            {
                HashSet<SyncConnector> syncConnectors = GetSyncConnectorsForUpdates();
                foreach (SyncConnector connector in syncConnectors)
                {
                    if (!syncConnectorsSent.Contains(connector.ConnectorNum) && !connector.otherSideActorID.Equals(senderActorID))
                    {
                        m_log.DebugFormat("{0}: send DeLinkObject to {1}", LogHeader, connector.description);
                        // connector.EnqueueOutgoingUpdate(sog.UUID, syncMsg);
                        connector.ImmediateOutgoingMsg(syncMsg);
                        syncConnectorsSent.Add(connector.ConnectorNum);
                    }
                }
            }
        }

        /// <summary>
        /// Send some special updates to other sync nodes, including: 
        /// NewObject, RemoveObject, LinkObject, NewPresence. The sync messages are sent out right
        /// away, without being enqueued as normal update messages.
        /// </summary>
        /// <param name="sog"></param>
        /// <param name="syncMsg"></param>
        public void SendSpecialUpdateToRelevantSyncConnectors(string init_actorID, SyncMsg syncMsg)
        {
            HashSet<SyncConnector> syncConnectors = GetSyncConnectorsForUpdates();

            foreach (SyncConnector connector in syncConnectors)
            {
                if (!connector.otherSideActorID.Equals(init_actorID))
                {
                    // DetailedUpdateWrite(logReason, sendingUUID, 0, m_zeroUUID, connector.otherSideActorID, syncMsg.DataLength);
                    connector.ImmediateOutgoingMsg(syncMsg);
                }
            }
        }

        //Events are send out right away, without being put into the connector's outQueue first. 
        //May need a better method for managing the outgoing messages (i.e. prioritizing object updates and events)
        public void SendSceneEventToRelevantSyncConnectors(string init_actorID, SyncMsg rsm, SceneObjectGroup sog)
        {
            // Convert the message from data fields to a block of data to send.
            rsm.ConvertOut(this);

            //TODO: need to pick connectors based on sog position (quark it resides in)
            List<SyncConnector> syncConnectors = GetSyncConnectorsForSceneEvents(init_actorID, rsm, sog);
            // m_log.DebugFormat("{0}: SendSyncEventToRelevantSyncConnectors. numConnectors={1}", LogHeader, syncConnectors.Count);

            foreach (SyncConnector connector in syncConnectors)
            {
                /*
                //special fix for R@I demo, need better optimization later
                if ((rsm.Type == SymmetricSyncMessage.MsgType.PhysicsCollision || rsm.Type == SymmetricSyncMessage.MsgType.ScriptCollidingStart
                    || rsm.Type == SymmetricSyncMessage.MsgType.ScriptColliding || rsm.Type == SymmetricSyncMessage.MsgType.ScriptCollidingEnd
                    || rsm.Type == SymmetricSyncMessage.MsgType.ScriptLandCollidingStart
                    || rsm.Type == SymmetricSyncMessage.MsgType.ScriptLandColliding || rsm.Type == SymmetricSyncMessage.MsgType.ScriptLandCollidingEnd)
                    && IsSyncRelay)
                {
                    //for persistence actor, only forward collision events to script engines
                    if (connector.OtherSideActorType == ScriptEngineSyncModule.ActorTypeString)
                    {
                        lock (m_stats) m_statEventOut++;
                        connector.Send(rsm);
                    }
                }
                else
                 * */
                {
                    lock (m_stats) m_statEventOut++;
                    DetailedUpdateWrite("SndEventtt", sog == null ? m_zeroUUID : sog.UUID.ToString(), 0, rsm.MType.ToString(), connector.otherSideActorID, rsm.DataLength);
                    connector.ImmediateOutgoingMsg(rsm);
                }
            }
        }

        /// <summary>
        /// Get the set of SyncConnectors to send updates of the given object. 
        /// </summary>
        /// 
        /// <returns></returns>
        private HashSet<SyncConnector> GetSyncConnectorsForUpdates()
        {
            HashSet<SyncConnector> syncConnectors = new HashSet<SyncConnector>();
            if (IsSyncRelay)
            {
                //This is a relay node in the synchronization overlay, forward it to all connectors. 
                //Note LastUpdateTimeStamp and LastUpdateActorID is one per SceneObjectPart, not one per SceneObjectGroup, 
                //hence an actor sending in an update on one SceneObjectPart of a SceneObjectGroup may need to know updates
                //in other parts as well, so we are sending to all connectors.
                ForEachSyncConnector(delegate(SyncConnector connector)
                {
                    syncConnectors.Add(connector);
                });
            }
            else
            {
                //This is a end node in the synchronization overlay (e.g. a non ScenePersistence actor). Get the right set of synconnectors.
                //This may go more complex when an actor connects to several ScenePersistence actors.
                ForEachSyncConnector(delegate(SyncConnector connector)
                {
                    syncConnectors.Add(connector);
                });
            }

            return syncConnectors;
        }

        /// <summary>
        /// Get the set of SyncConnectors to send certain scene events. 
        /// </summary>
        /// <param name="sog"></param>
        /// <returns></returns>
        private List<SyncConnector> GetSyncConnectorsForSceneEvents(string init_actorID, SyncMsg rsm, SceneObjectGroup sog)
        {
            List<SyncConnector> syncConnectors = new List<SyncConnector>();
            if (IsSyncRelay)
            {
                //This is a relay node in the synchronization overlay, forward it to all connectors, except the one that sends in the event
                ForEachSyncConnector(delegate(SyncConnector connector)
                {
                    if (connector.otherSideActorID != init_actorID)
                    {
                        syncConnectors.Add(connector);
                    }
                });
            }
            else
            {
                //This is a end node in the synchronization overlay (e.g. a non ScenePersistence actor). Get the right set of synconnectors.
                //For now, there is only one syncconnector that connects to ScenePersistence, due to the star topology.
                //This may go more complex when an actor connects to several ScenePersistence actors.
                ForEachSyncConnector(delegate(SyncConnector connector)
                {
                    syncConnectors.Add(connector);
                });
            }

            return syncConnectors;
        }

        private void StartLocalSyncListener()
        {
            RegionSyncListenerInfo localSyncListenerInfo = GetLocalSyncListenerInfo();

            if (localSyncListenerInfo!=null)
            {
                m_log.WarnFormat("{0}: Starting SyncListener", LogHeader);
                m_localSyncListener = new RegionSyncListener(localSyncListenerInfo, this);
                m_localSyncListener.Start();
            }
            
            //STATS TIMER: TO BE IMPLEMENTED
            //m_statsTimer.Elapsed += new System.Timers.ElapsedEventHandler(StatsTimerElapsed);
            //m_statsTimer.Start();
        }

        //Get the information for local IP:Port for listening incoming connection requests.
        //For now, we use configuration to access the information. Might be replaced by some Grid Service later on.
        private RegionSyncListenerInfo GetLocalSyncListenerInfo()
        {
            //string addr = m_sysConfig.GetString(Scene.RegionInfo.RegionName+"_SyncListenerIPAddress", IPAddrUnknown);
            //int port = m_sysConfig.GetInt(Scene.RegionInfo.RegionName+"_SyncListenerPort", PortUnknown);

            string addr;
            int port;
            try
            {
                addr = Scene.RegionInfo.GetOtherSetting("SyncServerAddress");
                port = Int32.Parse(Scene.RegionInfo.GetOtherSetting("SyncServerPort"));
            }
            catch (Exception e)
            {
                m_log.Warn(LogHeader + " Could not read SyncServerAddress or SyncServerPort from region info. Using defaults.");
                m_log.Warn(LogHeader + Scene.RegionInfo.GetOtherSetting("SyncServerAddress"));
                m_log.Warn(LogHeader + Scene.RegionInfo.GetOtherSetting("SyncServerPort"));
                addr = "127.0.0.1";
                port = 13000;
            }

            m_log.Warn(LogHeader + ": listener addr: " + addr + ", port: " + port);

            if (!addr.Equals(IPAddrUnknown) && port != PortUnknown)
            {
                RegionSyncListenerInfo info = new RegionSyncListenerInfo(addr, port);

                // remove any cruft from previous runs
                //Scene.GridService.CleanUpEndpoint(Scene.RegionInfo.RegionID.ToString());
                // Register the endpoint and quark and persistence actor for this simulator instance
                GridEndpointInfo gei = new GridEndpointInfo();
                gei.syncServerID = Scene.RegionInfo.RegionID.ToString();
                gei.address = addr;
                gei.port = port;
                /*
                if (!Scene.GridService.RegisterEndpoint(gei))
                {
                    m_log.ErrorFormat("{0}: Failure registering endpoint", LogHeader);
                }
                if (!Scene.GridService.RegisterQuark(Scene.RegionInfo.RegionID.ToString(),
                            Int32.Parse(Scene.RegionInfo.GetOtherSetting("SyncQuarkLocationX")), Int32.Parse(Scene.RegionInfo.GetOtherSetting("SyncQuarkLocationY"))))
                {
                    m_log.ErrorFormat("{0}: Failure registering quark", LogHeader);
                }
                */
                return info;
            }

            return null;
        }

        //Get the information for remote [IP:Port] to connect to for synchronization purpose.
        //For example, an actor may need to connect to several ScenePersistence's if the objects it operates are hosted collectively
        //by these ScenePersistence.
        //For now, we use configuration to access the information. Might be replaced by some Grid Service later on.
        //And for now, we assume there is only 1 remote listener to connect to.
        private void GetRemoteSyncListenerInfo()
        {
            //For now, we assume there is only one remote listener to connect to. Later on, 
            //we may need to modify the code to read in multiple listeners.
            //string addr = m_sysConfig.GetString(Scene.RegionInfo.RegionName + "_SyncListenerIPAddress", IPAddrUnknown);
            //int port = m_sysConfig.GetInt(Scene.RegionInfo.RegionName + "_SyncListenerPort", PortUnknown);
            m_log.DebugFormat("{0}: GetRemoteSyncListenerInfo() START", LogHeader);

            string addr;
            int port;
            try
            {
                addr = Scene.RegionInfo.GetOtherSetting("SyncServerAddress");
                port = Int32.Parse(Scene.RegionInfo.GetOtherSetting("SyncServerPort"));
            }
            catch (Exception e)
            {
                m_log.Warn(LogHeader + " Could not read SyncServerAddress or SyncServerPort from region info. Using defaults.");
                m_log.Warn(LogHeader + Scene.RegionInfo.GetOtherSetting("SyncServerAddress"));
                m_log.Warn(LogHeader + Scene.RegionInfo.GetOtherSetting("SyncServerPort"));
                addr = "127.0.0.1";
                port = 13000;
            }

            // if the address is not specified in the region configuration file, get it from the grid service
            if (addr.Equals(IPAddrUnknown))
            {
                /*
                List<GridEndpointInfo> lgei = Scene.GridService.LookupQuark(
                        Scene.RegionInfo.GetOtherSetting("SyncQuarkLocationX"), Scene.RegionInfo.GetOtherSetting("SyncQuarkLocationY"), "scene_persistence");
                if (lgei == null || lgei.Count != 1)
                {
                    m_log.ErrorFormat("{0}: Failed to find quark persistence actor", LogHeader);
                    addr = IPAddrUnknown;
                    port = PortUnknown;
                }
                else
                {
                    GridEndpointInfo gei = lgei[0];
                    addr = gei.address;
                    port = (int)gei.port;
                    m_log.WarnFormat("{0}: Found quark ({1}/{2}) persistence actor at {3}:{4}", LogHeader,
                            Scene.RegionInfo.GetOtherSetting("SyncQuarkLocationX"), Scene.RegionInfo.GetOtherSetting("SyncQuarkLocationY"),
                            addr, port.ToString());
                }
                 * */
                throw (new NotImplementedException("Grid Quark Registration is not implemented. You MUST specify server IP addresses in your region.ini file"));
            }

            if (!addr.Equals(IPAddrUnknown) && port != PortUnknown)
            {
                RegionSyncListenerInfo info = new RegionSyncListenerInfo(addr, port);
                m_remoteSyncListeners = new HashSet<RegionSyncListenerInfo>();
                m_remoteSyncListeners.Add(info);
            }
        }

        private void SyncStateDetailReport(Object[] args)
        {
            //Preliminary implementation
            EntityBase[] entities = Scene.GetEntities();
            List<SceneObjectGroup> sogList = new List<SceneObjectGroup>();
            foreach (EntityBase entity in entities)
            {
                if (entity is SceneObjectGroup)
                {
                    sogList.Add((SceneObjectGroup)entity);
                }
            }

            int primCount = 0;
            foreach (SceneObjectGroup sog in sogList)
            {
                primCount += sog.Parts.Length;
            }

            m_log.WarnFormat("SyncStateReport {0} -- Object count: {1}, Prim Count {2} ", Scene.RegionInfo.RegionName, sogList.Count, primCount);
            foreach (SceneObjectGroup sog in sogList)
            {
                m_log.WarnFormat("\n\n SyncStateReport -- SOG: name {0}, UUID {1}, position {2}", sog.Name, sog.UUID, sog.AbsolutePosition);

                foreach (SceneObjectPart part in sog.Parts)
                {
                    Vector3 pos = Vector3.Zero;
                    if (part.PhysActor != null)
                    {
                        pos = part.PhysActor.Position;
                    }
                    string debugMsg = "\nPart " + part.Name + "," + part.UUID+", LocalID "+part.LocalId + "ProfileShape "+part.Shape.ProfileShape;
                    if (part.TaskInventory.Count > 0)
                    {
                        debugMsg += ", has " + part.TaskInventory.Count + " inventory items";
                    }
                    if (part.ParentGroup.RootPart.UUID == part.UUID)
                    {
                        debugMsg += ", RootPart, ";
                        //else
                        //    debugMsg += ", ChildPart, ";
                        debugMsg += "ParentId = " + part.ParentID;
                        debugMsg += ", GroupPos " + part.GroupPosition + ", offset-position " + part.OffsetPosition;
                        if (part.ParentGroup.IsAttachment)
                        {
                            debugMsg += ", AttachedAvatar=" + part.ParentGroup.AttachedAvatar + ", AttachmentPoint = " + part.ParentGroup.AttachmentPoint;
                            debugMsg += ", AttachedPos = " + part.AttachedPos;
                        }
                        debugMsg += ", Flags = " + part.Flags.ToString();
                        debugMsg += ", LocalFlags = " + part.LocalFlags.ToString();
                        if (part.Text != String.Empty)
                        {
                            debugMsg += ", Text = " + part.Text+", Color = "+part.Color.ToString();
                        }
                        debugMsg += ", AggregateScriptEvents = " + part.AggregateScriptEvents;
                        debugMsg += ", VolumeDetectActive" + part.VolumeDetectActive; 

                        ScenePresence sp = Scene.GetScenePresence(part.ParentGroup.AttachedAvatar);
                        if (sp != null)
                        {
                            debugMsg += ", attached avatar's localID = "+sp.LocalId;
                        }
                        
                    }
                    m_log.WarnFormat(debugMsg);
                }
            }

            if (IsSyncRelay)
            {
                SyncMsg msg = new SyncMsgRegionStatus(this);
                ForEachSyncConnector(delegate(SyncConnector connector)
                {
                    connector.ImmediateOutgoingMsg(msg);
                });

            }
        }

        private void SyncDumpUUID(Object[] args)
        {
            UUID uuid = (UUID)(args[0]);
            bool full = ((string)args[1]).Equals("full");

            SyncInfoBase sib = m_SyncInfoManager.GetSyncInfo(uuid);
            if(sib == null)
            {
                m_log.Error("Usage: ssync uuid <uuid> (uuid not found in cache)");
                return;
            }

            Dictionary<SyncableProperties.Type, SyncedProperty> properties = sib.CurrentlySyncedProperties;
            foreach (SyncableProperties.Type property in Enum.GetValues(typeof(SyncableProperties.Type)))
            {
                SyncedProperty sprop;
                properties.TryGetValue(property, out sprop);
                string cachedVal = sprop == null ? "null" : sprop.LastUpdateValue.ToString();
                object sceneprop = sib.GetPropertyValue(property);
                string sceneVal = sceneprop == null ? "null" : sceneprop.ToString();
                if(cachedVal.ToString() != sceneVal.ToString() || full)
                    m_log.WarnFormat("PROPERTY: {0} {1,30} {2,30}", property, cachedVal, sceneVal);
            }
            
        }


        private void SyncStateReport(Object[] args)
        {
            //Preliminary implementation
            EntityBase[] entities = Scene.GetEntities();
            List<SceneObjectGroup> sogList = new List<SceneObjectGroup>();
            int avatarCount = 0;
            foreach (EntityBase entity in entities)
            {
                if (entity is SceneObjectGroup)
                    sogList.Add((SceneObjectGroup)entity);
                else
                    avatarCount++;
            }

            int primCount = 0;
            foreach (SceneObjectGroup sog in sogList)
            {
                primCount += sog.Parts.Length;
            }

            m_log.WarnFormat("SyncStateReport {0} -- Object count: {1}, Prim Count {2}, Av Count {3} ", Scene.RegionInfo.RegionName, sogList.Count, primCount, avatarCount);
            m_log.WarnFormat("Estimated size of SyncInfoManager is {0}", m_SyncInfoManager.Size);
        }

        private void SyncDebug(Object[] args)
        {
            /*
            if (Scene != null)
            {
                EntityBase[] entities = Scene.GetEntities();
                foreach (EntityBase entity in entities)
                {
                    if (entity is SceneObjectGroup)
                    {

                        SceneObjectGroup sog = (SceneObjectGroup)entity;

                        string sogXml = sog.ToXml2();

                        SceneObjectGroup sogCopy = SceneXmlLoader.DeserializeGroupFromXml2(sogXml);
                    }
                }
            }
             * */
            //Test HandleRemoteEvent_ScriptCollidingStart

            /*
            if (Scene != null)
            {
                EntityBase[] entities = Scene.GetEntities();
                SceneObjectGroup sog = null;

                foreach (EntityBase entity in entities)
                {
                    if (entity is SceneObjectGroup)
                    {

                        sog = (SceneObjectGroup)entity;
                        break;
                    }
                }

                if (sog != null)
                {
                    SceneObjectPart part = sog.RootPart;

                    OSDArray collisionUUIDs = new OSDArray();

                    UUID collider = UUID.Random();
                    collisionUUIDs.Add(OSD.FromUUID(collider));


                    OSDMap data = new OSDMap();
                    data["uuid"] = OSD.FromUUID(part.UUID);
                    data["collisionUUIDs"] = collisionUUIDs;
                    //SendSceneEvent(SymmetricSyncMessage.MsgType.ScriptCollidingStart, data);

                    ulong evSeq = GetNextEventSeq();
                    //data["actorID"] = OSD.FromString(ActorID);
                    data["syncID"] = OSD.FromString(m_syncID);
                    data["seqNum"] = OSD.FromULong(evSeq);
                    SymmetricSyncMessage rsm = new SymmetricSyncMessage(SymmetricSyncMessage.MsgType.ScriptCollidingStart, data);

                    //HandleRemoteEvent_ScriptCollidingStart(ActorID, evSeq, data, DateTime.UtcNow.Ticks);
                    HandleRemoteEvent_ScriptCollidingEvents(SymmetricSyncMessage.MsgType.ScriptCollidingStart, ActorID, evSeq, data, DateTime.UtcNow.Ticks);
                }
            }
            */
        }

        /*
        private void PrimSyncSerializationDebug()
        {
            if (Scene != null)
            {
                EntityBase[] entities = Scene.GetEntities();
                foreach (EntityBase entity in entities)
                {
                    if (entity is SceneObjectGroup)
                    {

                        SceneObjectGroup sog = (SceneObjectGroup)entity;

                        //First, create PrimSyncInfo for each part in SOG and insert 
                        //into the local record
                        foreach (SceneObjectPart part in sog.Parts)
                        {
                            m_SyncInfoManager.InsertSyncInfo(part.UUID, DateTime.UtcNow.Ticks, SyncID);
                        }

                        //Next test serialization
                        OSDMap sogData = EncodeSceneObject(sog);

                        //Next, test de-serialization
                        SceneObjectGroup group;
                        Dictionary<UUID, SyncInfoBase> primsSyncInfo;
                        if (!DecodeSceneObject(sogData, out group, out primsSyncInfo))
                            return;
                        

                        //Add the list of PrimSyncInfo to SyncInfoManager
                        foreach (KeyValuePair<UUID, SyncInfoBase> kvp in primsSyncInfo)
                            m_SyncInfoManager.InsertSyncInfo(kvp.Key, kvp.Value);

                        //Change each part's UUID so that we can add them to Scene and test the steps in AddNewSceneObjectByDecoding 
                        foreach (SceneObjectPart part in group.Parts)
                        {
                            UUID oldUUID = part.UUID;
                            part.UUID = UUID.Random();

                            SyncInfoBase syncInfo = primsSyncInfo[oldUUID];
                            primsSyncInfo.Add(part.UUID, syncInfo);
                        }

                        //Add the decoded object to Scene
                        Scene.AddNewSceneObject(group, true);

                        // Now the PhysActor of each part in sog has been created, set the PhysActor properties.
                        foreach (SyncInfoBase syncInfo in primsSyncInfo.Values)
                            syncInfo.SetPropertyValues(SyncableProperties.PhysActorProperties);

                        break;
                    }
                }
            }

        }
         */

        //end of debug functions

        //Start connections to each remote listener. 
        //For now, there is only one remote listener.
        private bool StartSyncConnections()
        {
            if (m_remoteSyncListeners == null)
            {
                m_log.Error(LogHeader + " SyncListener's address or port has not been configured.");
                return false;
            }

            if (m_synced)
            {
                m_log.Warn(LogHeader + ": Already synced.");
                return false;
            }

            bool connected = false;
            foreach (RegionSyncListenerInfo remoteListener in m_remoteSyncListeners)
            {
                SyncConnector syncConnector = new SyncConnector(m_syncConnectorNum++, remoteListener, this);
                if (syncConnector.Connect())
                {
                    syncConnector.StartCommThreads();
                    AddSyncConnector(syncConnector);
                    m_synced = true;
                    connected = true;
                }
            }

            return connected;
        }

        //To be called when a SyncConnector needs to be created by that the local listener receives a connection request
        public void AddNewSyncConnector(TcpClient tcpclient)
        {
            //Create a SynConnector due to an incoming request, and starts its communication threads
            SyncConnector syncConnector = new SyncConnector(m_syncConnectorNum++, tcpclient, this);
            syncConnector.StartCommThreads();
            AddSyncConnector(syncConnector);
        }

        private void AddSyncConnector(SyncConnector syncConnector)
        {
            lock (m_syncConnectorsLock)
            {
                // Create a new list while modifying the list: An optimization for frequent reads and occasional writes.
                // Anyone holding the previous version of the list can keep using it since
                // they will not hold it for long and get a new copy next time they need to iterate

                HashSet<SyncConnector> currentlist = m_syncConnectors;
                HashSet<SyncConnector> newlist = new HashSet<SyncConnector>(currentlist);
                newlist.Add(syncConnector);

                m_syncConnectors = newlist;
            }
        }

        private void RemoveSyncConnector(SyncConnector syncConnector)
        {
            lock (m_syncConnectorsLock)
            {
                // Create a new list while modifying the list: An optimization for frequent reads and occasional writes.
                // Anyone holding the previous version of the list can keep using it since
                // they will not hold it for long and get a new copy next time they need to iterate

                HashSet<SyncConnector> currentlist = m_syncConnectors;
                HashSet<SyncConnector> newlist = new HashSet<SyncConnector>(currentlist);
                newlist.Remove(syncConnector);

                if (newlist.Count == 0)
                {
                    m_synced = false;
                }

                m_syncConnectors = newlist;
            }

        }

        private bool IsSyncingWithOtherSyncNodes()
        {
            return (m_syncConnectors.Count > 0);
        }

        private void DoInitialSync()
        {
            Scene.DeleteAllSceneObjects();
            
            SendSyncMessage(new SyncMsgActorID(this, ActorID));
            // SendSyncMessage(new SyncMsgActorType(ActorType.ToString());
            // SendSyncMessage(new SyncMsgSyncID(m_syncID));

            // message sent to help calculating the difference in the clocks
            SendSyncMessage(new SyncMsgTimeStamp(this, DateTime.UtcNow.Ticks));

            SendSyncMessage(new SyncMsgRegionName(this, Scene.RegionInfo.RegionName));
            m_log.WarnFormat("{0}: Sending region name: \"{0}\"", LogHeader, Scene.RegionInfo.RegionName);

            SendSyncMessage(new SyncMsgGetRegionInfo(this));
            SendSyncMessage(new SyncMsgGetTerrain(this));
            SendSyncMessage(new SyncMsgGetPresences(this));
            SendSyncMessage(new SyncMsgGetObjects(this));

            //We'll deal with Event a bit later

            // Register for events which will be forwarded to authoritative scene
            // Scene.EventManager.OnNewClient += EventManager_OnNewClient;
            //Scene.EventManager.OnMakeRootAgent += EventManager_OnMakeRootAgent;
            //Scene.EventManager.OnMakeChildAgent += EventManager_OnMakeChildAgent;
            //Scene.EventManager.OnClientClosed += new EventManager.ClientClosed(RemoveLocalClient);
        }

        /// <summary>
        /// This function will send out the sync message right away, without putting it into the SyncConnector's queue.
        /// Should only be called for infrequent or high prority messages.
        /// </summary>
        /// <param name="msg"></param>
        private void SendSyncMessage(SyncMsg msg)
        {
            ForEachSyncConnector(delegate(SyncConnector syncConnector)
            {
                syncConnector.ImmediateOutgoingMsg(msg);
            });
        }

        private void ForEachSyncConnector(Action<SyncConnector> action)
        {
            List<SyncConnector> closed = null;

            HashSet<string> connectedRegions = new HashSet<string>();
            // The local region is always connected
            connectedRegions.Add(Scene.Name);

            foreach (SyncConnector syncConnector in m_syncConnectors)
            {
                // If connected, apply the action
                if (syncConnector.connected)
                {
                    action(syncConnector);
                    connectedRegions.Add(syncConnector.otherSideRegionName);
                }                
                    // Else, remove the SyncConnector from the list
                else
                {
                    if (closed == null)
                        closed = new List<SyncConnector>();
                    closed.Add(syncConnector);
                }
            }

            // If a connector has disconnected
            if (closed != null)
            {
                // Remove the disconnected connectors
                foreach (SyncConnector connector in closed)
                {
                    RemoveSyncConnector(connector);
                }

                // Remove scene presences from disconnected regions
                List<UUID> avatarsToRemove = new List<UUID>();
                Scene.ForEachRootScenePresence(delegate(ScenePresence sp)
                {
                    UUID uuid = sp.UUID;
                    SyncInfoPresence sip = (SyncInfoPresence)(m_SyncInfoManager.GetSyncInfo(uuid));
                    string cachedRealRegionName = (string)(sip.CurrentlySyncedProperties[SyncableProperties.Type.RealRegion].LastUpdateValue);
                    if (!connectedRegions.Contains(cachedRealRegionName))
                    {
                        avatarsToRemove.Add(uuid);
                    }

                });
                foreach (UUID uuid in avatarsToRemove)
                {
                    Scene.RemoveClient(uuid, false);
                }
            }
        }

        public void CleanupAvatars()
        {
            // Force a loop through each of the sync connectors which will clean up any disconnected connectors.
            ForEachSyncConnector(delegate(SyncConnector sc){ ; });
        }

        #region Sync message handlers

        /// <summary>
        /// The handler for processing incoming sync messages.
        /// </summary>
        /// <param name="msg"></param>
        /// <param name="senderActorID">ActorID of the sender</param>
        public void HandleIncomingMessage(SyncMsg msg, string senderActorID, SyncConnector syncConnector)
        {
            // m_log.WarnFormat("{0} HandleIncomingMessage: {1}", LogHeader, msg.ToString());
            lock (m_stats) m_statMsgsIn++;
            msg.HandleIn(this);
        }

        ///////////////////////////////////////////////////////////////////////
        // Per property sync handlers
        ///////////////////////////////////////////////////////////////////////
        void estate_OnRegionInfoChange(UUID regionID)
        {
            if (regionID != Scene.RegionInfo.RegionID)
                return;
            if(IsLocallyGeneratedEvent(SyncMsg.MsgType.RegionInfo, null))
                return;
            SyncMsgRegionInfo msg = new SyncMsgRegionInfo(this, Scene.RegionInfo);
            foreach (SyncConnector connector in GetSyncConnectorsForUpdates())
            {
                connector.ImmediateOutgoingMsg(msg);
            }
        }
        
        // DSG DEBUG
        /// <summary>
        /// Write out a detailed logging message for the change in GroupPosition.
        /// Will syncedProperties if you have the structures
        /// but if you pass 'null', it will be looked up.
        /// </summary>
        /// <param name="sopUUID">the UUID of the sop being logged</param>
        /// <param name="syncedProperties">hashset of PropertiesSyncInfo. If null, will be looked up.</param>
        /// <param name="op">text string telling the type of operation being logged</param>
        /// <param name="msgLen">the length of the message being sent/received</param>
        public void DetailedUpdateLogging(UUID sopUUID,
                            HashSet<SyncableProperties.Type> updatedProperties,
                            HashSet<SyncedProperty> syncedProperties,
                            string op,
                            string senderReceiver,
                            int msgLen )
        {
            // save the lookup work if logging is not enabled
            if (!m_detailedLog.Enabled) return;

            SyncableProperties.Type propertyToCheck = SyncableProperties.Type.GroupPosition;
            if (updatedProperties != null && !updatedProperties.Contains(SyncableProperties.Type.GroupPosition))
            {
                // the group position is not being updated this time so just pick the first property in the set
                foreach (SyncableProperties.Type t in updatedProperties)
                {
                    propertyToCheck = t;
                    break;
                }
            }
            SyncedProperty syncedProperty = null;
            // Did the caller pass us the list of synced properties? (can save the lookup time)
            if (syncedProperties == null)
            {
                try
                {
                    // go find the synced properties for this updated object
                    SyncInfoBase sib = m_SyncInfoManager.GetSyncInfo(sopUUID);
                    if (sib != null)
                    {
                        Dictionary<SyncableProperties.Type, SyncedProperty> primProperties = sib.CurrentlySyncedProperties;
                        primProperties.TryGetValue(propertyToCheck, out syncedProperty);

                        // odd, this should not happen. If there is no instance of the property, just use the first sync info
                        if (syncedProperty == null && primProperties.Count > 0)
                        {
                            // the property we're looking for is not there. Just use the first one in the list
                            Dictionary<SyncableProperties.Type, SyncedProperty>.Enumerator eenum = primProperties.GetEnumerator();
                            eenum.MoveNext();
                            syncedProperty = eenum.Current.Value;
                        }
                    }
                }
                catch
                {
                    syncedProperty = null;
                }
            }
            else
            {
                // we were passed the list of syncInfos so look for our property in there
                foreach (SyncedProperty psi in syncedProperties)
                {
                    if (psi.Property == propertyToCheck)
                    {
                        syncedProperty = psi;
                        break;
                    }
                }
                if (syncedProperty == null && syncedProperties.Count > 0)
                {
                    // again, this should not happen but recover by using the first sync info in the list
                    HashSet<SyncedProperty>.Enumerator eenum = syncedProperties.GetEnumerator();
                    eenum.MoveNext();
                    syncedProperty = eenum.Current;
                }
            }
            if (syncedProperty != null)
            {
                // get something to report as the properties being changed
                string propertyName = GenerateUpdatedPropertyName(sopUUID, syncedProperty, updatedProperties, syncedProperties);

                StringBuilder sb = new StringBuilder(op);
                sb.Append(",");
                sb.Append(DateTime.UtcNow.Ticks.ToString());
                sb.Append(",");
                sb.Append(sopUUID.ToString());
                sb.Append(",");
                sb.Append(syncedProperty.LastUpdateTimeStamp.ToString());
                sb.Append(",");
                sb.Append(propertyName);
                sb.Append(",");
                sb.Append(senderReceiver);
                sb.Append(",");
                sb.Append(syncedProperty.LastUpdateSyncID.ToString());
                sb.Append(",");
                sb.Append(msgLen.ToString());
                m_detailedLog.Write(sb.ToString());
                /*
                // My presumption is that using a StringBuilder and doing my own conversions
                //    is going to be faster then all the checking that happens in String.Format().
                m_detailedLog.Write("{0},{1},{2},{3},{4},{5},{6},{7}",
                            op,
                            DateTime.UtcNow.Ticks,
                            sopUUID,
                            syncedProperty.LastUpdateTimeStamp,
                            syncedProperty.Property.ToString(),
                            senderReceiver,
                            syncedProperty.LastUpdateSyncID,
                            msgLen);
                 */
            }
        }

        // version for if you don't know the message length
        public void DetailedUpdateLogging(UUID sopUUID,
                            HashSet<SyncableProperties.Type> updatedProperties,
                            HashSet<SyncedProperty> syncedProperties,
                            string op,
                            string senderReceiver)
        {
            DetailedUpdateLogging(sopUUID, updatedProperties, syncedProperties, op, senderReceiver, 0);
        }

        // Version with the uuid.ToString in one place
        public void DetailedUpdateWrite(string op, UUID theUuid, long lastUpdate, string syncID, string senderReceiver, int msgLen)
        {
            DetailedUpdateWrite(op, theUuid.ToString(), lastUpdate, syncID, senderReceiver, msgLen);
        }

        // version for logging events other than property updates
        public void DetailedUpdateWrite(string op, string uuid, long lastUpdate, string syncID, string senderReceiver, int msgLen)
        {
            if (!m_detailedLog.Enabled) return;

            long nowTime = DateTime.UtcNow.Ticks;  // save fetching the time twice
            long lastUpdateTime = lastUpdate == 0 ? nowTime : lastUpdate;

            StringBuilder sb = new StringBuilder(op);
            sb.Append(",");
            sb.Append(nowTime.ToString());
            sb.Append(",");
            sb.Append(uuid.ToString());
            sb.Append(",");
            sb.Append(lastUpdateTime.ToString());
            sb.Append(",");
            sb.Append("Detail");
            sb.Append(",");
            sb.Append(senderReceiver);
            sb.Append(",");
            sb.Append(syncID);
            sb.Append(",");
            sb.Append(msgLen.ToString());
            m_detailedLog.Write(sb.ToString());
            /*
            m_detailedLog.Write("{0},{1},{2},{3},{4},{5},{6},{7}",
                        op,
                        DateTime.UtcNow.Ticks,
                        uuid,
                        lastUpdateTime,
                        "Detail",
                        senderReceiver,
                        syncID,
                        msgLen);
             */
        }

        // If enabled in the configuration file, fill the properties field with a list of all
        //  the properties updated along with their value.
        // This is a lot of work, but it really helps when debugging.
        // Routine returns the name of the passed 'syncedProperty.Type' (short form)
        //   or a list of all the changed properties with their values.
        // The string output will have no commas in it so it doesn't break the comma
        //   separated log fields. Each property/values are separated by "/"s.
        private string GenerateUpdatedPropertyName(
                            UUID sopUUID,
                            SyncedProperty syncedProperty,
                            HashSet<SyncableProperties.Type> updatedProperties,
                            HashSet<SyncedProperty> syncedProperties )
        {
            //  Default is just the name from the passed updated property
            string ret = syncedProperty.Property.ToString();

            // if we're not doing detailed values, just return the default, short property section
            if (!m_detailedPropertyValues) return ret;

            // We are called two ways: either we are passed a list of synced properties
            //    or we have to fetch the one for this UUID.
            HashSet<SyncedProperty> properties = syncedProperties;
            // if we were not passed properties, get the currently outbound updated properties
            if (properties == null)
            {
                SyncInfoBase sib = m_SyncInfoManager.GetSyncInfo(sopUUID);
                if (sib != null)
                {
                    Dictionary<SyncableProperties.Type, SyncedProperty> currentlySyncedProperties = sib.CurrentlySyncedProperties;
                    properties = new HashSet<SyncedProperty>();
                    foreach (KeyValuePair<SyncableProperties.Type, SyncedProperty> kvp in currentlySyncedProperties)
                    {
                        properties.Add(kvp.Value);
                    }
                }
            }
            if (properties != null)
            {
                StringBuilder sb = new StringBuilder();
                foreach (SyncedProperty synp in properties)
                {
                    // if this is not one of the updated properties, don't output anything
                    if (!updatedProperties.Contains(synp.Property)) continue;

                    try
                    {
                        // put the name of the property
                        if (sb.Length != 0) sb.Append("/");
                        sb.Append(synp.Property.ToString());

                        string sVal = null;

                        // There are some properties that we don't want to output
                        //   or that we want to format specially.
                        switch (synp.Property)
                        {
                            // don't print out value
                            case SyncableProperties.Type.AgentCircuitData:
                            case SyncableProperties.Type.AvatarAppearance:
                            case SyncableProperties.Type.Shape:
                                break;
                            case SyncableProperties.Type.Animations:
                                OSDArray anims = synp.LastUpdateValue as OSDArray;
                                if (anims != null)
                                {
                                    foreach (OSD anim in anims)
                                    {
                                        OSDMap animMap = anim as OSDMap;
                                        if (animMap != null)
                                        {
                                            OpenSim.Framework.Animation oneAnim = new OpenSim.Framework.Animation(animMap);
                                            sVal += oneAnim.ToString();
                                            sVal += "&";
                                        }
                                    }
                                }
                                break;
                            // print out specific uint values as hex
                            case SyncableProperties.Type.AgentControlFlags:
                            case SyncableProperties.Type.AggregateScriptEvents:
                                uint acf = (uint)synp.LastUpdateValue;
                                sVal = acf.ToString("X");
                                break;
                            // default relies on 'ToString()' to output the correct format
                            default:
                                Object val = synp.LastUpdateValue;
                                sVal = val.ToString();
                                break;
                        }
                        if (sVal != null)
                        {
                            sb.Append("=");
                            sb.Append(sVal);
                        }
                    }
                    catch 
                    {
                        // if value fetching fails, just go onto next property
                    }
                }
                // So the fields are still separated by commas, replace all the commas in the values
                ret = sb.ToString().Replace(", ", ";");
            }

            return ret;
        }
        // END DSG DEBUG

        #endregion //Sync message handlers

        #region Remote Event handlers

        public EventsReceived EventRecord = new EventsReceived();
        
        #region RememberLocallyGeneratedEvents

        [ThreadStatic]
        static string LocallyGeneratedSignature;

        /// <summary>
        /// There is a problem where events that call On*Event might be because we triggered
        /// the event for a received event message. This routine is called before we trigger
        /// an event so we can check to see if a sensed event has the same parameters as one
        /// we triggered.
        /// These routines receive all the event parameters so any matching algorithm can
        /// be implemented.
        /// </summary>
        /// <param name="msgtype"></param>
        /// <param name="parms">Parameters being passed to the event</param>
        public void RememberLocallyGeneratedEvent(SyncMsg.MsgType msgtype, params Object[] parms)
        {
            // This is a terrible kludge but it will work for the short term.
            // We mark the thread used to call Trigger*Event with the name of the type of event
            // being generated. That name is checked when the On*Event is fired and, if the
            // same, we presume it was our call that caused the On*Event.
            // When the On*Event happens, we remove the flag from the thread.
            // This relies on many assumptions. The principle of which is that the
            // processing of received events is done with one thread and that thread
            // is the one that calls Trigger*Event and will be the one that returns
            // on the On*Event. All the Trigger*Event routines serialize the calls
            // to the event handling routines so this will hold true. 
            // We also rely on every Trigger*Event generating an On*Event.
            // It is possible that some event handling routine might generate
            // an event of the same type. This would cause an event to disappear.
            LocallyGeneratedSignature = CreateLocallyGeneratedEventSignature(msgtype, parms);
            // m_log.DebugFormat("{0} RememberLocallyGeneratedEvent. Remembering={1}", LogHeader, LocallyGeneratedSignature);      // DEBUG DEBUG
            return;
        }

        /// <summary>
        /// Test to see if the current event is probably one we just generated.
        /// </summary>
        /// <param name="msgtype">SymmetricSyncMessage.MsgType of the event</param>
        /// <param name="parms">the parameters received for the event</param>
        /// <returns>true if this is an event we just called Trigger* for</returns>
        public bool IsLocallyGeneratedEvent(SyncMsg.MsgType msgtype, params Object[] parms)
        {
            bool ret = false;
            // m_log.DebugFormat("{0} IsLocallyGeneratedEvent. Checking remembered={1} against {2}", LogHeader, LocallyGeneratedSignature, msgtype);      // DEBUG DEBUG
            if (LocallyGeneratedSignature == CreateLocallyGeneratedEventSignature(msgtype, parms))
            {
                ret = true;
            }
            return ret;
        }

        /// <summary>
        /// Forget that we are remembering a message being processed.
        /// </summary>
        public void ForgetLocallyGeneratedEvent()
        {
            LocallyGeneratedSignature = "";
        }

        /// <summary>
        /// Create a unique string that identifies this event. This is generated when we call Trigger*Event
        /// and then is checked on the On*Event to see if the received event is one we just generated.
        /// </summary>
        /// <param name="msgtype"></param>
        /// <param name="parms"></param>
        /// <returns></returns>
        private string CreateLocallyGeneratedEventSignature(SyncMsg.MsgType msgtype, params Object[] parms)
        {
            return msgtype.ToString();
        }

        #endregion RememberLocallyGeneratedEvents

        /// <summary>
        /// The handler for (locally initiated) event OnNewScript: triggered by client's RezSript packet, publish it to other actors.
        /// </summary>
        /// <param name="clientID">ID of the client who creates the new script</param>
        /// <param name="part">the prim that contains the new script</param>
        public delegate void NewRezScript(uint localID, UUID itemID, string script, int startParam, bool postOnRez, string engine, int stateSource);
        private void OnLocalNewScript(UUID clientID, SceneObjectPart part, UUID itemID)
        {
            UUID uuid = part.UUID;
            // m_log.DebugFormat("{0}: RegionSyncModule_OnLocalNewScript", LogHeader);
            if (IsLocallyGeneratedEvent(SyncMsg.MsgType.NewScript, clientID, part, itemID))
                return;

            SceneObjectGroup sog = part.ParentGroup;
            if(sog==null){
                m_log.Warn(LogHeader + ": part " + uuid + " not in an SceneObjectGroup yet. Will not propagating new script event");
                //sog = new SceneObjectGroup(part);
                return;
            }
            HashSet<SyncableProperties.Type> updatedProperties = m_SyncInfoManager.UpdateSyncInfoByLocal(uuid, 
                new HashSet<SyncableProperties.Type>(){SyncableProperties.Type.TaskInventory});

            //It is very likely that the TaskInventory cache data in SyncInfoManager
            //has been updated by local RezScript(), which will only update
            //inventory but not create a script instance unless this is a 
            //script engine. We just make sure that if that does not happen 
            //ealier than this, we are sync'ing the new TaskInventory.
            updatedProperties.Add(SyncableProperties.Type.TaskInventory);

            SyncMsgNewScript msg = new SyncMsgNewScript(this, part.UUID, clientID, itemID, updatedProperties);

            SendSceneEvent(msg);
        }

        /// <summary>
        /// The handler for (locally initiated) event OnUpdateScript: publish it to other actors.
        /// </summary>
        /// <param name="agentID"></param>
        /// <param name="itemId"></param>
        /// <param name="primId"></param>
        /// <param name="isScriptRunning"></param>
        /// <param name="newAssetID"></param>
        private void OnLocalUpdateScript(UUID agentID, UUID itemId, UUID primId, bool isScriptRunning, UUID newAssetID)
        {
            // m_log.DebugFormat("{0}: RegionSyncModule_OnUpdateScript", LogHeader);
            if (IsLocallyGeneratedEvent(SyncMsg.MsgType.UpdateScript, agentID, itemId, isScriptRunning, newAssetID))
                return;

            SyncMsgUpdateScript msg = new SyncMsgUpdateScript(this, agentID, itemId, primId, isScriptRunning, newAssetID);
            SendSceneEvent(msg);
        }

        private void OnLocalScriptReset(uint localID, UUID itemID)
        {
            // m_log.DebugFormat("{0}: OnLocalScriptReset: obj={1}", LogHeader, itemID);
            if (IsLocallyGeneratedEvent(SyncMsg.MsgType.ScriptReset, localID, itemID))
                return;

            //we will use the prim's UUID as the identifier, not the localID, to publish the event for the prim                
            SceneObjectPart part = Scene.GetSceneObjectPart(localID);
            if (part == null)
            {
                m_log.Warn(LogHeader + ": part with localID " + localID + " not exist");
                return;
            }
            SyncMsgScriptReset msg = new SyncMsgScriptReset(this, part.UUID, itemID);
            SendSceneEvent(msg);
        }

        private void OnLocalChatBroadcast(Object sender, OSChatMessage chat)
        {
            
            if (IsLocallyGeneratedEvent(SyncMsg.MsgType.ChatBroadcast, sender, chat))
                return;

            SyncMsgChatBroadcast msg = new SyncMsgChatBroadcast(this, chat);
            SendSceneEvent(msg);
        }

        private void OnLocalChatFromClient(Object sender, OSChatMessage chat)
        {
            if (chat.Sender is RegionSyncAvatar)
                return;
            //if (IsLocallyGeneratedEvent(SymmetricSyncMessage.MsgType.ChatFromClient, sender, chat))
            //    return;

            SyncMsgChatFromClient msg = new SyncMsgChatFromClient(this, chat);
            SendSceneEvent(msg);
        }

        private void OnLocalChatFromWorld(Object sender, OSChatMessage chat)
        {
            if (IsLocallyGeneratedEvent(SyncMsg.MsgType.ChatFromWorld, sender, chat))
                return;

            //m_log.WarnFormat("RegionSyncModule.OnLocalChatFromWorld {0}:{1}", chat.From, chat.Message);
            SyncMsgChatFromWorld msg = new SyncMsgChatFromWorld(this, chat);
            SendSceneEvent(msg);
        }

        private void OnLocalAttach(uint localID, UUID itemID, UUID avatarID)
        {
            if (IsLocallyGeneratedEvent(SyncMsg.MsgType.Attach, localID, itemID, avatarID))
                return;

            OSDMap data = new OSDMap();
            SceneObjectPart part = Scene.GetSceneObjectPart(localID);
            if (part == null)
            {
                m_log.Warn(LogHeader + ", OnLocalAttach: no part with localID: " + localID);
                return;
            }
            SyncMsgAttach msg = new SyncMsgAttach(this, part.UUID, itemID, avatarID);
            SendSceneEvent(msg);
        }

        /* UNUSED??
        private void OnLocalPhysicsCollision(UUID partUUID, OSDArray collisionUUIDs)
        {
            //temp solution for reducing collision events for demo
            OSDArray collisionUUIDsArgs = new OSDArray();
            switch (m_reportCollisions)
            {
                case "All":
                    break;
                case "PrimToAvatarOnly":
                    SceneObjectPart part = Scene.GetSceneObjectPart(partUUID);
                    if (part == null) return;
                    for (int i = 0; i < collisionUUIDs.Count; i++)
                    {
                        OSD arg = collisionUUIDs[i];
                        UUID collidingUUID = arg.AsUUID();
                        ScenePresence sp = Scene.GetScenePresence(collidingUUID);
                        //if not colliding with an avatar (sp==null), don't propagate
                        if (sp != null)
                        {
                            collisionUUIDsArgs.Add(arg);
                        }
                    }
                    break;
                default:
                    break;
            }

            if(collisionUUIDsArgs.Count>0){
                OSDMap data = new OSDMap();
                data["uuid"] = OSD.FromUUID(partUUID);
                data["collisionUUIDs"] = collisionUUIDs;
                SendSceneEvent(SymmetricSyncMessage.MsgType.PhysicsCollision, data);
            }
        }
         */

        private void OnLocalGrabObject(uint localID, uint originalID, Vector3 offsetPos, IClientAPI remoteClient, SurfaceTouchEventArgs surfaceArgs)
        {
            if (IsLocallyGeneratedEvent(SyncMsg.MsgType.ObjectGrab, localID, originalID, offsetPos, remoteClient, surfaceArgs))
                return;

            UUID localUUID, originalUUID;
            GetGrabUUIDs(localID, out localUUID, originalID, out originalUUID);
            SyncMsgObjectGrab msg = new SyncMsgObjectGrab(this, remoteClient.AgentId, localUUID, originalUUID, offsetPos, surfaceArgs);
            SendSceneEvent(msg);
        }

        private void OnLocalObjectGrabbing(uint localID, uint originalID, Vector3 offsetPos, IClientAPI remoteClient, SurfaceTouchEventArgs surfaceArgs)
        {
            if (IsLocallyGeneratedEvent(SyncMsg.MsgType.ObjectGrabbing, localID, originalID, offsetPos, remoteClient, surfaceArgs))
                return;

            UUID localUUID, originalUUID;
            GetGrabUUIDs(localID, out localUUID, originalID, out originalUUID);
            SyncMsgObjectGrabbing msg = new SyncMsgObjectGrabbing(this, remoteClient.AgentId, localUUID, originalUUID, offsetPos, surfaceArgs);
            SendSceneEvent(msg);
        }

        private void OnLocalDeGrabObject(uint localID, uint originalID, IClientAPI remoteClient, SurfaceTouchEventArgs surfaceArgs)
        {
            if (IsLocallyGeneratedEvent(SyncMsg.MsgType.ObjectDeGrab, localID, originalID, remoteClient, surfaceArgs))
                return;

            Vector3 offsetPos = Vector3.Zero;
            UUID localUUID, originalUUID;
            GetGrabUUIDs(localID, out localUUID, originalID, out originalUUID);
            SyncMsgObjectDeGrab msg = new SyncMsgObjectDeGrab(this, remoteClient.AgentId, localUUID, originalUUID, offsetPos, surfaceArgs);
            SendSceneEvent(msg);
        }


        private bool GetGrabUUIDs(uint partID, out UUID partUUID, uint origPartID, out UUID origPartUUID)
        {
            partUUID = UUID.Zero;
            origPartUUID = UUID.Zero;

            SceneObjectPart part = Scene.GetSceneObjectPart(partID);
            if (part == null)
            {
                m_log.ErrorFormat("{0}: GetGrabUUIDs - part with localID {1} does not exist", LogHeader, partID);
                return false;
            }
            partUUID = part.UUID;

            SceneObjectPart originalPart = null;
            if (origPartID != 0)
            {
                originalPart = Scene.GetSceneObjectPart(origPartID);
                if (originalPart == null)
                {
                    m_log.ErrorFormat("{0}: GetGrabUUIDs - part with localID {1} does not exist", LogHeader, origPartID);
                    return false;
                }
                origPartUUID = originalPart.UUID;
            }
            return true;
        }

        private void OnLocalScriptCollidingStart(uint localID, ColliderArgs colliders)
        {
            m_log.WarnFormat("{0}: OnLocalScriptCollidingStart", LogHeader);
            if (IsLocallyGeneratedEvent(SyncMsg.MsgType.ScriptCollidingStart, localID, colliders))
                return;

            SyncMsgScriptCollidingStart msg = new SyncMsgScriptCollidingStart(this, GetSOPUUID(localID), localID, colliders.Colliders);
            SendSceneEvent(msg);
        }

        private void OnLocalScriptColliding(uint localID, ColliderArgs colliders)
        {
            if (IsLocallyGeneratedEvent(SyncMsg.MsgType.ScriptColliding, localID, colliders))
                return;

            SyncMsgScriptColliding msg = new SyncMsgScriptColliding(this, GetSOPUUID(localID), localID, colliders.Colliders);
            SendSceneEvent(msg);
        }

        private void OnLocalScriptCollidingEnd(uint localID, ColliderArgs colliders)
        {
            m_log.WarnFormat("{0}: OnLocalScriptCollidingEnd", LogHeader);
            if (IsLocallyGeneratedEvent(SyncMsg.MsgType.ScriptCollidingEnd, localID, colliders))
                return;

            SyncMsgScriptCollidingEnd msg = new SyncMsgScriptCollidingEnd(this, GetSOPUUID(localID), localID, colliders.Colliders);
            SendSceneEvent(msg);
        }

        private void OnLocalScriptLandCollidingStart(uint localID, ColliderArgs colliders)
        {
            if (IsLocallyGeneratedEvent(SyncMsg.MsgType.ScriptLandCollidingStart, localID, colliders))
                return;

            SyncMsgScriptLandCollidingStart msg = new SyncMsgScriptLandCollidingStart(this, GetSOPUUID(localID), localID, colliders.Colliders);
            SendSceneEvent(msg);
        }

        private void OnLocalScriptLandColliding(uint localID, ColliderArgs colliders)
        {
            if (IsLocallyGeneratedEvent(SyncMsg.MsgType.ScriptLandColliding, localID, colliders))
                return;

            SyncMsgScriptLandColliding msg = new SyncMsgScriptLandColliding(this, GetSOPUUID(localID), localID, colliders.Colliders);
            SendSceneEvent(msg);
        }

        private void OnLocalScriptLandCollidingEnd(uint localID, ColliderArgs colliders)
        {
            if (IsLocallyGeneratedEvent(SyncMsg.MsgType.ScriptLandCollidingEnd, localID, colliders))
                return;

            SyncMsgScriptLandCollidingEnd msg = new SyncMsgScriptLandCollidingEnd(this, GetSOPUUID(localID), localID, colliders.Colliders);
            SendSceneEvent(msg);
        }

        private OSDMap PrepareCollisionArgs(uint localID, ColliderArgs colliders)
        {
            SceneObjectPart part = Scene.GetSceneObjectPart(localID);
            if (part == null)
                return null;

            OSDMap data = new OSDMap();
            OSDArray collisionUUIDs = new OSDArray();
            foreach (DetectedObject detObj in colliders.Colliders)
            {
                collisionUUIDs.Add(OSD.FromUUID(detObj.keyUUID));
            }

            data["uuid"] = OSD.FromUUID(part.UUID);
            data["collisionUUIDs"] = collisionUUIDs;
            return data;
        }
        private UUID GetSOPUUID(uint localID)
        {
            UUID ret = UUID.Zero;
            SceneObjectPart part = Scene.GetSceneObjectPart(localID);
            if (part != null)
                ret = part.UUID;
            return ret;
        }

        // Several routines rely on the check for 'data == null' to skip processing
        // when there are selection errors.
        private void SendSceneEvent(SyncMsg msg)
        {
            SyncMsgEvent msgEvent = msg as SyncMsgEvent;
            if (msgEvent != null)
            {
                msgEvent.SyncID = SyncID;
                msgEvent.SequenceNum = GetNextEventSeq();
            }
            //send to actors who are interested in the event
            SendSceneEventToRelevantSyncConnectors(ActorID, msg, null);
        }

        private ulong GetNextEventSeq()
        {
            return m_eventSeq++;
        }
         
        #endregion //Remote Event handlers

        private SyncInfoManager m_SyncInfoManager;
        public SyncInfoManager InfoManager { get { return m_SyncInfoManager; } }

        #region Prim Property Sync management
        //private 
        
        private void OnSceneObjectPartUpdated(SceneObjectPart part, bool full)
        {
            // If the scene presence update event was triggered by a call from RegionSyncModule, then we don't need to handle it.
            // Changes to scene presence that are actually local will not have originated from this module or thread.
            if (IsLocallyGeneratedEvent(SyncMsg.MsgType.UpdatedProperties))
                return;

            UUID uuid = part.UUID;
            HashSet<SyncableProperties.Type> properties = new HashSet<SyncableProperties.Type>(full ? SyncableProperties.FullUpdateProperties : SyncableProperties.TerseUpdateProperties);

            // m_log.WarnFormat("{0}: OnSceneObjectPartUpdated uuid={1}", LogHeader, part.UUID);

            if (part == null || part.ParentGroup == null || part.ParentGroup.IsDeleted)
                return;

            int count1 = properties.Count;
            if (part.PhysActor == null)
                properties.ExceptWith(SyncableProperties.PhysActorProperties);
            //if (properties.Count != count1)
            //    m_log.WarnFormat("{0}: OnSceneObjectPartUpdated: Filtered PhysActor properties from part with no PhysActor: {1}", LogHeader, part.UUID);

            //Sync values with SOP's data and update timestamp according, to 
            //obtain the list of properties that really have been updated
            //and should be propogated to other sync nodes.
            HashSet<SyncableProperties.Type> propertiesWithSyncInfoUpdated = m_SyncInfoManager.UpdateSyncInfoByLocal(uuid, properties);

            // If this isn't the "parent" prim, don't enqueue the group properites
            if (uuid != part.ParentGroup.UUID)
            {
                propertiesWithSyncInfoUpdated.ExceptWith(SyncableProperties.GroupProperties);
                // m_log.WarnFormat("{0}: OnSceneObjectPartUpdated: Filtered GroupProperties from non-root part: {1}", LogHeader, part.UUID);
            }

            //If this is a prim attached to avatar, don't enqueque the properties defined in AttachmentNonSyncProperties
            if (part.ParentGroup.IsAttachment)
            {
                propertiesWithSyncInfoUpdated.ExceptWith(SyncableProperties.AttachmentNonSyncProperties);
            }

            // Enqueue whatever properties are left in the set
            EnqueueUpdatedProperty(uuid, propertiesWithSyncInfoUpdated);
        }

        //private int m_updateTick = 0;
        //private StringBuilder m_updateLoopLogSB;
        /// <summary>
        /// Triggered periodically to send out sync messages that include 
        /// prim and scene presence properties that have been updated since last SyncOut.
        /// </summary>
        private void SyncOutUpdates(Scene scene)
        {
            //we are riding on this periodic events to check if there are un-handled sync event messages
            if (m_savedSyncMessage.Count > 0)
            {
                // m_log.WarnFormat("{0} SyncOutUpdates: m_savedSyncMessage.Count = {1}", LogHeader, m_savedSyncMessage.Count);
                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    List<SyncMessageRecord> savedSyncMessage;
                    lock (m_savedSyncMessage)
                    {
                        savedSyncMessage = new List<SyncMessageRecord>(m_savedSyncMessage);
                        m_savedSyncMessage.Clear();
                    }
                    foreach (SyncMessageRecord syncMsgSaved in savedSyncMessage)
                    {
                        SyncMsg msg = syncMsgSaved.SyncMessage;
                        // Re-invoke the event to see if we can get the object this time
                        msg.HandleIn(this);
                    }
                });
            }

            // Existing value of 1 indicates that updates are currently being sent so skip updates this pass
            if (Interlocked.Exchange(ref m_sendingPropertyUpdates, 1) == 1)
            {
                m_log.WarnFormat("{0} SyncOutUpdates(): An update thread is already running.", LogHeader);
                return;
            }

            //m_updateTick++;

            lock (m_propertyUpdateLock)
            {
                /*
                bool tickLog = false;
                DateTime startTime = DateTime.Now;
                if (m_propertyUpdates.Count > 0)
                {
                    tickLog = true;
                    //m_log.InfoFormat("SyncOutPrimUpdates - tick {0}: START the thread for SyncOutUpdates, {1} prims, ", m_updateTick, m_propertyUpdates.Count);
                    m_updateLoopLogSB = new StringBuilder(m_updateTick.ToString());
                }
                 * */ 

                //copy the updated  property list, and clear m_propertyUpdates immediately for future use
                Dictionary<UUID, HashSet<SyncableProperties.Type>> updates = new Dictionary<UUID, HashSet<SyncableProperties.Type>>(m_propertyUpdates);
                m_propertyUpdates.Clear();

                // Starting a new thread to prepare sync message and enqueue it to SyncConnectors
                // Might not be syncing right now or have any updates, but the worker thread will determine that just before the send
                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    // If syncing with other nodes, send updates
                    if(IsSyncingWithOtherSyncNodes())
                    {
            			int updateIndex = 0;
                        foreach (KeyValuePair<UUID, HashSet<SyncableProperties.Type>> update in updates)
                        {
                            UUID uuid = update.Key;
                            HashSet<SyncableProperties.Type> updatedProperties = update.Value;

                            // Skip if the uuid is no longer in the local Scene or if the part is being deleted
                            //if ((sp == null) && (sop == null || sop.ParentGroup == null || sop.ParentGroup.IsDeleted))
                            //    continue;

                            //Sync the SOP data and cached property values in SyncInfoManager again
                            //HashSet<SyncableProperties.Type> propertiesWithSyncInfoUpdated = m_SyncInfoManager.UpdateSyncInfoByLocal(sop, update.Value);
                            //updatedProperties.UnionWith(propertiesWithSyncInfoUpdated);

                            HashSet<string> syncIDs = null;
                            try
                            {
                                syncIDs = m_SyncInfoManager.GetLastUpdatedSyncIDs(uuid, updatedProperties);

                                /*
                                //Log encoding delays
                                if (tickLog)
                                {
                                    DateTime encodeEndTime = DateTime.Now;
                                    TimeSpan span = encodeEndTime - startTime;
                                    m_updateLoopLogSB.Append(",update-" +updateIndex+"," + span.TotalMilliseconds.ToString());
                                }
                                 * */

                                SyncMsgUpdatedProperties msg = new SyncMsgUpdatedProperties(this, uuid, updatedProperties);

                                /*
                                //Log encoding delays
                                if (tickLog)
                                {
                                    DateTime syncMsgendTime = DateTime.Now;
                                    TimeSpan span = syncMsgendTime - startTime;
                                    m_updateLoopLogSB.Append("," + span.TotalMilliseconds.ToString());
                                }
                                 * */ 

                                HashSet<SyncConnector> syncConnectors = GetSyncConnectorsForUpdates();
                                // m_log.WarnFormat("{0} SendUpdateToRelevantSyncConnectors: Sending update msg to {1} connectors", LogHeader, syncConnectors.Count);
                                foreach (SyncConnector connector in syncConnectors)
                                {
                                    //If the updated properties are from the same actor, the no need to send this sync message to that actor
                                    if (syncIDs.Count == 1)
                                    {
                                        if (syncIDs.Contains(connector.otherSideActorID))
                                        {
                                            //m_log.DebugFormat("Skip sending to {0}", connector.otherSideActorID);
                                            continue;
                                        }

                                    }
                                    else
                                    {
                                        //debug
                                        /*
                                        string logstr="";
                                        foreach (string sid in syncIDs)
                                        {
                                            logstr += sid+",";
                                        }
                                        m_log.DebugFormat("Updates from {0}", logstr);
                                         * */
                                    }
                                    // Prepare the data for output. If more updated properties are added later,
                                    //     the data is rebuilt. Calling this here means the conversion is usually done on this
                                    //     worker thread and not the send thread and that log messages have the correct len.
                                    msg.ConvertOut(this);
                                    connector.EnqueueOutgoingUpdate(uuid, msg);
                                }

                                /*
                                //Log encoding delays
                                if (tickLog)
                                {
                                    DateTime syncConnectorendTime = DateTime.Now;
                                    TimeSpan span = syncConnectorendTime - startTime;
                                    m_updateLoopLogSB.Append("," + span.TotalMilliseconds.ToString());
                                }
                                 * */
                            }
                            catch (Exception e)
                            {
                                m_log.ErrorFormat("{0} Error in EncodeProperties for {1}: {2}", LogHeader, uuid, e);
                            }
                            
                            updateIndex++;
                            
                        }
                    }

                    /*
                    if (tickLog)
                    {
                        DateTime endTime = DateTime.Now;
                        TimeSpan span = endTime - startTime;
                        m_updateLoopLogSB.Append(", total-span " + span.TotalMilliseconds.ToString());
                        //m_log.InfoFormat("SyncOutUpdates - tick {0}: END the thread for SyncOutUpdates, time span {1}",
                        //    m_updateTick, span.Milliseconds);
                    }
                     * */ 

                    // Indicate that the current batch of updates has been completed
                    Interlocked.Exchange(ref m_sendingPropertyUpdates, 0);
                });
            }

            CheckTerrainTainted();
        }


        #endregion //Prim Property Sync management

        #region Presence Property Sync management

        private void OnAvatarAppearanceChange(ScenePresence sp)
        {
            // If the scene presence update event was triggered by a call from RegionSyncModule, then we don't need to handle it.
            // Changes to scene presence that are actually local will not have originated from this module or thread.
            if (IsLocallyGeneratedEvent(SyncMsg.MsgType.UpdatedProperties))
                return;
            UUID uuid = sp.UUID;
            
            // Sync values with SP data and update timestamp according, to 
            // obtain the list of properties that really have been updated
            // and should be propogated to other sync nodes.
            HashSet<SyncableProperties.Type> propertiesWithSyncInfoUpdated = m_SyncInfoManager.UpdateSyncInfoByLocal(uuid, new HashSet<SyncableProperties.Type>() { SyncableProperties.Type.AvatarAppearance });
            
            //Enqueue the set of changed properties
            EnqueueUpdatedProperty(uuid, propertiesWithSyncInfoUpdated);
        }

        private void OnScenePresenceUpdated(ScenePresence sp)
        {
            // If the scene presence update event was triggered by a call from RegionSyncModule, then we don't need to handle it.
            // Changes to scene presence that are actually local will not have originated from this module or thread.
            if (IsLocallyGeneratedEvent(SyncMsg.MsgType.UpdatedProperties))
                return;

            UUID uuid = sp.UUID;
            //m_log.Warn("OnScenePresenceUpdated A");

            // Sync values with SP data and update timestamp according, to 
            // obtain the list of properties that really have been updated
            // and should be propogated to other sync nodes.
            //HashSet<SyncableProperties.Type> propertiesWithSyncInfoUpdated = m_SyncInfoManager.UpdateSyncInfoByLocal(uuid, SyncableProperties.AvatarProperties);
            HashSet<SyncableProperties.Type> propertiesWithSyncInfoUpdated = m_SyncInfoManager.UpdateSyncInfoByLocal(uuid, SyncableProperties.AvatarSyncableProperties); 

            // string types = "";
            //foreach(SyncableProperties.Type t in propertiesWithSyncInfoUpdated)
            //    types += (t.ToString() + ",");
            //m_log.WarnFormat("OnScenePresenceUpdated B {0}", types);
                

            //Enqueue the set of changed properties
            EnqueueUpdatedProperty(uuid, propertiesWithSyncInfoUpdated);
            //m_log.Warn("OnScenePresenceUpdated C");
        }

        #endregion //Presence Property Sync management

        public void EnqueueUpdatedProperty(UUID uuid, HashSet<SyncableProperties.Type> updatedProperties)
        {
            // m_log.WarnFormat("{0} EnqueueUpdatedProperty: propertiesWithSyncInfoUpdated.Count = {1}", LogHeader, updatedProperties.Count);
            if (updatedProperties.Count == 0)
                return;

            lock (m_propertyUpdateLock)
            {
                if (!m_propertyUpdates.ContainsKey(uuid))
                    m_propertyUpdates.Add(uuid, updatedProperties);
                else
                    // No need to check if the property is already in the hash set.
                    //foreach (SyncableProperties.Type property in updatedProperties)
                    //    m_propertyUpdates[uuid].Add(property);
                    m_propertyUpdates[uuid].UnionWith(updatedProperties);
            }
        }
    }

    ///////////////////////////////////////////////////////////////////////////
    // 
    ///////////////////////////////////////////////////////////////////////////

    public enum PropertyUpdateSource
    {
        Local,
        BySync
    }
}
