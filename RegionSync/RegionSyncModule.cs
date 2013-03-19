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
            Scene.EventManager.OnAvatarAppearanceChange         += OnScenePresenceUpdated;

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
            // Would only want to clear regions for non-persistence actors. Not a problem if using null storage. 
            //Scene.DeleteAllSceneObjects();
            SyncStart(null);
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

        private class EventsReceived
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
            public SymmetricSyncMessage SyncMessage;
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

            OSDMap data = new OSDMap(3);
            data["terrain"] = OSD.FromString(terrain);
            data["actorID"] = OSD.FromString(TerrainSyncInfo.LastUpdateActorID);
            data["timeStamp"] = OSD.FromLong(TerrainSyncInfo.LastUpdateTimeStamp);

            //m_log.DebugFormat("{0}: Ready to send terrain update with lastUpdateTimeStamp {1} and lastUpdateActorID {2}", LogHeader, lastUpdateTimeStamp, lastUpdateActorID);

            SymmetricSyncMessage syncMsg = new SymmetricSyncMessage(SymmetricSyncMessage.MsgType.Terrain, data);
            SendTerrainUpdateToRelevantSyncConnectors(syncMsg, TerrainSyncInfo.LastUpdateActorID);
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
                // If we are syncing with other nodes, send out the message
                OSDMap data = EncodeScenePresence(sp);
                SymmetricSyncMessage syncMsg = new SymmetricSyncMessage(SymmetricSyncMessage.MsgType.NewPresence, data);
                m_log.DebugFormat("{0}: Send NewPresence message for {1} ({2})", LogHeader, sp.Name, sp.UUID);
                SendSpecialUpdateToRelevantSyncConnectors(ActorID, "SndNewPres", uuid, syncMsg);
            }
        }

        private void OnRemovePresence(UUID uuid)
        {
            // m_log.WarnFormat("{0} OnRemovePresence called for {1}", LogHeader, uuid);
            // First, remove from SyncInfoManager's record.
            m_SyncInfoManager.RemoveSyncInfo(uuid);

            OSDMap data = new OSDMap();
            data["uuid"] = OSD.FromUUID(uuid);
            SymmetricSyncMessage syncMsg = new SymmetricSyncMessage(SymmetricSyncMessage.MsgType.RemovedPresence, data);

            SendSpecialUpdateToRelevantSyncConnectors(ActorID, "SndRemPres", uuid, syncMsg);
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
                OSDMap data = EncodeSceneObject(sog);
                SymmetricSyncMessage syncMsg = new SymmetricSyncMessage(SymmetricSyncMessage.MsgType.NewObject, data);
                m_log.DebugFormat("{0}: Send NewObject message for {1} ({2})", LogHeader, sog.Name, sog.UUID);
                SendSpecialUpdateToRelevantSyncConnectors(ActorID, "SndNewObjj", uuid, syncMsg);
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

            OSDMap data = new OSDMap();
            data["uuid"] = OSD.FromUUID(sog.UUID);
            //TODO: need to put in SyncID instead of ActorID here. 
            //For now, keep it the same for simple debugging
            data["actorID"] = OSD.FromString(ActorID);
            data["softDelete"] = OSD.FromBoolean(false); // softDelete

            //m_log.DebugFormat("{0}: Send DeleteObject out for {1},{2}", Scene.RegionInfo.RegionName, sog.Name, sog.UUID);

            SymmetricSyncMessage rsm = new SymmetricSyncMessage(SymmetricSyncMessage.MsgType.RemovedObject, data);
            SendSpecialUpdateToRelevantSyncConnectors(ActorID, "SndRemObjj", sog.UUID, rsm);
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


            if (!IsSyncingWithOtherSyncNodes())
            {
                //no SyncConnector connected. Do nothing.
                return;
            }

            //Now encode the linkedGroup for sync
            OSDMap data = new OSDMap();
            OSDMap encodedSOG = EncodeSceneObject(linkedGroup);
            data["linkedGroup"] = encodedSOG;
            data["rootID"] = OSD.FromUUID(root.UUID);
            data["partCount"] = OSD.FromInteger(children.Count);
            data["actorID"] = OSD.FromString(ActorID);
            int partNum = 0;

            string debugString = "";
            foreach (SceneObjectPart part in children)
            {
                string partTempID = "part" + partNum;
                data[partTempID] = OSD.FromUUID(part.UUID);
                partNum++;

                //m_log.DebugFormat("{0}: SendLinkObject to link {1},{2} with {3}, {4}", part.Name, part.UUID, root.Name, root.UUID);
                debugString += part.UUID + ", ";
            }

            m_log.DebugFormat("SyncLinkObject: SendLinkObject to link parts {0} with {1}, {2}", debugString, root.Name, root.UUID);

            SymmetricSyncMessage rsm = new SymmetricSyncMessage(SymmetricSyncMessage.MsgType.LinkObject, data);
            SendSpecialUpdateToRelevantSyncConnectors(ActorID, "SndLnkObjj", root.UUID, rsm);
            //SendSceneEventToRelevantSyncConnectors(ActorID, rsm, linkedGroup);
        }

        private void SyncDeLinkObject(List<SceneObjectPart> prims, List<SceneObjectGroup> beforeDelinkGroups, List<SceneObjectGroup> afterDelinkGroups)
        {
            if (prims.Count == 0 || beforeDelinkGroups.Count == 0) return;

            //the prims are just delinked, each part has quite some properties changed 
            //(OffsetPosition, etc). Need to sync the property values in SyncInfoManager
            //first
            foreach (SceneObjectPart part in prims)
            {
                m_SyncInfoManager.UpdateSyncInfoByLocal(part.UUID, part.PhysActor == null ? SyncableProperties.NonPhysActorProperties : SyncableProperties.FullUpdateProperties);
            }


            if (!IsSyncingWithOtherSyncNodes())
            {
                //no SyncConnector connected. Do nothing.
                return;
            }

            OSDMap data = new OSDMap();
            data["partCount"] = OSD.FromInteger(prims.Count);
            int partNum = 0;
            foreach (SceneObjectPart part in prims)
            {
                string partTempID = "part" + partNum;
                data[partTempID] = OSD.FromUUID(part.UUID);
                partNum++;
            }
            //We also include the IDs of beforeDelinkGroups, for now it is more for sanity checking at the receiving end, so that the receiver 
            //could make sure its delink starts with the same linking state of the groups/prims.
            data["beforeGroupsCount"] = OSD.FromInteger(beforeDelinkGroups.Count);
            int groupNum = 0;
            foreach (SceneObjectGroup affectedGroup in beforeDelinkGroups)
            {
                string groupTempID = "beforeGroup" + groupNum;
                data[groupTempID] = OSD.FromUUID(affectedGroup.UUID);
                groupNum++;
            }

            //include the property values of each object after delinking, for synchronizing the values
            data["afterGroupsCount"] = OSD.FromInteger(afterDelinkGroups.Count);
            groupNum = 0;
            foreach (SceneObjectGroup afterGroup in afterDelinkGroups)
            {
                string groupTempID = "afterGroup" + groupNum;
                //string sogxml = SceneObjectSerializer.ToXml2Format(afterGroup);
                //data[groupTempID] = OSD.FromString(sogxml);
                OSDMap encodedSOG = EncodeSceneObject(afterGroup);
                data[groupTempID] = encodedSOG;
                groupNum++;
            }

            SymmetricSyncMessage rsm = new SymmetricSyncMessage(SymmetricSyncMessage.MsgType.DelinkObject, data);
            SendDelinkObjectToRelevantSyncConnectors(ActorID, beforeDelinkGroups, rsm);
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
            Command cmdSyncStart = new Command("start", CommandIntentions.COMMAND_HAZARDOUS, SyncStart, "Begins synchronization with RegionSyncServer.");
            //cmdSyncStart.AddArgument("server_address", "The IP address of the server to synchronize with", "String");
            //cmdSyncStart.AddArgument("server_port", "The port of the server to synchronize with", "Integer");

            Command cmdSyncStop = new Command("stop", CommandIntentions.COMMAND_HAZARDOUS, SyncStop, "Stops synchronization with RegionSyncServer.");
            //cmdSyncStop.AddArgument("server_address", "The IP address of the server to synchronize with", "String");
            //cmdSyncStop.AddArgument("server_port", "The port of the server to synchronize with", "Integer");

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


            m_commander.RegisterCommand("start", cmdSyncStart);
            m_commander.RegisterCommand("stop", cmdSyncStop);
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

        private TerrainSyncInfo TerrainSyncInfo { get; set; }

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

        private void SendTerrainUpdateToRelevantSyncConnectors(SymmetricSyncMessage syncMsg, string lastUpdateActorID)
        {
            List<SyncConnector> syncConnectors = GetSyncConnectorsForSceneEvents(lastUpdateActorID, syncMsg, null);

            foreach (SyncConnector connector in syncConnectors)
            {
                //m_log.WarnFormat("{0}: Send terrain update to {1}", LogHeader, connector.otherSideActorID);
                DetailedUpdateWrite("SndTerrUpd", m_zeroUUID, 0, m_zeroUUID, connector.otherSideActorID, syncMsg.Length);
                connector.ImmediateOutgoingMsg(syncMsg);
            }
        }

        //ScenePresence updates are sent by enqueuing into each connector's outQueue.
        // UNUSED??
        private void SendAvatarUpdateToRelevantSyncConnectors(ScenePresence sp, SymmetricSyncMessage syncMsg)
        {
            HashSet<SyncConnector> syncConnectors = GetSyncConnectorsForUpdates();

            foreach (SyncConnector connector in syncConnectors)
                connector.EnqueueOutgoingUpdate(sp.UUID, syncMsg);
        }

        //Object updates are sent by enqueuing into each connector's outQueue.
        // UNUSED??
        private void SendObjectUpdateToRelevantSyncConnectors(SceneObjectGroup sog, SymmetricSyncMessage syncMsg)
        {
            HashSet<SyncConnector> syncConnectors = GetSyncConnectorsForUpdates();

            foreach (SyncConnector connector in syncConnectors)
            {
                //m_log.Debug("Send " + syncMsg.Type.ToString() + " about sog "+sog.Name+","+sog.UUID+ " at pos "+sog.AbsolutePosition.ToString()+" to " + connector.OtherSideActorID);
                connector.EnqueueOutgoingUpdate(sog.UUID, syncMsg);
            }
        }

        //Object updates are sent by enqueuing into each connector's outQueue.
        // UNUSED??
        /*
        private void SendPrimUpdateToRelevantSyncConnectors(SceneObjectPart updatedPart, SymmetricSyncMessage syncMsg, string lastUpdateActorID)
        {
            HashSet<SyncConnector> syncConnectors = GetSyncConnectorsForUpdates();

            foreach (SyncConnector connector in syncConnectors)
            {
                //m_log.Debug("Send " + syncMsg.Type.ToString() + " about sop " + updatedPart.Name + "," + updatedPart.UUID + " at pos "+updatedPart.GroupPosition.ToString()
                //+" to " + connector.OtherSideActorID);

                if (!connector.otherSideActorID.Equals(lastUpdateActorID))
                {
                    connector.EnqueueOutgoingUpdate(updatedPart.UUID, syncMsg);
                }
            }
        }
         * */ 

        private void SendDelinkObjectToRelevantSyncConnectors(string senderActorID, List<SceneObjectGroup> beforeDelinkGroups, SymmetricSyncMessage syncMsg)
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
                        DetailedUpdateWrite("DelinkObjj", sog.UUID, 0, m_zeroUUID, connector.otherSideActorID, syncMsg.Length);
                        connector.EnqueueOutgoingUpdate(sog.UUID, syncMsg);
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
        private void SendSpecialUpdateToRelevantSyncConnectors(string init_actorID, string logReason, UUID sendingUUID, SymmetricSyncMessage syncMsg)
        {
            HashSet<SyncConnector> syncConnectors = GetSyncConnectorsForUpdates();

            foreach (SyncConnector connector in syncConnectors)
            {
                if (!connector.otherSideActorID.Equals(init_actorID))
                {
                    DetailedUpdateWrite(logReason, sendingUUID, 0, m_zeroUUID, connector.otherSideActorID, syncMsg.Length);
                    connector.ImmediateOutgoingMsg(syncMsg);
                }
            }
        }

        //Events are send out right away, without being put into the connector's outQueue first. 
        //May need a better method for managing the outgoing messages (i.e. prioritizing object updates and events)
        private void SendSceneEventToRelevantSyncConnectors(string init_actorID, SymmetricSyncMessage rsm, SceneObjectGroup sog)
        {
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
                    DetailedUpdateWrite("SndEventtt", sog == null ? m_zeroUUID : sog.UUID.ToString(), 0, rsm.Type.ToString(), connector.otherSideActorID, rsm.Length);
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
        private List<SyncConnector> GetSyncConnectorsForSceneEvents(string init_actorID, SymmetricSyncMessage rsm, SceneObjectGroup sog)
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

        /*
        private void OnNewPresence(ScenePresence avatar)
        {
            //Go through the objects, if any of them are attachments of the
            //new avatar, link them.
            //DSL Warning, this can make adding a new avatar very slow if there are a lot of prims to iterate through
            EntityBase[] entities = Scene.GetEntities();
            foreach (EntityBase e in entities)
            {
                if (e is SceneObjectGroup)
                {
                    SceneObjectGroup sog = (SceneObjectGroup)e;
                    if (sog.AttachedAvatar == avatar.UUID)
                    {
                        //Scene.AttachObjectBySync(avatar, sog);
                        Scene.AttachmentsModule.AttachObject(avatar, sog, sog.AttachmentPoint, false);
                    }
                }
            }

        }
         */ 

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

        //Start SyncListener if a listener is supposed to run on this actor; Otherwise, initiate connections to remote listeners.
        private void SyncStart(Object[] args)
        {
            if (m_isSyncListenerLocal)
            {
                m_log.Warn(LogHeader + " SyncStart - Sync listener is local");
                if (m_localSyncListener!=null && m_localSyncListener.IsListening)
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
                m_log.Warn(LogHeader + " SyncStart - Sync listener is remote");
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

        private void SyncStop(Object[] args)
        {
            if (m_isSyncListenerLocal)
            {
                if (m_localSyncListener!=null && m_localSyncListener.IsListening)
                {
                    m_localSyncListener.Shutdown();
                    //Trigger SyncStop event, ActorSyncModules can then take actor specific action if needed.
                    //For instance, script engine will save script states
                    //save script state and stop script instances
                    //Scene.EventManager.TriggerOnSymmetricSyncStop();
                }
                m_synced = true;
            }
            else
            {
                //Shutdown all sync connectors
                if (m_synced)
                {
                    StopAllSyncConnectors();
                    m_synced = false;

                    //Trigger SyncStop event, ActorSyncModules can then take actor specific action if needed.
                    //For instance, script engine will save script states
                    //save script state and stop script instances
                    //Scene.EventManager.TriggerOnSymmetricSyncStop();
                }
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
                SymmetricSyncMessage msg = new SymmetricSyncMessage(SymmetricSyncMessage.MsgType.SyncStateReport);
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

            m_log.DebugFormat("{0}: new connector {1}", LogHeader, syncConnector.ConnectorNum);
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

        private void StopAllSyncConnectors()
        {
            lock (m_syncConnectorsLock)
            {
                foreach (SyncConnector syncConnector in m_syncConnectors)
                {
                    syncConnector.Shutdown();
                }

                m_syncConnectors.Clear();
            }
        }

        private bool IsSyncingWithOtherSyncNodes()
        {
            return (m_syncConnectors.Count > 0);
        }

        private void DoInitialSync()
        {
            Scene.DeleteAllSceneObjects();
            
            SendSyncMessage(SymmetricSyncMessage.MsgType.ActorID, ActorID);
            // SendSyncMessage(SymmetricSyncMessage.MsgType.ActorType, ActorType.ToString());
            // SendSyncMessage(SymmetricSyncMessage.MsgType.SyncID, m_syncID);

            // message sent to help calculating the difference in the clocks
            SendSyncMessage(SymmetricSyncMessage.MsgType.TimeStamp, DateTime.UtcNow.Ticks.ToString());

            SendSyncMessage(SymmetricSyncMessage.MsgType.RegionName, Scene.RegionInfo.RegionName);
            m_log.WarnFormat("Sending region name: \"{0}\"", Scene.RegionInfo.RegionName);

            SendSyncMessage(SymmetricSyncMessage.MsgType.GetRegionInfo);
            SendSyncMessage(SymmetricSyncMessage.MsgType.GetTerrain);
            SendSyncMessage(SymmetricSyncMessage.MsgType.GetPresences);
            SendSyncMessage(SymmetricSyncMessage.MsgType.GetObjects);

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
        /// <param name="msgType"></param>
        /// <param name="data"></param>
        private void SendSyncMessage(SymmetricSyncMessage.MsgType msgType, string data)
        {
            //See RegionSyncClientView for initial implementation by Dan Lake

            SymmetricSyncMessage msg = new SymmetricSyncMessage(msgType, data);
            ForEachSyncConnector(delegate(SyncConnector syncConnector)
            {
                DetailedUpdateWrite("SndSyncMsg", msgType.ToString(), 0, m_zeroUUID, syncConnector.otherSideActorID, msg.Length);
                syncConnector.ImmediateOutgoingMsg(msg);
            });
        }

        private void SendSyncMessage(SymmetricSyncMessage.MsgType msgType)
        {
            //See RegionSyncClientView for initial implementation by Dan Lake

            SendSyncMessage(msgType, "");
        }

        private void ForEachSyncConnector(Action<SyncConnector> action)
        {
            List<SyncConnector> closed = null;
            foreach (SyncConnector syncConnector in m_syncConnectors)
            {
                // If connected, apply the action
                if (syncConnector.connected)
                {
                    action(syncConnector);
                }                
                    // Else, remove the SyncConnector from the list
                else
                {
                    if (closed == null)
                        closed = new List<SyncConnector>();
                    closed.Add(syncConnector);
                }
            }

            if (closed != null)
            {
                foreach (SyncConnector connector in closed)
                {
                    RemoveSyncConnector(connector);
                }
            }
        }

        #region Sync message handlers

        /// <summary>
        /// The handler for processing incoming sync messages.
        /// </summary>
        /// <param name="msg"></param>
        /// <param name="senderActorID">ActorID of the sender</param>
        public void HandleIncomingMessage(SymmetricSyncMessage msg, string senderActorID, SyncConnector syncConnector)
        {
            // m_log.WarnFormat("{0} HandleIncomingMessage: {1}", LogHeader, msg.ToString());
            lock (m_stats) m_statMsgsIn++;
            //Added senderActorID, so that we don't have to include actorID in sync messages -- TODO
            switch (msg.Type)
            {
                case SymmetricSyncMessage.MsgType.UpdatedProperties:
                    HandleUpdatedProperties(msg, senderActorID);
                    break;
                case SymmetricSyncMessage.MsgType.GetRegionInfo:
                    HandleGetRegionInfo(syncConnector);
                    break;
                case SymmetricSyncMessage.MsgType.RegionInfo:
                    HandleRegionInfoMessage(msg, senderActorID);
                    break;
                case SymmetricSyncMessage.MsgType.GetTerrain:
                    HandleGetTerrainRequest(syncConnector);
                    break;
                case SymmetricSyncMessage.MsgType.Terrain:
                    HandleTerrainUpdateMessage(msg, senderActorID);
                    break;
                case SymmetricSyncMessage.MsgType.GetObjects:
                    HandleGetObjectsRequest(syncConnector);
                    break;
                case SymmetricSyncMessage.MsgType.GetPresences:
                    HandleGetPresencesRequest(syncConnector);
                    break;
                case SymmetricSyncMessage.MsgType.NewObject:
                    HandleSyncNewObject(msg, senderActorID);
                    break;
                case SymmetricSyncMessage.MsgType.RemovedObject:
                    HandleRemovedObject(msg, senderActorID);
                    break;
                case SymmetricSyncMessage.MsgType.LinkObject:
                    HandleSyncLinkObject(msg, senderActorID);
                    break;
                case SymmetricSyncMessage.MsgType.DelinkObject:
                    HandleSyncDelinkObject(msg, senderActorID);
                    break;
                case SymmetricSyncMessage.MsgType.NewPresence:
                    HandleSyncNewPresence(msg, senderActorID);
                    break;
                case SymmetricSyncMessage.MsgType.RemovedPresence:
                    HandleRemovedPresence(msg, senderActorID);
                    break;
                    //EVENTS PROCESSING
                case SymmetricSyncMessage.MsgType.NewScript:
                case SymmetricSyncMessage.MsgType.UpdateScript:
                case SymmetricSyncMessage.MsgType.ScriptReset:
                case SymmetricSyncMessage.MsgType.ChatFromClient:
                case SymmetricSyncMessage.MsgType.ChatFromWorld:
                case SymmetricSyncMessage.MsgType.ChatBroadcast:
                case SymmetricSyncMessage.MsgType.ObjectGrab:
                case SymmetricSyncMessage.MsgType.ObjectGrabbing:
                case SymmetricSyncMessage.MsgType.ObjectDeGrab:
                case SymmetricSyncMessage.MsgType.Attach:
                case SymmetricSyncMessage.MsgType.PhysicsCollision:
                case SymmetricSyncMessage.MsgType.ScriptCollidingStart:
                case SymmetricSyncMessage.MsgType.ScriptColliding:
                case SymmetricSyncMessage.MsgType.ScriptCollidingEnd:
                case SymmetricSyncMessage.MsgType.ScriptLandCollidingStart:
                case SymmetricSyncMessage.MsgType.ScriptLandColliding:
                case SymmetricSyncMessage.MsgType.ScriptLandCollidingEnd:
                    HandleRemoteEvent(msg, senderActorID);
                    break;
                case SymmetricSyncMessage.MsgType.SyncStateReport:
                    SyncStateDetailReport(null);
                    break;
                default:
                    break;
            }
        }

        private void HandleTerrainUpdateMessage(SymmetricSyncMessage msg, string senderActorID)
        {
            // Get the data from message and error check
            OSDMap data = DeserializeMessage(msg);

            if (data == null)
            {
                SymmetricSyncMessage.HandleError(LogHeader, msg, "Could not deserialize " + msg.Type.ToString());
                return;
            }

            string terrain = data["terrain"].AsString();
            long lastUpdateTimeStamp = data["timeStamp"].AsLong();
            string lastUpdateActorID = data["actorID"].AsString();

            DetailedUpdateWrite("RcvTerrain", m_zeroUUID, lastUpdateTimeStamp, m_zeroUUID, senderActorID, msg.Length);

            //update the terrain if the incoming terrain data has a more recent timestamp
            TerrainSyncInfo.UpdateTerrianBySync(lastUpdateTimeStamp, lastUpdateActorID, terrain);
        }

        ///////////////////////////////////////////////////////////////////////
        // Per property sync handlers
        ///////////////////////////////////////////////////////////////////////
        void estate_OnRegionInfoChange(UUID regionID)
        {
            if (regionID != Scene.RegionInfo.RegionID)
                return;
            if(IsLocallyGeneratedEvent(SymmetricSyncMessage.MsgType.RegionInfo, null))
                return;
            SymmetricSyncMessage msg = new SymmetricSyncMessage(SymmetricSyncMessage.MsgType.RegionInfo, GetRegionInfo());
            foreach (SyncConnector connector in GetSyncConnectorsForUpdates())
            {
                DetailedUpdateWrite("SndRgnInfo", m_zeroUUID, 0, m_zeroUUID, connector.otherSideActorID, msg.Length);
                connector.ImmediateOutgoingMsg(msg);
            }
        }
        
        private void HandleGetRegionInfo(SyncConnector connector)
        {
            DetailedUpdateWrite("RcvInfoReq", m_zeroUUID, 0, m_zeroUUID, connector.otherSideActorID, 0);
            SymmetricSyncMessage msg = new SymmetricSyncMessage(SymmetricSyncMessage.MsgType.RegionInfo, GetRegionInfo());
            DetailedUpdateWrite("SndRgnInfo", m_zeroUUID, 0, m_zeroUUID, connector.otherSideActorID, msg.Length);
            connector.ImmediateOutgoingMsg(msg);
        }
        
        private void HandleRegionInfoMessage(SymmetricSyncMessage msg, string senderActorID)
        {
            OSDMap data = DeserializeMessage(msg);
            RememberLocallyGeneratedEvent(SymmetricSyncMessage.MsgType.RegionInfo);
            SetRegionInfo(data);
            ForgetLocallyGeneratedEvent();
        }

        private OSDMap GetRegionInfo()
        {
            OSDMap data = new OSDMap(5);
            data["tex1"] = OSD.FromUUID(Scene.RegionInfo.RegionSettings.TerrainTexture1);
            data["tex2"] = OSD.FromUUID(Scene.RegionInfo.RegionSettings.TerrainTexture2);
            data["tex3"] = OSD.FromUUID(Scene.RegionInfo.RegionSettings.TerrainTexture3);
            data["tex4"] = OSD.FromUUID(Scene.RegionInfo.RegionSettings.TerrainTexture4);
            data["waterheight"] = OSD.FromReal(Scene.RegionInfo.RegionSettings.WaterHeight);
            return data;
        }

        private void SetRegionInfo(OSDMap data)
        {
            Scene.RegionInfo.RegionSettings.TerrainTexture1 = data["tex1"].AsUUID();
            Scene.RegionInfo.RegionSettings.TerrainTexture2 = data["tex2"].AsUUID();
            Scene.RegionInfo.RegionSettings.TerrainTexture3 = data["tex3"].AsUUID();
            Scene.RegionInfo.RegionSettings.TerrainTexture4 = data["tex4"].AsUUID();
            Scene.RegionInfo.RegionSettings.WaterHeight = data["waterheight"].AsReal();
            IEstateModule estate = Scene.RequestModuleInterface<IEstateModule>();
            if (estate != null)
                estate.sendRegionHandshakeToAll();
        }

        private void HandleGetTerrainRequest(SyncConnector connector)
        {
            DetailedUpdateWrite("RcvTerrReq", m_zeroUUID, TerrainSyncInfo.LastUpdateTimeStamp, m_zeroUUID, connector.otherSideActorID, 0);

            OSDMap data = new OSDMap(3);
            data["terrain"] = OSD.FromString((string)TerrainSyncInfo.LastUpdateValue);
            data["actorID"] = OSD.FromString(TerrainSyncInfo.LastUpdateActorID);
            data["timeStamp"] = OSD.FromLong(TerrainSyncInfo.LastUpdateTimeStamp);

            SymmetricSyncMessage syncMsg = new SymmetricSyncMessage(SymmetricSyncMessage.MsgType.Terrain, data);
            DetailedUpdateWrite("SndTerrRsp", m_zeroUUID, 0, m_zeroUUID, connector.otherSideActorID, syncMsg.Length);
            connector.ImmediateOutgoingMsg(syncMsg);
        }

        private void HandleGetPresencesRequest(SyncConnector connector)
        {
            DetailedUpdateWrite("RcvGetPres", m_zeroUUID, 0, m_zeroUUID, connector.otherSideActorID, 0);
            EntityBase[] entities = Scene.GetEntities();
            foreach (EntityBase e in entities)
            {
                if (e is ScenePresence)
                {
                    ScenePresence sp = (ScenePresence)e;

                    // This will sync the appearance that's currently in the agent circuit data.
                    // If the avatar has updated their appearance since they connected, the original data will still be in ACD.
                    // The ACD normally only gets updated when an avatar is moving between regions.
                    OSDMap data = EncodeScenePresence(sp);
                    SymmetricSyncMessage syncMsg = new SymmetricSyncMessage(SymmetricSyncMessage.MsgType.NewPresence, data);
                    m_log.DebugFormat("{0}: Send NewPresence message for {1} ({2})", LogHeader, sp.Name, sp.UUID);
                    DetailedUpdateWrite("SndGetPReq", sp.UUID, 0, m_zeroUUID, connector.otherSideActorID, syncMsg.Length);
                    connector.ImmediateOutgoingMsg(syncMsg);
                }
            }
        }

        private void HandleGetObjectsRequest(SyncConnector connector)
        {
            DetailedUpdateWrite("RcvGetObjj", m_zeroUUID, 0, m_zeroUUID, connector.otherSideActorID, 0);
            Scene.ForEachSOG(delegate(SceneObjectGroup sog)
            {
                OSDMap encodedSOG = EncodeSceneObject(sog);
                SymmetricSyncMessage syncMsg = new SymmetricSyncMessage(SymmetricSyncMessage.MsgType.NewObject, encodedSOG);
                DetailedUpdateWrite("SndGetORsp", sog.UUID, 0, m_zeroUUID, connector.otherSideActorID, syncMsg.Length);
                connector.EnqueueOutgoingUpdate(sog.UUID, syncMsg);
            });
        } 

        private void HandleSyncNewObject(SymmetricSyncMessage msg, string senderActorID)
        {
            // m_log.DebugFormat("{0}: HandleSyncNewObject called", LogHeader);
            OSDMap data = DeserializeMessage(msg);

            if (data == null)
            {
                SymmetricSyncMessage.HandleError(LogHeader, msg, "Could not deserialize " + msg.Type.ToString());
                return;
            }

            // Decode group and syncInfo from message data
            SceneObjectGroup group;
            Dictionary<UUID, SyncInfoBase> syncInfos;
            if (!DecodeSceneObject(data, out group, out syncInfos))
            {
                m_log.WarnFormat("{0}: Failed to decode scene object in HandleSyncNewObject", LogHeader);
                return;
            }

            if (group.RootPart.Shape == null)
            {
                m_log.WarnFormat("{0}: group.RootPart.Shape is null", LogHeader);
            }

            DetailedUpdateWrite("RecNewObjj", group.UUID, 0, m_zeroUUID, senderActorID, msg.Length);

            // If this is a relay node, forward the message
            if (IsSyncRelay)
                SendSpecialUpdateToRelevantSyncConnectors(senderActorID, "SndNewObjR", group.UUID, msg);

            //Add the list of PrimSyncInfo to SyncInfoManager
            foreach (SyncInfoBase syncInfo in syncInfos.Values)
                m_SyncInfoManager.InsertSyncInfo(syncInfo.UUID, syncInfo);

            // Add the decoded object to Scene
            // This will invoke OnObjectAddedToScene but the syncinfo has already been created so that's a NOP
            Scene.AddNewSceneObject(group, true);

            // If it's an attachment, connect this to the presence
            if (group.IsAttachmentCheckFull())
            {
                //m_log.WarnFormat("{0}: HandleSyncNewObject: Adding attachement to presence", LogHeader);
                ScenePresence sp = Scene.GetScenePresence(group.AttachedAvatar);
                if (sp != null)
                {
                    sp.AddAttachment(group);
                    group.RootPart.SetParentLocalId(sp.LocalId);

                    // In case it is later dropped, don't let it get cleaned up
                    group.RootPart.RemFlag(PrimFlags.TemporaryOnRez);

                    group.HasGroupChanged = true;
                }
                
            }

            /* Uncomment when quarks exist
            //If we just keep a copy of the object in our local Scene,
            //and is not supposed to operation on it (e.g. object in 
            //passive quarks), then ignore the event.
            if (!ToOperateOnObject(group))
                return;
             */

            // Now that (if) the PhysActor of each part in sog has been created, set the PhysActor properties.
            if (group.RootPart.PhysActor != null)
            {
                foreach (SyncInfoBase syncInfo in syncInfos.Values)
                {
                    // m_log.DebugFormat("{0}: HandleSyncNewObject: setting physical properties", LogHeader);
                    syncInfo.SetPropertyValues(SyncableProperties.PhysActorProperties);
                }
            }

            group.CreateScriptInstances(0, false, Scene.DefaultScriptEngine, 0);
            group.ResumeScripts();

            // Trigger aggregateScriptEventSubscriptions since it may access PhysActor to link collision events
            foreach (SceneObjectPart part in group.Parts)
                part.aggregateScriptEvents();

            group.ScheduleGroupForFullUpdate();
        }

        private void HandleRemovedObject(SymmetricSyncMessage msg, string senderActorID)
        {
            // Get the data from message and error check
            OSDMap data = DeserializeMessage(msg);

            if (data == null)
            {
                SymmetricSyncMessage.HandleError(LogHeader, msg, "Could not deserialize " + msg.Type.ToString());
                return;
            }

            UUID uuid = data["uuid"].AsUUID();
            bool softDelete = data["softDelete"].AsBoolean();
            DetailedUpdateWrite("RecRemObjj", uuid, 0, m_zeroUUID, senderActorID, msg.Length);

            if (!m_SyncInfoManager.SyncInfoExists(uuid))
                return;

            // If this is a relay node, forward the message
            if (IsSyncRelay)
                SendSpecialUpdateToRelevantSyncConnectors(senderActorID, "SndRemObjR", uuid, msg);

            SceneObjectGroup sog = Scene.GetGroupByPrim(uuid);

            if (sog != null)
            {
                if (!softDelete)
                {
                    //m_log.DebugFormat("{0}: hard delete object {1}", LogHeader, sog.UUID);
                    foreach (SceneObjectPart part in sog.Parts)
                    {
                        m_SyncInfoManager.RemoveSyncInfo(part.UUID);
                    }
                    Scene.DeleteSceneObject(sog, false);
                }
                else
                {
                    //m_log.DebugFormat("{0}: soft delete object {1}", LogHeader, sog.UUID);
                    Scene.UnlinkSceneObject(sog, true);
                }
            }
        }

        private void HandleUpdatedProperties(SymmetricSyncMessage msg, string senderActorID)
        {
            OSDMap data = DeserializeMessage(msg);
            if (data == null)
            {
                m_log.Error("HandleUpdatedProperties could not deserialize message!");
                return;
            }
            UUID uuid = data["uuid"].AsUUID();
            if (uuid == null)
            {
                m_log.Error("HandleUpdatedProperties could not get UUID!");
                return;
            }

            // Decode synced properties from the message
            HashSet<SyncedProperty> syncedProperties = SyncedProperty.DecodeProperties(data);
            if (syncedProperties == null)
            {
                m_log.Error("HandleUpdatedProperties could not get syncedProperties");
                return;
            }

            if (syncedProperties.Count > 0)
            {
                // Update local sync info and scene object/presence
                RememberLocallyGeneratedEvent(msg.Type);
                HashSet<SyncableProperties.Type> propertiesUpdated = m_SyncInfoManager.UpdateSyncInfoBySync(uuid, syncedProperties);
                ForgetLocallyGeneratedEvent();

                DetailedUpdateLogging(uuid, propertiesUpdated, syncedProperties, "RecUpdateN", senderActorID, msg.Data.Length);

                // Relay the update properties
                if (IsSyncRelay)
                    EnqueueUpdatedProperty(uuid, propertiesUpdated);    
            }
        }

        private void HandleSyncLinkObject(SymmetricSyncMessage msg, string senderActorID)
        {

            // Get the data from message and error check
            OSDMap data = DeserializeMessage(msg);
            if (data == null)
            {
                SymmetricSyncMessage.HandleError(LogHeader, msg, "Could not deserialize JSON data.");
                return;
            }

            OSDMap encodedSOG = (OSDMap)data["linkedGroup"];
            SceneObjectGroup linkedGroup;
            Dictionary<UUID, SyncInfoBase> groupSyncInfos;
            if (!DecodeSceneObject(encodedSOG, out linkedGroup, out groupSyncInfos))
            {
                m_log.WarnFormat("{0}: Failed to decode scene object in HandleSyncLinkObject", LogHeader);
                return;
            }
            DetailedUpdateWrite("RecLnkObjj", linkedGroup.UUID, 0, m_zeroUUID, senderActorID, msg.Data.Length);

            //TEMP DEBUG
            // m_log.DebugFormat(" received linkedGroup: {0}", linkedGroup.DebugObjectUpdateResult());
            //m_log.DebugFormat(linkedGroup.DebugObjectUpdateResult());

            if (linkedGroup == null)
            {
                m_log.ErrorFormat("{0}: HandleSyncLinkObject, no valid Linked-Group has been deserialized", LogHeader);
                return;
            }

            UUID rootID = data["rootID"].AsUUID();
            int partCount = data["partCount"].AsInteger();
            List<UUID> childrenIDs = new List<UUID>();

            for (int i = 0; i < partCount; i++)
            {
                string partTempID = "part" + i;
                childrenIDs.Add(data[partTempID].AsUUID());
            }

            // if this is a relay node, forward the message
            if (IsSyncRelay)
            {
                //SendSceneEventToRelevantSyncConnectors(senderActorID, msg, linkedGroup);
                SendSpecialUpdateToRelevantSyncConnectors(senderActorID, "SndLnkObjR", rootID, msg);
            }

            //TEMP SYNC DEBUG
            //m_log.DebugFormat("{0}: received LinkObject from {1}", LogHeader, senderActorID);

            //DSL Scene.LinkObjectBySync(linkedGroup, rootID, childrenIDs);

            //Update properties, if any has changed
            foreach (KeyValuePair<UUID, SyncInfoBase> partSyncInfo in groupSyncInfos)
            {
                UUID uuid = partSyncInfo.Key;
                SyncInfoBase updatedPrimSyncInfo = partSyncInfo.Value;

                SceneObjectPart part = Scene.GetSceneObjectPart(uuid);
                if (part == null)
                {
                    m_log.ErrorFormat("{0}: HandleSyncLinkObject, prim {1} not in local Scene Graph after LinkObjectBySync is called", LogHeader, uuid);
                }
                else
                {
                    m_SyncInfoManager.UpdateSyncInfoBySync(part.UUID, updatedPrimSyncInfo);
                }
            }
        }

        private void HandleSyncDelinkObject(SymmetricSyncMessage msg, string senderActorID)
        {
            OSDMap data = DeserializeMessage(msg);
            if (data == null)
            {
                SymmetricSyncMessage.HandleError(LogHeader, msg, "Could not deserialize JSON data.");
                return;
            }

            //List<SceneObjectPart> localPrims = new List<SceneObjectPart>();
            List<UUID> delinkPrimIDs = new List<UUID>();
            List<UUID> beforeDelinkGroupIDs = new List<UUID>();
            List<SceneObjectGroup> incomingAfterDelinkGroups = new List<SceneObjectGroup>();
            List<Dictionary<UUID, SyncInfoBase>> incomingPrimSyncInfo = new List<Dictionary<UUID, SyncInfoBase>>();

            int partCount = data["partCount"].AsInteger();
            for (int i = 0; i < partCount; i++)
            {
                string partTempID = "part" + i;
                UUID primID = data[partTempID].AsUUID();
                //SceneObjectPart localPart = Scene.GetSceneObjectPart(primID);
                //localPrims.Add(localPart);
                delinkPrimIDs.Add(primID);
            }

            int beforeGroupCount = data["beforeGroupsCount"].AsInteger();
            for (int i = 0; i < beforeGroupCount; i++)
            {
                string groupTempID = "beforeGroup" + i;
                UUID beforeGroupID = data[groupTempID].AsUUID();
                beforeDelinkGroupIDs.Add(beforeGroupID);
            }

            int afterGroupsCount = data["afterGroupsCount"].AsInteger();
            for (int i = 0; i < afterGroupsCount; i++)
            {
                string groupTempID = "afterGroup" + i;
                //string sogxml = data[groupTempID].AsString();
                SceneObjectGroup afterGroup;
                OSDMap encodedSOG = (OSDMap)data[groupTempID];
                Dictionary<UUID, SyncInfoBase> groupSyncInfo;
                if(!DecodeSceneObject(encodedSOG, out afterGroup, out groupSyncInfo))
                {
                    m_log.WarnFormat("{0}: Failed to decode scene object in HandleSyncDelinkObject", LogHeader);
                    return;
                }

                incomingAfterDelinkGroups.Add(afterGroup);
                incomingPrimSyncInfo.Add(groupSyncInfo);
            }

            // if this is a relay node, forward the message
            if (IsSyncRelay)
            {
                List<SceneObjectGroup> beforeDelinkGroups = new List<SceneObjectGroup>();
                foreach (UUID sogID in beforeDelinkGroupIDs)
                {
                    SceneObjectGroup sog = Scene.GetGroupByPrim(sogID);
                    beforeDelinkGroups.Add(sog);
                }
                SendDelinkObjectToRelevantSyncConnectors(senderActorID, beforeDelinkGroups, msg);
            }

            //DSL Scene.DelinkObjectsBySync(delinkPrimIDs, beforeDelinkGroupIDs, incomingAfterDelinkGroups);

            //Sync properties 
            //Update properties, for each prim in each deLinked-Object
            foreach (Dictionary<UUID, SyncInfoBase> primsSyncInfo in incomingPrimSyncInfo)
            {
                foreach (KeyValuePair<UUID, SyncInfoBase> inPrimSyncInfo in primsSyncInfo)
                {
                    UUID uuid = inPrimSyncInfo.Key;
                    SyncInfoBase updatedPrimSyncInfo = inPrimSyncInfo.Value;

                    SceneObjectPart part = Scene.GetSceneObjectPart(uuid);
                    if (part == null)
                    {
                        m_log.ErrorFormat("{0}: HandleSyncDelinkObject, prim {1} not in local Scene Graph after DelinkObjectsBySync is called", LogHeader, uuid);
                    }
                    else
                    {
                        m_SyncInfoManager.UpdateSyncInfoBySync(part.UUID, updatedPrimSyncInfo);
                    }
                }
            }
        }
        
        private void HandleSyncNewPresence(SymmetricSyncMessage msg, string senderActorID)
        {
            // m_log.WarnFormat("{0}: HandleSyncNewPresence called", LogHeader);
            OSDMap data = DeserializeMessage(msg);
            if(data == null)
            {
                m_log.ErrorFormat("{0}: HandleSyncNewPresence. Failed deserialization of new object data. Sender={1}", LogHeader, senderActorID);
                return;
            }

            // Decode presence and syncInfo from message data
            SyncInfoBase syncInfo;
            DecodeScenePresence(data, out syncInfo);
            DetailedUpdateWrite("RecNewPres", syncInfo.UUID, 0, m_zeroUUID, senderActorID, msg.Length);

            // if this is a relay node, forward the message
            if (IsSyncRelay)
                SendSpecialUpdateToRelevantSyncConnectors(senderActorID, "SndNewPreR", syncInfo.UUID, msg);

            //Add the SyncInfo to SyncInfoManager
            m_SyncInfoManager.InsertSyncInfo(syncInfo.UUID, syncInfo);

            // Get ACD and PresenceType from decoded SyncInfoPresence
            // NASTY CASTS AHEAD!
            AgentCircuitData acd = (AgentCircuitData)((SyncInfoPresence)syncInfo).CurrentlySyncedProperties[SyncableProperties.Type.AgentCircuitData].LastUpdateValue;
            PresenceType pt = (PresenceType)(int)(((SyncInfoPresence)syncInfo).CurrentlySyncedProperties[SyncableProperties.Type.PresenceType].LastUpdateValue);

            // Add the decoded circuit to local scene
            Scene.AuthenticateHandler.AddNewCircuit(acd.circuitcode, acd);

            // Create a client and add it to the local scene
            IClientAPI client = new RegionSyncAvatar(acd.circuitcode, Scene, acd.AgentID, acd.firstname, acd.lastname, acd.startpos);
            bool resAttachment = false;
            syncInfo.SceneThing = Scene.AddNewClient2(client, pt, resAttachment);
            // Might need to trigger something here to send new client messages to connected clients
        }

        // Decodes scene presence data into sync info
        private void DecodeScenePresence(OSDMap data, out SyncInfoBase syncInfo)
        {
            syncInfo = null;
            if (!data.ContainsKey("ScenePresence"))
            {
                m_log.ErrorFormat("{0}: DecodeScenePresence, no ScenePresence found in the OSDMap", LogHeader);
                return;
            }

            OSDMap presenceData = (OSDMap)data["ScenePresence"];

            //Decode the syncInfo
            try
            {
                syncInfo = new SyncInfoPresence(presenceData["uuid"], (OSDMap)presenceData["propertyData"], Scene);
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("{0} DecodeScenePresence caught exception: {1}", LogHeader, e);
                return;
            }
        }

        private void HandleRemovedPresence(SymmetricSyncMessage msg, string senderActorID)
        {
            // m_log.WarnFormat("{0}: HandleSyncPresence called", LogHeader);
            OSDMap data = DeserializeMessage(msg);

            if (data == null)
            {
                SymmetricSyncMessage.HandleError(LogHeader, msg, "Could not deserialize " + msg.Type.ToString());
                return;
            }

            UUID uuid = data["uuid"].AsUUID();

            DetailedUpdateWrite("RecRemPres", uuid.ToString(), 0, m_zeroUUID, senderActorID, msg.Length);

            if (!m_SyncInfoManager.SyncInfoExists(uuid))
                return;

            // if this is a relay node, forward the message
            if (IsSyncRelay)
            {
                SendSpecialUpdateToRelevantSyncConnectors(senderActorID, "SndRemPreR", uuid, msg);
            }
            
            // This limits synced avatars to real clients (no npcs) until we sync PresenceType field
            Scene.RemoveClient(uuid, false);
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
        // The string output will have not commas in it so it doesn't break the comma
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
                            case SyncableProperties.Type.AvatarAppearance:
                            case SyncableProperties.Type.AgentCircuitData:
                                break;
                            // print out specific uint values as hex
                            case SyncableProperties.Type.AgentControlFlags:
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

        // Returns 'null' if the message cannot be deserialized
        private static HashSet<string> exceptions = new HashSet<string>();
        private static OSDMap DeserializeMessage(SymmetricSyncMessage msg)
        {
            OSDMap data = null;
            try
            {
                data = OSDParser.DeserializeJson(Encoding.ASCII.GetString(msg.Data, 0, msg.Length)) as OSDMap;
            }
            catch (Exception e)
            {
                lock (exceptions)
                {
                    // If this is a new message, then print the underlying data that caused it
                    //if (!exceptions.Contains(e.Message))
                    {
                        exceptions.Add(e.Message);  // remember we've seen this type of error
                        // print out the unparsable message
                        m_log.Error(LogHeader + " " + Encoding.ASCII.GetString(msg.Data, 0, msg.Length));
                        // after all of that, print out the actual error
                        m_log.ErrorFormat("{0}: {1}", LogHeader, e);
                    }
                }
                data = null;
            }
            return data;
        }

        #endregion //Sync message handlers

        #region Remote Event handlers

        private EventsReceived m_eventsReceived = new EventsReceived();

        
        /// <summary>
        /// The common actions for handling remote events (event initiated at other actors and propogated here)
        /// </summary>
        /// <param name="msg"></param>
        private void HandleRemoteEvent(SymmetricSyncMessage msg, string senderActorID)
        {
            OSDMap data = DeserializeMessage(msg);
            if (data == null)
            {
                SymmetricSyncMessage.HandleError(LogHeader, msg, "Could not deserialize JSON data.");
                return;
            }

            lock (m_stats) m_statEventIn++;
            //string init_actorID = data["actorID"].AsString();
            string init_syncID = data["syncID"].AsString();
            ulong evSeqNum = data["seqNum"].AsULong();

            DetailedUpdateWrite("RecEventtt", msg.Type.ToString(), 0, init_syncID, senderActorID, msg.Length);

            //check if this is a duplicate event message that we have received before
            if (m_eventsReceived.IsSEQReceived(init_syncID, evSeqNum))
            {
                m_log.ErrorFormat("Duplicate event {0} originated from {1}, seq# {2} has been received", msg.Type, init_syncID, evSeqNum);
                return;
            }
            else
            {
                m_eventsReceived.RecordEventReceived(init_syncID, evSeqNum);
            }

            // if this is a relay node, forward the message
            if (IsSyncRelay)
            {
                SendSceneEventToRelevantSyncConnectors(senderActorID, msg, null);
            }

            switch (msg.Type)
            {
                case SymmetricSyncMessage.MsgType.NewScript:
                    HandleRemoteEvent_OnNewScript(init_syncID, evSeqNum, data);
                    break;
                case SymmetricSyncMessage.MsgType.UpdateScript:
                    HandleRemoteEvent_OnUpdateScript(init_syncID, evSeqNum, data);
                    break; 
                case SymmetricSyncMessage.MsgType.ScriptReset:
                    HandleRemoteEvent_OnScriptReset(init_syncID, evSeqNum, data);
                    break;
                case SymmetricSyncMessage.MsgType.ChatFromClient:
                    HandleRemoteEvent_OnChatFromClient(init_syncID, evSeqNum, data);
                    break;
                case SymmetricSyncMessage.MsgType.ChatFromWorld:
                    HandleRemoteEvent_OnChatFromWorld(init_syncID, evSeqNum, data);
                    break;
                case SymmetricSyncMessage.MsgType.ChatBroadcast:
                    HandleRemoteEvent_OnChatBroadcast(init_syncID, evSeqNum, data);
                    break;
                case SymmetricSyncMessage.MsgType.ObjectGrab:
                    HandleRemoteEvent_OnObjectGrab(init_syncID, evSeqNum, data);
                    break;
                case SymmetricSyncMessage.MsgType.ObjectGrabbing:
                    HandleRemoteEvent_OnObjectGrabbing(init_syncID, evSeqNum, data);
                    break;
                case SymmetricSyncMessage.MsgType.ObjectDeGrab:
                    HandleRemoteEvent_OnObjectDeGrab(init_syncID, evSeqNum, data);
                    break;
                case SymmetricSyncMessage.MsgType.Attach:
                    HandleRemoteEvent_OnAttach(init_syncID, evSeqNum, data);
                    break;
                case SymmetricSyncMessage.MsgType.PhysicsCollision:
                    HandleRemoteEvent_PhysicsCollision(init_syncID, evSeqNum, data);
                    break;
                case SymmetricSyncMessage.MsgType.ScriptCollidingStart:
                case SymmetricSyncMessage.MsgType.ScriptColliding:
                case SymmetricSyncMessage.MsgType.ScriptCollidingEnd:
                case SymmetricSyncMessage.MsgType.ScriptLandCollidingStart:
                case SymmetricSyncMessage.MsgType.ScriptLandColliding:
                case SymmetricSyncMessage.MsgType.ScriptLandCollidingEnd:
                    //HandleRemoteEvent_ScriptCollidingStart(init_actorID, evSeqNum, data, DateTime.UtcNow.Ticks);
                    HandleRemoteEvent_ScriptCollidingEvents(msg.Type, init_syncID, evSeqNum, data, DateTime.UtcNow.Ticks);
                    break;
                case SymmetricSyncMessage.MsgType.TimeStamp:
                    {
                        string otherTimeString = Encoding.ASCII.GetString(msg.Data, 0, msg.Length);
                        try
                        {
                            long otherTime = long.Parse(otherTimeString);
                            DetailedUpdateWrite("TimeStampp", m_zeroUUID, otherTime, m_zeroUUID, senderActorID, msg.Length);
                        }
                        catch (Exception e)
                        {
                            m_log.ErrorFormat("{0}: MsgType.TimeStamp failed parsing: str='{1}', e={2}", LogHeader, otherTimeString, e);
                        }
                        return;
                    }

            }

        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="actorID">the ID of the actor that initiates the event</param>
        /// <param name="evSeqNum">sequence num of the event from the actor</param>
        /// <param name="data">OSDMap data of event args</param>
        private void HandleRemoteEvent_OnNewScript(string actorID, ulong evSeqNum, OSDMap data)
        {
            UUID agentID = data["agentID"].AsUUID();
            UUID uuid = data["uuid"].AsUUID();
            UUID itemID = data["itemID"].AsUUID();

            SceneObjectPart localPart = Scene.GetSceneObjectPart(uuid);

            if (localPart == null || localPart.ParentGroup.IsDeleted)
            {
                m_log.ErrorFormat("{0}: HandleRemoteEvent_OnNewScript: prim {1} no longer in local SceneGraph", LogHeader, uuid);
                return;
            }

            HashSet<SyncedProperty> syncedProperties = SyncedProperty.DecodeProperties(data);
            if (syncedProperties.Count > 0)
            {
                HashSet<SyncableProperties.Type> propertiesUpdated = m_SyncInfoManager.UpdateSyncInfoBySync(uuid, syncedProperties);
            }

            //The TaskInventory value might have already been sync'ed by UpdatedPrimProperties, 
            //but we still need to create the script instance by reading out the inventory.
            RememberLocallyGeneratedEvent(SymmetricSyncMessage.MsgType.NewScript, agentID, localPart, itemID);
            Scene.EventManager.TriggerNewScript(agentID, localPart, itemID);
            ForgetLocallyGeneratedEvent();
        }
        
       

        /// <summary>
        /// Special actions for remote event UpdateScript
        /// </summary>
        /// <param name="actorID">the ID of the actor that initiates the event</param>
        /// <param name="evSeqNum">sequence num of the event from the actor</param>
        /// <param name="data">OSDMap data of event args</param>
        private void HandleRemoteEvent_OnUpdateScript(string actorID, ulong evSeqNum, OSDMap data)
        {
            m_log.DebugFormat("{0}: {1} received UpdateScript event", LogHeader, ActorID);

            UUID agentID = data["agentID"].AsUUID();
            UUID itemID = data["itemID"].AsUUID();
            UUID primID = data["primID"].AsUUID();
            bool isRunning = data["running"].AsBoolean();
            UUID assetID = data["assetID"].AsUUID();

            //trigger the event in the local scene
            RememberLocallyGeneratedEvent(SymmetricSyncMessage.MsgType.UpdateScript, agentID, itemID, primID, isRunning, assetID);   
            Scene.EventManager.TriggerUpdateScript(agentID, itemID, primID, isRunning, assetID);   
            ForgetLocallyGeneratedEvent();
        }

        /// <summary>
        /// Special actions for remote event ScriptReset
        /// </summary>
        /// <param name="data">OSDMap data of event args</param>
        private void HandleRemoteEvent_OnScriptReset(string actorID, ulong evSeqNum, OSDMap data)
        {
            // m_log.DebugFormat("{0}: {1} received ScriptReset event", LogHeader, ActorID);

            UUID agentID = data["agentID"].AsUUID();
            UUID itemID = data["itemID"].AsUUID();
            UUID primID = data["primID"].AsUUID();

            SceneObjectPart part = Scene.GetSceneObjectPart(primID);
            if (part == null || part.ParentGroup.IsDeleted)
            {
                m_log.ErrorFormat("{0}: part {1}" + primID + " not exist, or is deleted");
                return;
            }
            RememberLocallyGeneratedEvent(SymmetricSyncMessage.MsgType.ScriptReset, part.LocalId, itemID);
            Scene.EventManager.TriggerScriptReset(part.LocalId, itemID);
            ForgetLocallyGeneratedEvent();
        }

        private void HandleRemoteEvent_OnChatFromClient(string actorID, ulong evSeqNum, OSDMap data)
        {
            OSChatMessage args = PrepareOnChatArgs(data);
            //m_log.WarnFormat("RegionSyncModule.HandleRemoteEvent_OnChatFromClient {0}:{1}", args.From, args.Message);
            if (args.Sender is RegionSyncAvatar)
                ((RegionSyncAvatar)args.Sender).SyncChatFromClient(args);
            /*
            RememberLocallyGeneratedEvent(SymmetricSyncMessage.MsgType.ChatFromClient, args);   
            Scene.EventManager.TriggerOnChatFromClient(args.SenderObject, args); //Let WorldCommModule and other modules to catch the event
            ForgetLocallyGeneratedEvent();
            RememberLocallyGeneratedEvent(SymmetricSyncMessage.MsgType.ChatFromWorld, args);   
            Scene.EventManager.TriggerOnChatFromWorld(args.SenderObject, args); //This is to let ChatModule to get the event and deliver it to avatars
            ForgetLocallyGeneratedEvent();
             * */
        }

        private void HandleRemoteEvent_OnChatFromWorld(string actorID, ulong evSeqNum, OSDMap data)
        {
            OSChatMessage args = PrepareOnChatArgs(data);
            //m_log.WarnFormat("RegionSyncModule.HandleRemoteEvent_OnChatFromWorld {0}:{1}", args.From, args.Message);
            RememberLocallyGeneratedEvent(SymmetricSyncMessage.MsgType.ChatFromWorld, args);   
            Scene.EventManager.TriggerOnChatFromWorld(args.SenderObject, args); //This is to let ChatModule to get the event and deliver it to avatars
            ForgetLocallyGeneratedEvent();
        }

        private void HandleRemoteEvent_OnChatBroadcast(string actorID, ulong evSeqNum, OSDMap data)
        {
            OSChatMessage args = PrepareOnChatArgs(data);
            //m_log.WarnFormat("RegionSyncModule.HandleRemoteEvent_OnChatBroadcast {0}:{1}", args.From, args.Message);
            RememberLocallyGeneratedEvent(SymmetricSyncMessage.MsgType.ChatBroadcast, args);   
            Scene.EventManager.TriggerOnChatBroadcast(args.SenderObject, args);
            ForgetLocallyGeneratedEvent();
        }

        private OSChatMessage PrepareOnChatArgs(OSDMap data)
        {
            OSChatMessage args = new OSChatMessage();
            args.Channel = data["channel"].AsInteger();
            args.Message = data["msg"].AsString();
            args.Position = data["pos"].AsVector3();
            args.From = data["name"].AsString();
            args.SenderUUID = data["id"].AsUUID();
            args.Scene = Scene;
            args.Type = (ChatTypeEnum)data["type"].AsInteger();

            // Need to look up the sending object within this scene!
            args.SenderObject = Scene.GetScenePresence(args.SenderUUID);
            if(args.SenderObject != null)
                args.Sender = ((ScenePresence)args.SenderObject).ControllingClient;
            else
                args.SenderObject = Scene.GetSceneObjectPart(args.SenderUUID);
            //m_log.WarnFormat("RegionSyncModule.PrepareOnChatArgs: name:\"{0}\" msg:\"{1}\" pos:{2} id:{3}", args.From, args.Message, args.Position, args.SenderUUID);
            return args;
        }

        /// <summary>
        /// Special actions for remote event ChatFromClient
        /// </summary>
        /// <param name="data">OSDMap data of event args</param>
        private void HandleRemoteEvent_OnObjectGrab(string actorID, ulong evSeqNum, OSDMap data)
        {
            // m_log.DebugFormat("{0}: {1} received GrabObject from {2}, seq={3}", LogHeader, ActorID, actorID, evSeqNum);

            UUID agentID = data["agentID"].AsUUID();
            UUID primID = data["primID"].AsUUID();
            UUID originalPrimID = data["originalPrimID"].AsUUID();
            Vector3 offsetPos = data["offsetPos"].AsVector3();
            SurfaceTouchEventArgs surfaceArgs = new SurfaceTouchEventArgs();
            surfaceArgs.Binormal = data["binormal"].AsVector3();
            surfaceArgs.FaceIndex = data["faceIndex"].AsInteger();
            surfaceArgs.Normal = data["normal"].AsVector3();
            surfaceArgs.Position = data["position"].AsVector3();
            surfaceArgs.STCoord = data["stCoord"].AsVector3();
            surfaceArgs.UVCoord = data["uvCoord"].AsVector3();

            SceneObjectPart part = Scene.GetSceneObjectPart(primID);
            if (part == null)
            {
                m_log.ErrorFormat("{0}: HandleRemoteEvent_OnObjectGrab: no prim with ID {1}", LogHeader, primID);
                return;
            }
            uint originalID = 0;
            if (originalPrimID != UUID.Zero)
            {
                SceneObjectPart originalPart = Scene.GetSceneObjectPart(originalPrimID);
                originalID = originalPart.LocalId;
            }
            
            // Get the scene presence in local scene that triggered the event
            ScenePresence sp;
            if (!Scene.TryGetScenePresence(agentID, out sp))
            {
                m_log.ErrorFormat("{0} HandleRemoteEvent_OnObjectGrab: could not get ScenePresence for uuid {1}", LogHeader, agentID);
                return;
            }

            RememberLocallyGeneratedEvent(SymmetricSyncMessage.MsgType.ObjectGrab, part.LocalId, originalID, offsetPos, sp.ControllingClient, surfaceArgs);
            Scene.EventManager.TriggerObjectGrab(part.LocalId, originalID, offsetPos, sp.ControllingClient, surfaceArgs);
            ForgetLocallyGeneratedEvent();
        }

        private void HandleRemoteEvent_OnObjectGrabbing(string actorID, ulong evSeqNum, OSDMap data)
        {
            // m_log.DebugFormat("{0}: {1} received ObjectGrabbing from {2}, seq={3}", LogHeader, ActorID, actorID, evSeqNum);

            UUID agentID = data["agentID"].AsUUID();
            UUID primID = data["primID"].AsUUID();
            UUID originalPrimID = data["originalPrimID"].AsUUID();
            Vector3 offsetPos = data["offsetPos"].AsVector3();
            SurfaceTouchEventArgs surfaceArgs = new SurfaceTouchEventArgs();
            surfaceArgs.Binormal = data["binormal"].AsVector3();
            surfaceArgs.FaceIndex = data["faceIndex"].AsInteger();
            surfaceArgs.Normal = data["normal"].AsVector3();
            surfaceArgs.Position = data["position"].AsVector3();
            surfaceArgs.STCoord = data["stCoord"].AsVector3();
            surfaceArgs.UVCoord = data["uvCoord"].AsVector3();

            SceneObjectPart part = Scene.GetSceneObjectPart(primID);
            if (part == null)
            {
                m_log.ErrorFormat("{0}: HandleRemoteEvent_OnObjectGrabbing: no prim with ID {1}", LogHeader, primID);
                return;
            }
            uint originalID = 0;
            if (originalPrimID != UUID.Zero)
            {
                SceneObjectPart originalPart = Scene.GetSceneObjectPart(originalPrimID);
                originalID = originalPart.LocalId;
            }

            // Get the scene presence in local scene that triggered the event
            ScenePresence sp;
            if (!Scene.TryGetScenePresence(agentID, out sp))
            {
                m_log.ErrorFormat("{0} HandleRemoteEvent_OnObjectGrab: could not get ScenePresence for uuid {1}", LogHeader, agentID);
                return;
            }

            RememberLocallyGeneratedEvent(SymmetricSyncMessage.MsgType.ObjectGrabbing, part.LocalId, originalID, offsetPos, sp.ControllingClient, surfaceArgs);
            Scene.EventManager.TriggerObjectGrabbing(part.LocalId, originalID, offsetPos, sp.ControllingClient, surfaceArgs);
            ForgetLocallyGeneratedEvent();
        }

        private void HandleRemoteEvent_OnObjectDeGrab(string actorID, ulong evSeqNum, OSDMap data)
        {
            // m_log.DebugFormat("{0}: {1} received ObjectDeGrab from {2}, seq={3}", LogHeader, ActorID, actorID, evSeqNum);

            UUID agentID = data["agentID"].AsUUID();
            UUID primID = data["primID"].AsUUID();
            UUID originalPrimID = data["originalPrimID"].AsUUID();
            
            SurfaceTouchEventArgs surfaceArgs = new SurfaceTouchEventArgs();
            surfaceArgs.Binormal = data["binormal"].AsVector3();
            surfaceArgs.FaceIndex = data["faceIndex"].AsInteger();
            surfaceArgs.Normal = data["normal"].AsVector3();
            surfaceArgs.Position = data["position"].AsVector3();
            surfaceArgs.STCoord = data["stCoord"].AsVector3();
            surfaceArgs.UVCoord = data["uvCoord"].AsVector3();

            SceneObjectPart part = Scene.GetSceneObjectPart(primID);
            if (part == null)
            {
                m_log.ErrorFormat("{0}: HandleRemoteEvent_OnObjectDeGrab: no prim with ID {1}", LogHeader, primID);
                return;
            }
            uint originalID = 0;
            if (originalPrimID != UUID.Zero)
            {
                SceneObjectPart originalPart = Scene.GetSceneObjectPart(originalPrimID);
                originalID = originalPart.LocalId;
            }

            // Get the scene presence in local scene that triggered the event
            ScenePresence sp;
            if (!Scene.TryGetScenePresence(agentID, out sp))
            {
                m_log.ErrorFormat("{0} HandleRemoteEvent_OnObjectGrab could not get ScenePresence for uuid {1}", LogHeader, agentID);
                return;
            }

            RememberLocallyGeneratedEvent(SymmetricSyncMessage.MsgType.ObjectDeGrab, part.LocalId, originalID, sp.ControllingClient, surfaceArgs);
            Scene.EventManager.TriggerObjectDeGrab(part.LocalId, originalID, sp.ControllingClient, surfaceArgs);
            ForgetLocallyGeneratedEvent();
        }

        private void HandleRemoteEvent_OnAttach(string actorID, ulong evSeqNum, OSDMap data)
        {
            UUID primID = data["primID"].AsUUID();
            UUID itemID = data["itemID"].AsUUID();
            UUID avatarID = data["avatarID"].AsUUID();

            SceneObjectPart part = Scene.GetSceneObjectPart(primID);
            if (part == null)
            {
                m_log.Warn(LogHeader + ", HandleRemoteEvent_OnAttach: no part with UUID " + primID + " found");
                return;
            }

            uint localID = part.LocalId;
            RememberLocallyGeneratedEvent(SymmetricSyncMessage.MsgType.Attach, localID, itemID, avatarID);
            Scene.EventManager.TriggerOnAttach(localID, itemID, avatarID);
            ForgetLocallyGeneratedEvent();
        }

        private void HandleRemoteEvent_ScriptCollidingEvents(SymmetricSyncMessage.MsgType msgType, string syncID, ulong evSeqNum, OSDMap data, long recvTime)
        {
            if (!data.ContainsKey("uuid") || !data.ContainsKey("collisionUUIDs"))
            {
                m_log.ErrorFormat("{0}: HandleRemoteEvent_{1}: either uuid or collisionUUIDs is missing in incoming OSDMap",
                            LogHeader, msgType.ToString());
                return;
            }

            ColliderArgs StartCollidingMessage = new ColliderArgs();
            List<DetectedObject> colliding = new List<DetectedObject>();
            SceneObjectPart collisionPart = null;
            OSDArray collidersNotFound = new OSDArray();

            try
            {
                UUID uuid = data["uuid"].AsUUID();
                //OSDArray collisionLocalIDs = (OSDArray)data["collisionLocalIDs"];
                OSDArray collisionUUIDs = (OSDArray)data["collisionUUIDs"];

                collisionPart = Scene.GetSceneObjectPart(uuid);
                if (collisionPart == null)
                {
                    m_log.ErrorFormat("{0}: HandleRemoteEvent_{1}: no part with UUID {2} found, event initiator {3}", 
                                    LogHeader, msgType.ToString(), uuid, syncID);
                    return;
                }
                if (collisionUUIDs == null)
                {
                    m_log.ErrorFormat("{0}: HandleRemoteEvent_{1}: no collisionLocalIDs", LogHeader, msgType.ToString());
                    return;
                }
                if (collisionPart.ParentGroup.IsDeleted == true)
                    return;

                switch (msgType)
                {
                    case SymmetricSyncMessage.MsgType.ScriptCollidingStart:
                    case SymmetricSyncMessage.MsgType.ScriptColliding:
                    case SymmetricSyncMessage.MsgType.ScriptCollidingEnd:
                        {
                            for (int i = 0; i < collisionUUIDs.Count; i++)
                            {
                                OSD arg = collisionUUIDs[i];
                                UUID collidingUUID = arg.AsUUID();

                                SceneObjectPart obj = Scene.GetSceneObjectPart(collidingUUID);
                                if (obj != null)
                                {
                                    DetectedObject detobj = new DetectedObject();
                                    detobj.keyUUID = obj.UUID;
                                    detobj.nameStr = obj.Name;
                                    detobj.ownerUUID = obj.OwnerID;
                                    detobj.posVector = obj.AbsolutePosition;
                                    detobj.rotQuat = obj.GetWorldRotation();
                                    detobj.velVector = obj.Velocity;
                                    detobj.colliderType = 0;
                                    detobj.groupUUID = obj.GroupID;
                                    colliding.Add(detobj);
                                }
                                else
                                {
                                    //collision object is not a prim, check if it's an avatar
                                    ScenePresence av = Scene.GetScenePresence(collidingUUID);
                                    if (av != null)
                                    {
                                        DetectedObject detobj = new DetectedObject();
                                        detobj.keyUUID = av.UUID;
                                        detobj.nameStr = av.ControllingClient.Name;
                                        detobj.ownerUUID = av.UUID;
                                        detobj.posVector = av.AbsolutePosition;
                                        detobj.rotQuat = av.Rotation;
                                        detobj.velVector = av.Velocity;
                                        detobj.colliderType = 0;
                                        detobj.groupUUID = av.ControllingClient.ActiveGroupId;
                                        colliding.Add(detobj);
                                    }
                                    else
                                    {
                                        // m_log.WarnFormat("HandleRemoteEvent_ScriptCollidingStart for SOP {0},{1} with SOP/SP {2}, but the latter is not found in local Scene. Saved for later processing",
                                        //             collisionPart.Name, collisionPart.UUID, collidingUUID);
                                        collidersNotFound.Add(OSD.FromUUID(collidingUUID));
                                    }
                                }
                            }

                            if (collidersNotFound.Count > 0)
                            {
                                //hard-coded expiration time to be one minute
                                TimeSpan msgExpireTime = new TimeSpan(0, 1, 0);
                                TimeSpan msgSavedTime = new TimeSpan(DateTime.UtcNow.Ticks - recvTime);

                                if (msgSavedTime < msgExpireTime)
                                {

                                    OSDMap newdata = new OSDMap();
                                    newdata["uuid"] = OSD.FromUUID(collisionPart.UUID);
                                    newdata["collisionUUIDs"] = collidersNotFound;

                                    //newdata["actorID"] = OSD.FromString(actorID);
                                    newdata["syncID"] = OSD.FromString(syncID);
                                    newdata["seqNum"] = OSD.FromULong(evSeqNum);

                                    SymmetricSyncMessage rsm = null;
                                    switch (msgType)
                                    {
                                        case SymmetricSyncMessage.MsgType.ScriptCollidingStart:
                                            rsm = new SymmetricSyncMessage(SymmetricSyncMessage.MsgType.ScriptCollidingStart, newdata);
                                            break;
                                        case SymmetricSyncMessage.MsgType.ScriptColliding:
                                            rsm = new SymmetricSyncMessage(SymmetricSyncMessage.MsgType.ScriptColliding, newdata);
                                            break;
                                        case SymmetricSyncMessage.MsgType.ScriptCollidingEnd:
                                            rsm = new SymmetricSyncMessage(SymmetricSyncMessage.MsgType.ScriptCollidingEnd, newdata);
                                            break;
                                    }
                                    SyncMessageRecord syncMsgToSave = new SyncMessageRecord();
                                    syncMsgToSave.ReceivedTime = recvTime;
                                    syncMsgToSave.SyncMessage = rsm;
                                    lock (m_savedSyncMessage)
                                    {
                                        m_savedSyncMessage.Add(syncMsgToSave);
                                    }
                                }
                            }
                        }
                        break;
                    case SymmetricSyncMessage.MsgType.ScriptLandCollidingStart:
                    case SymmetricSyncMessage.MsgType.ScriptLandColliding:
                    case SymmetricSyncMessage.MsgType.ScriptLandCollidingEnd:
                        {
                            for (int i = 0; i < collisionUUIDs.Count; i++)
                            {
                                OSD arg = collisionUUIDs[i];
                                UUID collidingUUID = arg.AsUUID();
                                if (collidingUUID.Equals(UUID.Zero))
                                {
                                    //Hope that all is left is ground!
                                    DetectedObject detobj = new DetectedObject();
                                    detobj.keyUUID = UUID.Zero;
                                    detobj.nameStr = "";
                                    detobj.ownerUUID = UUID.Zero;
                                    detobj.posVector = collisionPart.ParentGroup.RootPart.AbsolutePosition;
                                    detobj.rotQuat = Quaternion.Identity;
                                    detobj.velVector = Vector3.Zero;
                                    detobj.colliderType = 0;
                                    detobj.groupUUID = UUID.Zero;
                                    colliding.Add(detobj);
                                }
                            }
                        }
                        break;
                }
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("HandleRemoteEvent_ScriptCollidingStart Error: {0}", e.Message);
            }
           
            if (colliding.Count > 0)
            {
                StartCollidingMessage.Colliders = colliding;
                // always running this check because if the user deletes the object it would return a null reference.
                switch (msgType)
                {
                    case SymmetricSyncMessage.MsgType.ScriptCollidingStart:
                        m_log.DebugFormat("ScriptCollidingStart received for {0}", collisionPart.Name);
                        RememberLocallyGeneratedEvent(msgType, collisionPart.LocalId, StartCollidingMessage);
                        Scene.EventManager.TriggerScriptCollidingStart(collisionPart.LocalId, StartCollidingMessage);
                        ForgetLocallyGeneratedEvent();
                        break;
                    case SymmetricSyncMessage.MsgType.ScriptColliding:
                        m_log.DebugFormat("ScriptColliding received for {0}", collisionPart.Name);
                        RememberLocallyGeneratedEvent(msgType, collisionPart.LocalId, StartCollidingMessage);
                        Scene.EventManager.TriggerScriptColliding(collisionPart.LocalId, StartCollidingMessage);
                        ForgetLocallyGeneratedEvent();
                        break;
                    case SymmetricSyncMessage.MsgType.ScriptCollidingEnd:
                        m_log.DebugFormat("ScriptCollidingEnd received for {0}", collisionPart.Name);
                        RememberLocallyGeneratedEvent(msgType, collisionPart.LocalId, StartCollidingMessage);
                        Scene.EventManager.TriggerScriptCollidingEnd(collisionPart.LocalId, StartCollidingMessage);
                        ForgetLocallyGeneratedEvent();
                        break;
                    case SymmetricSyncMessage.MsgType.ScriptLandCollidingStart:
                        m_log.DebugFormat("ScriptLandCollidingStart received for {0}", collisionPart.Name);
                        RememberLocallyGeneratedEvent(msgType, collisionPart.LocalId, StartCollidingMessage);
                        Scene.EventManager.TriggerScriptLandCollidingStart(collisionPart.LocalId, StartCollidingMessage);
                        ForgetLocallyGeneratedEvent();
                        break;
                    case SymmetricSyncMessage.MsgType.ScriptLandColliding:
                        m_log.DebugFormat("ScriptLandColliding received for {0}", collisionPart.Name);
                        RememberLocallyGeneratedEvent(msgType, collisionPart.LocalId, StartCollidingMessage);
                        Scene.EventManager.TriggerScriptLandColliding(collisionPart.LocalId, StartCollidingMessage);
                        ForgetLocallyGeneratedEvent();
                        break;
                    case SymmetricSyncMessage.MsgType.ScriptLandCollidingEnd:
                        m_log.DebugFormat("ScriptLandCollidingEnd received for {0}", collisionPart.Name);
                        RememberLocallyGeneratedEvent(msgType, collisionPart.LocalId, StartCollidingMessage);
                        Scene.EventManager.TriggerScriptLandCollidingEnd(collisionPart.LocalId, StartCollidingMessage);
                        ForgetLocallyGeneratedEvent();
                        break;
                }
            }
        }

        private int spErrCount = 0;
        private HashSet<UUID> errUUIDs = new HashSet<UUID>(); 
        private void HandleRemoteEvent_PhysicsCollision(string actorID, ulong evSeqNum, OSDMap data)
        {
            if (!data.ContainsKey("uuid") || !data.ContainsKey("collisionUUIDs"))
            {
                m_log.ErrorFormat("RemoteEvent_PhysicsCollision: either uuid or collisionUUIDs is missing in incoming OSDMap");
                return;
            }

            try
            {
                UUID uuid = data["uuid"].AsUUID();
                OSDArray collisionUUIDs = (OSDArray)data["collisionUUIDs"];

                SceneObjectPart part = Scene.GetSceneObjectPart(uuid);
                if (part == null)
                {
                    m_log.ErrorFormat("{0}: HandleRemoteEvent_PhysicsCollision: no part with UUID {1} found, event initiator {2}", LogHeader, uuid, actorID);
                    return;
                }
                if (collisionUUIDs == null)
                {
                    m_log.ErrorFormat("{0}: HandleRemoteEvent_PhysicsCollision: no collisionLocalIDs", LogHeader);
                    return;
                }

                // Build up the collision list. The contact point is ignored so we generate some default.
                CollisionEventUpdate e = new CollisionEventUpdate();

                for (int i = 0; i < collisionUUIDs.Count; i++)
                {
                    OSD arg = collisionUUIDs[i];
                    UUID collidingUUID = arg.AsUUID();

                    //check if it's land collision first.
                    if (collidingUUID == UUID.Zero)
                    {
                        uint localID = 0;
                        e.AddCollider(localID, new ContactPoint(Vector3.Zero, Vector3.UnitX, 0.03f));
                        continue;
                    }

                    SceneObjectPart collidingPart = Scene.GetSceneObjectPart(collidingUUID);
                    if (collidingPart == null)
                    {
                        //collision object is not a prim, check if it's an avatar
                        ScenePresence sp = Scene.GetScenePresence(collidingUUID);
                        if (sp == null)
                        {
                            //m_log.WarnFormat("Received collision event for SOP {0},{1} with another SOP/SP {2}, but the latter is not found in local Scene",
                            //    part.Name, part.UUID, collidingUUID);
                            if (spErrCount == 100)
                            {
                                string missedUUIDs = "";
                                foreach (UUID cUUID in errUUIDs)
                                {
                                    missedUUIDs += cUUID.ToString() + ", ";
                                }
                                m_log.ErrorFormat("{0}: collidingUUID not found {1} times. {2} collidingUUIDs seen so far: {3}",
                                            LogHeader, spErrCount, errUUIDs.Count, missedUUIDs);
                                spErrCount = 0;
                                errUUIDs.Clear();
                            }
                            else
                            {
                                spErrCount++;
                                errUUIDs.Add(collidingUUID);
                            }
                        }
                        else
                        {
                            e.AddCollider(sp.LocalId, new ContactPoint(Vector3.Zero, Vector3.UnitX, 0.03f));
                        }
                    }
                    else
                    {
                        e.AddCollider(collidingPart.LocalId, new ContactPoint(Vector3.Zero, Vector3.UnitX, 0.03f));
                    }
                }
                // This will generate OnScriptColliding* but these should be sent out
                part.PhysicsCollision(e);
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("HandleRemoteEvent_PhysicsCollision ERROR: {0}", e.Message);
            }
        }

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
        private void RememberLocallyGeneratedEvent(SymmetricSyncMessage.MsgType msgtype, params Object[] parms)
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
        private bool IsLocallyGeneratedEvent(SymmetricSyncMessage.MsgType msgtype, params Object[] parms)
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
        private void ForgetLocallyGeneratedEvent()
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
        private string CreateLocallyGeneratedEventSignature(SymmetricSyncMessage.MsgType msgtype, params Object[] parms)
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
            if (IsLocallyGeneratedEvent(SymmetricSyncMessage.MsgType.NewScript, clientID, part, itemID))
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
            //inventory but not creating script instance unless this is a 
            //script engine. We just make sure that if that does not happen 
            //ealier than this, we are sync'ing the new TaskInventory.
            updatedProperties.Add(SyncableProperties.Type.TaskInventory);

            OSDMap syncData = m_SyncInfoManager.EncodeProperties(part.UUID, updatedProperties);
            //syncData already includes uuid, add agentID and itemID next
            syncData["agentID"] = OSD.FromUUID(clientID);
            syncData["itemID"] = OSD.FromUUID(itemID); //id of the new inventory item of the part

            SendSceneEvent(SymmetricSyncMessage.MsgType.NewScript, syncData);
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
            if (IsLocallyGeneratedEvent(SymmetricSyncMessage.MsgType.UpdateScript, agentID, itemId, isScriptRunning, newAssetID))
                return;

            OSDMap data = new OSDMap();
            data["agentID"] = OSD.FromUUID(agentID);
            data["itemID"] = OSD.FromUUID(itemId);
            data["primID"] = OSD.FromUUID(primId);
            data["running"] = OSD.FromBoolean(isScriptRunning);
            data["assetID"] = OSD.FromUUID(newAssetID);

            SendSceneEvent(SymmetricSyncMessage.MsgType.UpdateScript, data);
        }

        private void OnLocalScriptReset(uint localID, UUID itemID)
        {
            // m_log.DebugFormat("{0}: OnLocalScriptReset: obj={1}", LogHeader, itemID);
            if (IsLocallyGeneratedEvent(SymmetricSyncMessage.MsgType.ScriptReset, localID, itemID))
                return;

            //we will use the prim's UUID as the identifier, not the localID, to publish the event for the prim                
            SceneObjectPart part = Scene.GetSceneObjectPart(localID);

            if (part == null)
            {
                m_log.Warn(LogHeader + ": part with localID " + localID + " not exist");
                return;
            }

            OSDMap data = new OSDMap();
            data["primID"] = OSD.FromUUID(part.UUID);
            data["itemID"] = OSD.FromUUID(itemID);

            SendSceneEvent(SymmetricSyncMessage.MsgType.ScriptReset, data);
        }

        private void OnLocalChatBroadcast(Object sender, OSChatMessage chat)
        {
            
            if (IsLocallyGeneratedEvent(SymmetricSyncMessage.MsgType.ChatBroadcast, sender, chat))
                return;

            //m_log.WarnFormat("RegionSyncModule.OnLocalChatBroadcast {0}:{1}", chat.From, chat.Message);
            OSDMap data = PrepareChatArgs(chat);
            SendSceneEvent(SymmetricSyncMessage.MsgType.ChatBroadcast, data);
        }

        private void OnLocalChatFromClient(Object sender, OSChatMessage chat)
        {
            if (chat.Sender is RegionSyncAvatar)
                return;
            //if (IsLocallyGeneratedEvent(SymmetricSyncMessage.MsgType.ChatFromClient, sender, chat))
            //    return;

            //m_log.WarnFormat("RegionSyncModule.OnLocalChatFromClient {0}:{1}", chat.From, chat.Message);
            OSDMap data = PrepareChatArgs(chat);
            SendSceneEvent(SymmetricSyncMessage.MsgType.ChatFromClient, data);
        }

        private void OnLocalChatFromWorld(Object sender, OSChatMessage chat)
        {
            if (IsLocallyGeneratedEvent(SymmetricSyncMessage.MsgType.ChatFromWorld, sender, chat))
                return;

            //m_log.WarnFormat("RegionSyncModule.OnLocalChatFromWorld {0}:{1}", chat.From, chat.Message);
            OSDMap data = PrepareChatArgs(chat);
            SendSceneEvent(SymmetricSyncMessage.MsgType.ChatFromWorld, data);
        }

        private OSDMap PrepareChatArgs(OSChatMessage chat)
        {
            OSDMap data = new OSDMap();
            data["channel"] = OSD.FromInteger(chat.Channel);
            data["msg"] = OSD.FromString(chat.Message);
            data["pos"] = OSD.FromVector3(chat.Position);
            data["name"] = OSD.FromString(chat.From); //note this is different from OnLocalChatFromClient
            data["id"] = OSD.FromUUID(chat.SenderUUID);
            data["type"] = OSD.FromInteger((int)chat.Type);
            return data;
        }

        private void OnLocalAttach(uint localID, UUID itemID, UUID avatarID)
        {
            if (IsLocallyGeneratedEvent(SymmetricSyncMessage.MsgType.Attach, localID, itemID, avatarID))
                return;

            OSDMap data = new OSDMap();
            SceneObjectPart part = Scene.GetSceneObjectPart(localID);
            if (part == null)
            {
                m_log.Warn(LogHeader + ", OnLocalAttach: no part with localID: " + localID);
                return;
            }
            data["primID"] = OSD.FromUUID(part.UUID);
            data["itemID"] = OSD.FromUUID(itemID);
            data["avatarID"] = OSD.FromUUID(avatarID);
            SendSceneEvent(SymmetricSyncMessage.MsgType.Attach, data);
        }

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

        private void OnLocalGrabObject(uint localID, uint originalID, Vector3 offsetPos, IClientAPI remoteClient, SurfaceTouchEventArgs surfaceArgs)
        {
            if (IsLocallyGeneratedEvent(SymmetricSyncMessage.MsgType.ObjectGrab, localID, originalID, offsetPos, remoteClient, surfaceArgs))
                return;

            OSDMap data = PrepareObjectGrabArgs(localID, originalID, offsetPos, remoteClient, surfaceArgs);
            SendSceneEvent(SymmetricSyncMessage.MsgType.ObjectGrab, data);
        }

        private void OnLocalObjectGrabbing(uint localID, uint originalID, Vector3 offsetPos, IClientAPI remoteClient, SurfaceTouchEventArgs surfaceArgs)
        {
            if (IsLocallyGeneratedEvent(SymmetricSyncMessage.MsgType.ObjectGrabbing, localID, originalID, offsetPos, remoteClient, surfaceArgs))
                return;

            OSDMap data = PrepareObjectGrabArgs(localID, originalID, offsetPos, remoteClient, surfaceArgs);
            SendSceneEvent(SymmetricSyncMessage.MsgType.ObjectGrabbing, data);
        }

        // Returns 'null' when the object can't be found or other construction errors.
        private OSDMap PrepareObjectGrabArgs(uint localID, uint originalID, Vector3 offsetPos, IClientAPI remoteClient, SurfaceTouchEventArgs surfaceArgs)
        {
            //we will use the prim's UUID as the identifier, not the localID, to publish the event for the prim                
            SceneObjectPart part = Scene.GetSceneObjectPart(localID);
            if (part == null)
            {
                m_log.ErrorFormat("{0}: PrepareObjectGrabArgs - part with localID {1} does not exist", LogHeader, localID);
                return null;
            }

            //this seems to be useful if the prim touched and the prim handling the touch event are different:
            //i.e. a child part is touched, pass the event to root, and root handles the event. then root is the "part",
            //and the child part is the "originalPart"
            SceneObjectPart originalPart = null;
            if (originalID != 0)
            {
                originalPart = Scene.GetSceneObjectPart(originalID);
                if (originalPart == null)
                {
                    m_log.ErrorFormat("{0}: PrepareObjectGrabArgs - part with localID {1} does not exist", LogHeader, localID);
                    return null;
                }
            }

            OSDMap data = new OSDMap();
            data["agentID"] = OSD.FromUUID(remoteClient.AgentId);
            data["primID"] = OSD.FromUUID(part.UUID);
            if (originalID != 0)
            {
                data["originalPrimID"] = OSD.FromUUID(originalPart.UUID);
            }
            else
            {
                data["originalPrimID"] = OSD.FromUUID(UUID.Zero);
            }
            data["offsetPos"] = OSD.FromVector3(offsetPos);

            data["binormal"] = OSD.FromVector3(surfaceArgs.Binormal);
            data["faceIndex"] = OSD.FromInteger(surfaceArgs.FaceIndex);
            data["normal"] = OSD.FromVector3(surfaceArgs.Normal);
            data["position"] = OSD.FromVector3(surfaceArgs.Position);
            data["stCoord"] = OSD.FromVector3(surfaceArgs.STCoord);
            data["uvCoord"] = OSD.FromVector3(surfaceArgs.UVCoord);

            return data;
        }

         private void OnLocalDeGrabObject(uint localID, uint originalID, IClientAPI remoteClient, SurfaceTouchEventArgs surfaceArgs)
        {
            if (IsLocallyGeneratedEvent(SymmetricSyncMessage.MsgType.ObjectDeGrab, localID, originalID, remoteClient, surfaceArgs))
                return;

            Vector3 offsetPos = Vector3.Zero;
            OSDMap data = PrepareObjectGrabArgs(localID, originalID, offsetPos, remoteClient, surfaceArgs);
            SendSceneEvent(SymmetricSyncMessage.MsgType.ObjectDeGrab, data);
        }

        private void OnLocalScriptCollidingStart(uint localID, ColliderArgs colliders)
        {
            m_log.WarnFormat("{0}: OnLocalScriptCollidingStart", LogHeader);
            if (IsLocallyGeneratedEvent(SymmetricSyncMessage.MsgType.ScriptCollidingStart, localID, colliders))
                return;

            OSDMap data = PrepareCollisionArgs(localID, colliders);
            SendSceneEvent(SymmetricSyncMessage.MsgType.ScriptCollidingStart, data);
        }

        private void OnLocalScriptColliding(uint localID, ColliderArgs colliders)
        {
            if (IsLocallyGeneratedEvent(SymmetricSyncMessage.MsgType.ScriptColliding, localID, colliders))
                return;

            OSDMap data = PrepareCollisionArgs(localID, colliders);
            SendSceneEvent(SymmetricSyncMessage.MsgType.ScriptColliding, data);
        }

        private void OnLocalScriptCollidingEnd(uint localID, ColliderArgs colliders)
        {
            m_log.WarnFormat("{0}: OnLocalScriptCollidingEnd", LogHeader);
            if (IsLocallyGeneratedEvent(SymmetricSyncMessage.MsgType.ScriptCollidingEnd, localID, colliders))
                return;

            OSDMap data = PrepareCollisionArgs(localID, colliders);
            SendSceneEvent(SymmetricSyncMessage.MsgType.ScriptCollidingEnd, data);
        }

        private void OnLocalScriptLandCollidingStart(uint localID, ColliderArgs colliders)
        {
            if (IsLocallyGeneratedEvent(SymmetricSyncMessage.MsgType.ScriptLandCollidingStart, localID, colliders))
                return;

            OSDMap data = PrepareCollisionArgs(localID, colliders);
            SendSceneEvent(SymmetricSyncMessage.MsgType.ScriptLandCollidingStart, data);
        }

        private void OnLocalScriptLandColliding(uint localID, ColliderArgs colliders)
        {
            if (IsLocallyGeneratedEvent(SymmetricSyncMessage.MsgType.ScriptLandColliding, localID, colliders))
                return;

            OSDMap data = PrepareCollisionArgs(localID, colliders);
            SendSceneEvent(SymmetricSyncMessage.MsgType.ScriptLandColliding, data);
        }

        private void OnLocalScriptLandCollidingEnd(uint localID, ColliderArgs colliders)
        {
            if (IsLocallyGeneratedEvent(SymmetricSyncMessage.MsgType.ScriptLandCollidingEnd, localID, colliders))
                return;

            OSDMap data = PrepareCollisionArgs(localID, colliders);
            SendSceneEvent(SymmetricSyncMessage.MsgType.ScriptLandCollidingEnd, data);
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

        // Several routines rely on the check for 'data == null' to skip processing
        // when there are selection errors.
        private void SendSceneEvent(SymmetricSyncMessage.MsgType msgType, OSDMap data)
        {
            if (data == null)
                return;

            //data["actorID"] = OSD.FromString(ActorID);
            data["syncID"] = OSD.FromString(SyncID);
            data["seqNum"] = OSD.FromULong(GetNextEventSeq());
            SymmetricSyncMessage rsm = new SymmetricSyncMessage(msgType, data);

            //send to actors who are interested in the event
            SendSceneEventToRelevantSyncConnectors(ActorID, rsm, null);
        }

        private ulong GetNextEventSeq()
        {
            return m_eventSeq++;
        }
         
        #endregion //Remote Event handlers

        private SyncInfoManager m_SyncInfoManager;

        #region Prim Property Sync management
        //private 
        
        private void OnSceneObjectPartUpdated(SceneObjectPart part, bool full)
        {
            // If the scene presence update event was triggered by a call from RegionSyncModule, then we don't need to handle it.
            // Changes to scene presence that are actually local will not have originated from this module or thread.
            if (IsLocallyGeneratedEvent(SymmetricSyncMessage.MsgType.UpdatedProperties))
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

        private int m_updateTick = 0;
        private StringBuilder m_updateLoopLogSB;
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
                        SymmetricSyncMessage msg = syncMsgSaved.SyncMessage;
                        switch (msg.Type)
                        {
                            case SymmetricSyncMessage.MsgType.ScriptCollidingStart:
                            case SymmetricSyncMessage.MsgType.ScriptColliding:
                            case SymmetricSyncMessage.MsgType.ScriptCollidingEnd:
                                {
                                    OSDMap data = DeserializeMessage(msg);

                                    if (data == null)
                                    {
                                        SymmetricSyncMessage.HandleError(LogHeader, msg, "Could not deserialize " + msg.Type.ToString());
                                        break;   
                                    }

                                    //string init_actorID = data["actorID"].AsString();
                                    string init_syncID = data["syncID"].AsString();
                                    ulong evSeqNum = data["seqNum"].AsULong();
                                    //HandleRemoteEvent_ScriptCollidingEvents(msg.Type, init_syncID, evSeqNum, data, syncMsgSaved.ReceivedTime);
                                    break;
                                }
                            default:
                                break;
                        }
                    
                    }
                });
            }

            // Existing value of 1 indicates that updates are currently being sent so skip updates this pass
            if (Interlocked.Exchange(ref m_sendingPropertyUpdates, 1) == 1)
            {
                m_log.WarnFormat("{0} SyncOutUpdates(): An update thread is already running.", LogHeader);
                return;
            }

            m_updateTick++;

            lock (m_propertyUpdateLock)
            {
                bool tickLog = false;
                DateTime startTime = DateTime.Now;
                if (m_propertyUpdates.Count > 0)
                {
                    tickLog = true;
                    //m_log.InfoFormat("SyncOutPrimUpdates - tick {0}: START the thread for SyncOutUpdates, {1} prims, ", m_updateTick, m_propertyUpdates.Count);
                    m_updateLoopLogSB = new StringBuilder(m_updateTick.ToString());
                }

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

                            OSDMap syncData;
                            HashSet<string> syncIDs = null;
                            try
                            {
                                syncData = m_SyncInfoManager.EncodeProperties(uuid, updatedProperties);
                                // m_log.WarnFormat("{0}: SyncOutUpdates(): Sending {1} updates for uuid {2}", LogHeader, syncData.Count, uuid);

                                syncIDs = m_SyncInfoManager.GetLastUpdatedSyncIDs(uuid, updatedProperties);

                                //Log encoding delays
                                if (tickLog)
                                {
                                    DateTime encodeEndTime = DateTime.Now;
                                    TimeSpan span = encodeEndTime - startTime;
                                    m_updateLoopLogSB.Append(",update-" +updateIndex+"," + span.TotalMilliseconds.ToString());
                                }

                                if (syncData.Count > 0)
                                {
                                    SymmetricSyncMessage syncMsg = new SymmetricSyncMessage(SymmetricSyncMessage.MsgType.UpdatedProperties, syncData);

                                    //Log encoding delays
                                    if (tickLog)
                                    {
                                        DateTime syncMsgendTime = DateTime.Now;
                                        TimeSpan span = syncMsgendTime - startTime;
                                        m_updateLoopLogSB.Append("," + span.TotalMilliseconds.ToString());
                                    }

                                    HashSet<SyncConnector> syncConnectors = GetSyncConnectorsForUpdates();
                                    // m_log.WarnFormat("{0} SendUpdateToRelevantSyncConnectors: Sending update msg to {1} connectors", LogHeader, syncConnectors.Count);
                                    foreach (SyncConnector connector in syncConnectors)
                                    {
                                        //If the updated properties are from the same actor, the no need to send this sync message to that actor
                                        if (syncIDs.Count == 1)
                                        {
                                            if (syncIDs.Contains(connector.otherSideActorID))
                                            {
                                                m_log.DebugFormat("Skip sending to {0}", connector.otherSideActorID);
                                                continue;
                                            }

                                        }
                                        else
                                        {
                                            string logstr="";
                                            foreach (string sid in syncIDs)
                                            {
                                                logstr += sid+",";
                                            }
                                            m_log.DebugFormat("Updates from {0}", logstr);
                                        }
                                        DetailedUpdateLogging(uuid, updatedProperties, null, "SendUpdate", connector.otherSideActorID, syncMsg.Length);
                                        connector.EnqueueOutgoingUpdate(uuid, syncMsg);
                                    }

                                    //Log encoding delays
                                    if (tickLog)
                                    {
                                        DateTime syncConnectorendTime = DateTime.Now;
                                        TimeSpan span = syncConnectorendTime - startTime;
                                        m_updateLoopLogSB.Append("," + span.TotalMilliseconds.ToString());
                                    }
                                }

                            }
                            catch (Exception e)
                            {
                                m_log.ErrorFormat("{0} Error in EncodeProperties for {1}: {2}", LogHeader, uuid, e.Message);
                            }
                            
                            updateIndex++;
                            
                        }
                    }

                    if (tickLog)
                    {
                        DateTime endTime = DateTime.Now;
                        TimeSpan span = endTime - startTime;
                        m_updateLoopLogSB.Append(", total-span " + span.TotalMilliseconds.ToString());
                        //m_log.InfoFormat("SyncOutUpdates - tick {0}: END the thread for SyncOutUpdates, time span {1}",
                        //    m_updateTick, span.Milliseconds);
                    }

                    // Indicate that the current batch of updates has been completed
                    Interlocked.Exchange(ref m_sendingPropertyUpdates, 0);
                });
            }

            CheckTerrainTainted();
        }

        /// <summary>
        /// Encode a SOG. Values of each part's properties are copied from SyncInfo, instead of from SOP's data. 
        /// If the SyncInfo is not maintained by SyncInfoManager yet, add it first.
        /// </summary>
        /// <param name="sog"></param>
        /// <returns></returns>
        private OSDMap EncodeSceneObject(SceneObjectGroup sog)
        {
            //This should not happen, but we deal with it by inserting a newly created PrimSynInfo
            if (!m_SyncInfoManager.SyncInfoExists(sog.RootPart.UUID))
            {
                m_log.ErrorFormat("{0}: EncodeSceneObject -- SOP {1},{2} not in SyncInfoManager's record yet. Adding.", LogHeader, sog.RootPart.Name, sog.RootPart.UUID);
                m_SyncInfoManager.InsertSyncInfo(sog.RootPart.UUID, DateTime.UtcNow.Ticks, SyncID);
            }

            OSDMap data = new OSDMap();
            data["uuid"] = OSD.FromUUID(sog.UUID);
            data["absPosition"] = OSDMap.FromVector3(sog.AbsolutePosition);
            data["RootPart"] = m_SyncInfoManager.EncodeProperties(sog.RootPart.UUID, sog.RootPart.PhysActor == null ? SyncableProperties.NonPhysActorProperties : SyncableProperties.FullUpdateProperties);

            OSDArray otherPartsArray = new OSDArray();
            foreach (SceneObjectPart part in sog.Parts)
            {
                if (!part.UUID.Equals(sog.RootPart.UUID))
                {
                    if (!m_SyncInfoManager.SyncInfoExists(part.UUID))
                    {
                        m_log.ErrorFormat("{0}: EncodeSceneObject -- SOP {1},{2} not in SyncInfoManager's record yet", 
                                    LogHeader, part.Name, part.UUID);
                        //This should not happen, but we deal with it by inserting a newly created PrimSynInfo
                        m_SyncInfoManager.InsertSyncInfo(part.UUID, DateTime.UtcNow.Ticks, SyncID);
                    }
                    OSDMap partData = m_SyncInfoManager.EncodeProperties(part.UUID, part.PhysActor == null ? SyncableProperties.NonPhysActorProperties : SyncableProperties.FullUpdateProperties);
                    otherPartsArray.Add(partData);
                }
            }
            data["OtherParts"] = otherPartsArray;

            data["IsAttachment"] = OSD.FromBoolean(sog.IsAttachment);
            data["AttachedAvatar"] = OSD.FromUUID(sog.AttachedAvatar);
            data["AttachmentPoint"] = OSD.FromUInteger(sog.AttachmentPoint);

            return data;
        }

        /// <summary>
        /// Decode & create a SOG data structure. Due to the fact that PhysActor
        /// is only created when SOG.AttachToScene() is called, the returned SOG
        /// here only have non PhysActor properties decoded and values set. The
        /// PhysActor properties should be set later by the caller.
        /// </summary>
        /// <param name="data"></param>
        /// <param name="sog"></param>
        /// <param name="syncInfos"></param>
        /// <returns>True of decoding sucessfully</returns>
        private bool DecodeSceneObject(OSDMap data, out SceneObjectGroup sog, out Dictionary<UUID, SyncInfoBase> syncInfos)
        {
            sog = new SceneObjectGroup();
            syncInfos = new Dictionary<UUID, SyncInfoBase>();
            bool ret = true;

            try{
                UUID uuid = ((OSDMap)data["RootPart"])["uuid"].AsUUID();

                OSDMap propertyData = (OSDMap)((OSDMap)data["RootPart"])["propertyData"];
                //m_log.WarnFormat("{0} DecodeSceneObject for RootPart uuid: {1}", LogHeader, uuid);

                //Decode and copy to the list of PrimSyncInfo
                SyncInfoPrim sip = new SyncInfoPrim(uuid, propertyData, Scene);
                SceneObjectPart root = (SceneObjectPart)sip.SceneThing;

                sog.SetRootPart(root);
                sip.SetPropertyValues(SyncableProperties.GroupProperties);
                syncInfos.Add(root.UUID, sip);

                if (sog.UUID == UUID.Zero)
                    sog.UUID = sog.RootPart.UUID;

                //Decode the remaining parts and add them to the object group
                if (data.ContainsKey("OtherParts"))
                {
                    //int otherPartsCount = data["OtherPartsCount"].AsInteger();
                    OSDArray otherPartsArray = (OSDArray)data["OtherParts"];
                    for (int i = 0; i < otherPartsArray.Count; i++)
                    {
                        uuid = ((OSDMap)otherPartsArray[i])["uuid"].AsUUID();
                        propertyData = (OSDMap)((OSDMap)otherPartsArray[i])["propertyData"];

                        //m_log.WarnFormat("{0} DecodeSceneObject for OtherParts[{1}] uuid: {2}", LogHeader, i, uuid);
                        sip = new SyncInfoPrim(uuid, propertyData, Scene);
                        SceneObjectPart part = (SceneObjectPart)sip.SceneThing;

                        if (part == null)
                        {
                            m_log.ErrorFormat("{0} DecodeSceneObject could not decode root part.", LogHeader);
                            sog = null;
                            return false;
                        }
                        sog.AddPart(part);
                        // Should only need to set group properties from the root part, not other parts
                        //sip.SetPropertyValues(SyncableProperties.GroupProperties);
                        syncInfos.Add(part.UUID, sip);
                    }
                }

                // Handled inline above because SyncInfoBase does not have SetGroupProperties.
                /*
                foreach (SceneObjectPart part in sog.Parts)
                {
                    syncInfos[part.UUID].SetGroupProperties(part);
                }
                */

                sog.IsAttachment = data["IsAttachment"].AsBoolean();
                sog.AttachedAvatar = data["AttachedAvatar"].AsUUID();
                uint ap = data["AttachmentPoint"].AsUInteger();
                if (ap != null)
                {
                    if (sog.RootPart == null)
                    {
                        //m_log.WarnFormat("{0} DecodeSceneObject - ROOT PART IS NULL", LogHeader);
                    }
                    else if (sog.RootPart.Shape == null)
                    {
                        //m_log.WarnFormat("{0} DecodeSceneObject - ROOT PART SHAPE IS NULL", LogHeader);
                    }
                    else
                    {
                        sog.AttachmentPoint = ap;
                        //m_log.WarnFormat("{0}: DecodeSceneObject AttachmentPoint = {1}", LogHeader, sog.AttachmentPoint);
                    }
                }
            }
            catch (Exception e)
            {
                m_log.WarnFormat("{0} Encountered an exception: {1} {2} {3}", "DecodeSceneObject", e.Message, e.TargetSite, e.ToString());
                ret = false;
            }
            //else
            //    m_log.WarnFormat("{0}: DecodeSceneObject AttachmentPoint = null", LogHeader);

            return ret;
        }

        #endregion //Prim Property Sync management

        #region Presence Property Sync management

        private void OnScenePresenceUpdated(ScenePresence sp)
        {
            // If the scene presence update event was triggered by a call from RegionSyncModule, then we don't need to handle it.
            // Changes to scene presence that are actually local will not have originated from this module or thread.
            if (IsLocallyGeneratedEvent(SymmetricSyncMessage.MsgType.UpdatedProperties))
                return;

            UUID uuid = sp.UUID;
            //m_log.Warn("OnScenePresenceUpdated A");

            // Sync values with SP data and update timestamp according, to 
            // obtain the list of properties that really have been updated
            // and should be propogated to other sync nodes.
            HashSet<SyncableProperties.Type> propertiesWithSyncInfoUpdated = m_SyncInfoManager.UpdateSyncInfoByLocal(uuid, SyncableProperties.AvatarProperties);
            // string types = "";
            //foreach(SyncableProperties.Type t in propertiesWithSyncInfoUpdated)
            //    types += (t.ToString() + ",");
            //m_log.WarnFormat("OnScenePresenceUpdated B {0}", types);
                

            //Enqueue the set of changed properties
            EnqueueUpdatedProperty(uuid, propertiesWithSyncInfoUpdated);
            //m_log.Warn("OnScenePresenceUpdated C");
        }

        /// <summary>
        /// Encode a SP. Values of each part's properties are copied from SyncInfo, instead of from SP's data. 
        /// If the SyncInfo is not maintained by SyncInfoManager yet, add it first.
        /// </summary>
        /// <param name="sog"></param>
        /// <returns></returns>
        private OSDMap EncodeScenePresence(ScenePresence sp)
        {
            //This should not happen, but we deal with it by inserting it now
            if (!m_SyncInfoManager.SyncInfoExists(sp.UUID))
            {
                m_log.ErrorFormat("{0}: ERROR: EncodeScenePresence -- SP {1},{2} not in SyncInfoManager's record yet. Adding.", LogHeader, sp.Name, sp.UUID);
                m_SyncInfoManager.InsertSyncInfo(sp.UUID, DateTime.UtcNow.Ticks, SyncID);
            }

            OSDMap data = new OSDMap();
            data["uuid"] = OSD.FromUUID(sp.UUID);
            data["absPosition"] = OSDMap.FromVector3(sp.AbsolutePosition);
            data["ScenePresence"] = m_SyncInfoManager.EncodeProperties(sp.UUID, SyncableProperties.AvatarProperties);

            return data;
        }

        #endregion //Presence Property Sync management

        private void EnqueueUpdatedProperty(UUID uuid, HashSet<SyncableProperties.Type> updatedProperties)
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
