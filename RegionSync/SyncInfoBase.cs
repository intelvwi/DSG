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
using System.Text;
using log4net;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Framework;

namespace DSG.RegionSync
{
    public abstract class SyncInfoBase
    {
        private static string LogHeader = "[SYNC INFO BASE]";

        #region Members
        public static long TimeOutThreshold;
        public static ILog DebugLog;

        public long LastUpdateTime { get; protected set; }
        public UUID UUID { get; protected set; }
        public Object SceneThing { get; set; }

        protected Object m_syncLock = new Object();

        // The previous quark the prim resided in
        private SyncQuark m_prevQuark = null;
        public SyncQuark PrevQuark
        {
            get
            {
                return m_prevQuark;
            }
            set { m_prevQuark = value; }
        }

        // The current quark the prim is located in
        private SyncQuark m_curQuark = null;
        public SyncQuark CurQuark
        {
            get
            {
                return m_curQuark;
            }
            set { m_curQuark = value; }
        }
        /// NOTE: CurrentlySyncedProperties should be protected but it's used (without locking) 
        /// by some debug logging in region module
        public Dictionary<SyncableProperties.Type, SyncedProperty> CurrentlySyncedProperties { get; set; }

        protected Scene Scene;

        protected static bool ByteArrayEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                    return false;
            }
            return true;
        }

        public static SyncInfoBase SyncInfoFactory(UUID uuid, Scene scene, long lastUpdateTimeStamp, string syncID)
        {
            ScenePresence sp = scene.GetScenePresence(uuid);
            if (sp != null)
            {
                return new SyncInfoPresence(sp, lastUpdateTimeStamp, syncID, scene);
            }

            SceneObjectPart sop = scene.GetSceneObjectPart(uuid);
            if (sop != null)
            {
                return new SyncInfoPrim(sop, lastUpdateTimeStamp, syncID, scene);
            }

            DebugLog.WarnFormat("{0}: SyncInfoFactory could not find uuid {1} in local scene.", LogHeader, uuid);
            return null;
        }

        #endregion //Members

        /// <summary>
        /// Encode the SyncInfo of each property, including its current value 
        /// maintained in this SyncModule, its timestamp and syncID.
        /// </summary>
        /// <param name="propertiesToSync">The list of properties to be encoded. 
        /// If FullUpdate is included, then encode all properties.</param>
        /// <returns></returns>
        public OSDMap EncodeSyncedProperties(HashSet<SyncableProperties.Type> propertiesToSync)
        {
            OSDMap propertyData = new OSDMap();

            //Lock first, so that we effectively freeze the record and take a snapshot
            lock (m_syncLock)
            {
                foreach (SyncableProperties.Type ptype in propertiesToSync)
                {
                    SyncedProperty prop;
                    if (CurrentlySyncedProperties.TryGetValue(ptype, out prop))
                    {
                        propertyData.Add(ptype.ToString(), prop.ToOSDMap());
                    }
                    else
                    {
                        DebugLog.ErrorFormat("{0}: EncodeSyncedProperties: property {1} not in sync cache", LogHeader, ptype);
                    }
                }
            }
            return propertyData;
        }

        public HashSet<string> GetLastUpdateSyncIDs(HashSet<SyncableProperties.Type> propertiesToSync)
        {
            HashSet<string> syncIDs = new HashSet<string>();
            SyncedProperty prop;
            lock (m_syncLock)
            {
                foreach (SyncableProperties.Type ptype in propertiesToSync)
                {
                    if (CurrentlySyncedProperties.TryGetValue(ptype, out prop))
                    {
                        syncIDs.Add(prop.LastUpdateSyncID);
                    }
                }
            }
            return syncIDs;
        }

        public abstract HashSet<SyncableProperties.Type> UpdatePropertiesByLocal(UUID uuid, HashSet<SyncableProperties.Type> updatedProperties, long lastUpdateTS, string syncID);

        //TODO: might return status such as Updated, Unchanged, etc to caller
        public HashSet<SyncableProperties.Type> UpdatePropertiesBySync(UUID uuid, HashSet<SyncedProperty> syncedProperties)
        {
            long recvTS = DateTime.UtcNow.Ticks;
            HashSet<SyncableProperties.Type> propertiesUpdated = new HashSet<SyncableProperties.Type>();

            lock (m_syncLock)
            {
                foreach (SyncedProperty syncedProperty in syncedProperties)
                {
                    bool updated = false;
                    SyncableProperties.Type property = syncedProperty.Property;
                    //Compare if the value of the property in this SyncInfo is different than the value in local scene
                    if (!CurrentlySyncedProperties.ContainsKey(property))
                    {
                        //could happen if PhysActor is just created (object stops being phantom)
                        if (SyncableProperties.PhysActorProperties.Contains(property))
                        {
                            SyncedProperty syncInfo = new SyncedProperty(syncedProperty);
                            CurrentlySyncedProperties.Add(property, syncInfo);
                        }
                        else
                        {
                            DebugLog.WarnFormat("{0}: UpdatePropertiesBySync: No record of property {1} for uuid {2}", LogHeader, property, uuid);
                        }
                    }
                    else
                    {
                        try
                        {
                            //Compare timestamp and update SyncInfo if necessary
                            updated = CurrentlySyncedProperties[property].CompareAndUpdateSyncInfoBySync(syncedProperty, recvTS);
                            //If updated, update the property value in scene object/presence
                            if (updated)
                            {
                                SetPropertyValue(property);
                                propertiesUpdated.Add(property);
                            }
                        }
                        catch (Exception e)
                        {
                            DebugLog.ErrorFormat("{0}: UpdatePropertiesBySync: Error in updating property {1}: {2}", LogHeader, property, e.Message);
                        }
                    }
                }
            }
            
            PostUpdateBySync(propertiesUpdated);

            return propertiesUpdated;
        }

        public abstract void PostUpdateBySync(HashSet<SyncableProperties.Type> updatedProperties);

        public abstract int Size { get; }

        public void SetPropertyValues(HashSet<SyncableProperties.Type> properties)
        {
            foreach (SyncableProperties.Type property in properties)
                SetPropertyValue(property);
        }

        // When this is called, the SyncInfo should already have a reference to the scene object it will be updating
        public abstract void SetPropertyValue(SyncableProperties.Type property);

        public abstract Object GetPropertyValue(SyncableProperties.Type property);
    }
}
