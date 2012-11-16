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
using System.Linq;
using System.Reflection;
using System.Text;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Interfaces;
using log4net;

namespace DSG.RegionSync
{
    public class TerrainSyncInfo
    {
        public string ActorID { get; set; }
        public long LastUpdateTimeStamp { get; set; }
        public string LastUpdateActorID { get; set; }
        public Object LastUpdateValue { get; set; }
        public String LastUpdateValueHash { get; set; }

        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private Scene Scene { get; set; }
        private ITerrainModule TerrainModule { get; set; }

        public TerrainSyncInfo(Scene scene, string actorID)
        {
            Scene = scene;
            LastUpdateValue = Scene.Heightmap.SaveToXmlString();
            ActorID = actorID;

            TerrainModule = scene.RequestModuleInterface<ITerrainModule>();
            if (TerrainModule == null)
                throw (new NullReferenceException("Could not get a reference to terrain module for region \"" + Scene.RegionInfo.RegionName + "\""));
            // Initialize time stamp to 0. Any changes anywhere will cause an update after initial load.
            LastUpdateTimeStamp = 0;
        }

        private void SyncInfoUpdate(long timeStamp, string actorID)
        {
            LastUpdateTimeStamp = timeStamp;
            LastUpdateActorID = actorID;

           // m_log.DebugFormat("TerrainModule: updated syncinfo -- TS {0}, actorID {1}", m_lastUpdateTimeStamp, m_lastUpdateActorID);
        }

        /// <summary>
        /// Invoked by receiving a terrain sync message. First, check if the
        /// timestamp is more advance than the local copy. If so, update the
        /// local terrain copy, update the sync info (timestamp and actorID).
        /// <param name="timeStamp"></param>
        /// <param name="actorID"></param>
        /// <param name="terrainData"></param>
        /// <returns></returns>
        public bool UpdateTerrianBySync(long timeStamp, string actorID, string terrainData)
        {
            // If update is outdated, ignore it.
            if (timeStamp < LastUpdateTimeStamp)
            {
                m_log.WarnFormat("[TERRAIN SYNC INFO] UpdateTerrianBySync received outdated terrain update.");
                return false;
            }
                
            LastUpdateTimeStamp = timeStamp;
            LastUpdateActorID = actorID;
            LastUpdateValue = terrainData;
                
            Scene.Heightmap.LoadFromXmlString(terrainData);
            // Causes CheckForTerrainUpdates to be called and updates to get sent to clients
            TerrainModule.TaintTerrain();
            return true;
        }

        public bool TerrianModifiedLocally(string localActorID)
        {
            if (localActorID == LastUpdateActorID)
                return true;
            return false;
        }
    }
}
