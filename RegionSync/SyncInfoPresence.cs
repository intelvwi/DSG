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
using OpenSim.Region.Framework.Scenes.Animation;
using OpenSim.Framework;
using System.Diagnostics;

namespace DSG.RegionSync
{
    public class SyncInfoPresence : SyncInfoBase
    {
        private static string LogHeader = "[SYNC INFO PRESENCE]";

        #region Constructors

        /// Constructor used for initializing SyncInfo from local (Scenepresence) data before syncing it out
        /// <param name="sp">Presence to use for initial synced property values</param>
        /// <param name="initUpdateTimestamp">Initial update timestamp</param>
        /// <param name="syncID"></param>
        /// <param name="scene">The local scene</param>
        public SyncInfoPresence(ScenePresence sp, long initUpdateTimestamp, string syncID, Scene scene)
        {
            // DebugLog.WarnFormat("[SYNC INFO PRESENCE] Constructing SyncInfoPresence (from scene) for uuid {0}", sp.UUID);

            UUID = sp.UUID;
            Scene = scene;
            SceneThing = sp;
            
            lock (m_syncLock)
            {
                CurrentlySyncedProperties = new Dictionary<SyncableProperties.Type, SyncedProperty>();
                foreach (SyncableProperties.Type property in SyncableProperties.AvatarProperties)
                {
                    Object initValue = GetPropertyValue(sp, property);
                    if (initValue != null)
                    {
                        SyncedProperty syncInfo = new SyncedProperty(property, initValue, initUpdateTimestamp, syncID);
                        CurrentlySyncedProperties.Add(property, syncInfo);
                    }
                }
            }
        }

        // Constructor used for initializing SyncInfo from remote (OSDMap) data before syncing it locally
        /// <param name="id">UUID of the scene presence</param>
        /// <param name="syncInfoData">Initial sync data</param>
        /// <param name="scene">The local scene</param>
        public SyncInfoPresence(UUID id, OSDMap syncInfoData, Scene scene)
        {
            // DebugLog.WarnFormat("[SYNC INFO PRESENCE] Constructing SyncInfoPresence (from map) for uuid {0}", id);

            UUID = id;
            Scene = scene;

            lock (m_syncLock)
            {
                CurrentlySyncedProperties = new Dictionary<SyncableProperties.Type, SyncedProperty>();
                foreach (SyncableProperties.Type property in SyncableProperties.AvatarProperties)
                {
                    if (syncInfoData.ContainsKey(property.ToString()))
                    {
                        SyncedProperty syncedProperty = new SyncedProperty(property, (OSDMap)syncInfoData[property.ToString()]);
                        CurrentlySyncedProperties.Add(property, syncedProperty);
                    }
                    else
                    {
                        DebugLog.ErrorFormat("[SYNC INFO PRESENCE] SyncInfoPresence: Property {0} not included in the given OSDMap", property);
                    }
                }
            }
        }
        
        #endregion //Constructors

        public override int Size
        {
            get
            {
                int estimateBytes = 0;
                return estimateBytes;
            }
        }

        //Triggered when a set of local writes just happened, and ScheduleFullUpdate 
        //or ScheduleTerseUpdate has been called.
        /// <summary>
        /// Update copies of the given list of properties in the SyncInfo.
        /// </summary>
        /// <param name="uuid"></param>
        /// <param name="updatedProperties"></param>
        /// <param name="lastUpdateTS"></param>
        /// <param name="syncID"></param>
        public override HashSet<SyncableProperties.Type> UpdatePropertiesByLocal(UUID uuid, HashSet<SyncableProperties.Type> updatedProperties, long lastUpdateTS, string syncID)
        {
            // DebugLog.WarnFormat("[SYNC INFO PRESENCE] UpdatePropertiesByLocal: uuid={0}", uuid);
            ScenePresence sp = Scene.GetScenePresence(uuid);

            if (sp == null)
            {
                // DebugLog.WarnFormat("[SYNC INFO PRESENCE] UpdatePropertiesByLocal uuid {0} not found in scene", uuid);
                return new HashSet<SyncableProperties.Type>();
            }

            //Second, for each updated property in the list, find out the ones that really have recently been updated by local operations
            HashSet<SyncableProperties.Type> propertiesUpdatedByLocal = new HashSet<SyncableProperties.Type>();

            lock (m_syncLock)
            {
                foreach (SyncableProperties.Type property in updatedProperties)
                {
                    if (CompareValue_UpdateByLocal(sp, property, lastUpdateTS, syncID))
                    {
                        propertiesUpdatedByLocal.Add(property);
                    }
                }
            }

            // string debugprops = "";
            // foreach (SyncableProperties.Type p in propertiesUpdatedByLocal)
            //     debugprops += p.ToString() + ",";
            // DebugLog.DebugFormat("[SYNC INFO PRESENCE] UpdatePropertiesByLocal ended for {0}. propertiesUpdatedByLocal.Count = {1}: {2}", sp.UUID, propertiesUpdatedByLocal.Count, debugprops);

            return propertiesUpdatedByLocal;
        }

        public void SetPhyscActorProperties(ScenePresence sp)
        {
            foreach (SyncableProperties.Type property in SyncableProperties.PhysActorProperties)
            {
                SetPropertyValue(sp, property);
            }
        }

        const float ROTATION_TOLERANCE = 0.01f;
        const float VELOCITY_TOLERANCE = 0.001f;
        const float POSITION_TOLERANCE = 0.05f;

        /// <summary>
        /// Compare the value (not "reference") of the given property. 
        /// Assumption: the caller has already checked if PhysActor exists
        /// if there are physics properties updated.
        /// If the value maintained here is different from that in SP data,
        /// synchronize the two: 
        /// (1) if the cached value has a timestamp newer than lastUpdateByLocalTS 
        /// overwrite the SP's property with the cached value (effectively 
        /// undoing the local write operation that just happened). 
        /// (2) otherwise, copy SP's data and update timestamp and syncID 
        /// as indicated by "lastUpdateByLocalTS" and "syncID".
        /// </summary>
        /// <param name="sp"></param>
        /// <param name="property"></param>
        /// <param name="lastUpdateByLocalTS"></param>
        /// <param name="syncID"></param>
        /// <returns>Return true if the property's value maintained in this 
        /// RegionSyncModule is replaced by SP's data.</returns>
        private bool CompareValue_UpdateByLocal(ScenePresence sp, SyncableProperties.Type property, long lastUpdateByLocalTS, string syncID)
        {
            //DebugLog.WarnFormat("[SYNC INFO PRESENCE] CompareValue_UpdateByLocal: Updating property {0} on sp {1}", property.ToString(), sp.UUID);
            // Check to see if this property is in the sync cache for this object.
            // If not, add it and initialize value to value in ScenePresence.
            bool ret = false;
            if (!CurrentlySyncedProperties.ContainsKey(property))
            {
                Object initValue = GetPropertyValue(sp, property);
                if (initValue != null)
                {
                    SyncedProperty syncInfo = new SyncedProperty(property, initValue, lastUpdateByLocalTS, syncID);
                    CurrentlySyncedProperties.Add(property, syncInfo);
                    ret = true;
                }
                return ret;
            }

            // First, check if the value maintained here is different from that in SP's. 
            // If different, next check if the timestamp in SyncInfo is newer than lastUpdateByLocalTS; 
            // if so (although ideally should not happen, but due to things likc clock not so perfectly 
            // sync'ed, it might happen), overwrite SP's value with what's maintained
            // in SyncInfo; otherwise, copy SP's data to SyncInfo.

            SyncedProperty syncedProperty = CurrentlySyncedProperties[property];
            Object value = GetPropertyValue(sp, property);

            // If both null, no update needed
            if (syncedProperty.LastUpdateValue == null && value == null)
                return false;

            switch (property)
            {
                default:
                    // If one is null and the other is not, or if the references are different, the property was changed.
                    // This will perform a value comparison for strings in C#. We could use String.Clone instead for string properties.
                    if ((value == null && syncedProperty.LastUpdateValue != null) ||
                        (value != null && syncedProperty.LastUpdateValue == null) ||
                        (!value.Equals(syncedProperty.LastUpdateValue)))
                    {
                        if (value != null)
                        {
                            // Some values, even if they are not 'equal', might be close enough to be equal.
                            // Note that the 'Equals()' above will most always return 'false' for lists and OSDMaps
                            //     since they are probably not the same object.
                            // Returning a 'false' here means the values don't need any updating (they are equal enough).
                            switch (property)
                            {
                                case SyncableProperties.Type.AvatarAppearance:
                                    String stringValue = OSDParser.SerializeJsonString((OSDMap)value);
                                    String lastStringValue = OSDParser.SerializeJsonString((OSDMap)syncedProperty.LastUpdateValue);
                                    if (stringValue == lastStringValue)
                                        return false;
                                    break;
                                case SyncableProperties.Type.Animations:
                                    if (syncedProperty.LastUpdateValue != null)
                                    {
                                        AnimationSet lastAnimations = new AnimationSet((OSDArray)syncedProperty.LastUpdateValue);

                                        // Get the home region for this presence (the client manager the presence is connected to).
                                        string cachedRealRegionName = (string)(CurrentlySyncedProperties[SyncableProperties.Type.RealRegion].LastUpdateValue);
                                        if (cachedRealRegionName != Scene.Name && sp.Animator.Animations.ToArray().Length == 0)
                                        {
                                            // If this is not the originating region for this presence or there is no additional
                                            //   animations being added, this simulator does not change the animation.
                                            // THIS IS A HORRIBLE KLUDGE. FIGURE OUT THE REAL SOLUTION!!
                                            // The problem is that animations are changed by every simulator (setting default
                                            //   sit and stand when parentID changes) and the updates conflict/override the real
                                            //   settings (like a scripted sit animation).
                                            // DebugLog.DebugFormat("{0} CompareValue_UpdateByLocal. Not home sim or no anim change. spID={1}, homeSim={2}, thisSim={3}, anims={4}",
                                            //                     LogHeader, sp.LocalId, cachedRealRegionName, Scene.Name, sp.Animator.Animations.ToArray().Length); // DEBUG DEBUG

                                            return false;
                                        }

                                        if (lastAnimations.Equals(sp.Animator.Animations))
                                        {
                                            // DebugLog.DebugFormat("{0} CompareValue_UpdateByLocal. Equal anims. spID={1}, sp.Anim={2}, lastAnim={3}",
                                            //                     LogHeader, sp.LocalId, sp.Animator.Animations, lastAnimations); // DEBUG DEBUG
                                            return false;
                                        }
                                        else
                                        {
                                            // DebugLog.DebugFormat("{0} CompareValue_UpdateByLocal. Not equal anims. spID={1}, sp.Anim={2}, lastAnim={3}",
                                            //                     LogHeader, sp.LocalId, sp.Animator.Animations, lastAnimations); // DEBUG DEBUG
                                        }
                                    }
                                    break;
                                case SyncableProperties.Type.Velocity:
                                case SyncableProperties.Type.PA_Velocity:
                                case SyncableProperties.Type.PA_TargetVelocity:
                                case SyncableProperties.Type.RotationalVelocity:
                                case SyncableProperties.Type.AngularVelocity:
                                    {
                                        Vector3 partVal = (Vector3)value;
                                        Vector3 lastVal = (Vector3)syncedProperty.LastUpdateValue;
                                        // If velocity difference is small but not zero, don't update
                                        if (partVal.ApproxEquals(lastVal, VELOCITY_TOLERANCE) && !partVal.Equals(Vector3.Zero))
                                            return false;
                                        break;
                                    }
                                case SyncableProperties.Type.RotationOffset:
                                case SyncableProperties.Type.Orientation:
                                    {
                                        Quaternion partVal = (Quaternion)value;
                                        Quaternion lastVal = (Quaternion)syncedProperty.LastUpdateValue;
                                        if (partVal.ApproxEquals(lastVal, ROTATION_TOLERANCE))
                                            return false;
                                        break;
                                    }
                                case SyncableProperties.Type.OffsetPosition:
                                case SyncableProperties.Type.AbsolutePosition:
                                case SyncableProperties.Type.Position:
                                case SyncableProperties.Type.GroupPosition:
                                    {
                                        Vector3 partVal = (Vector3)value;
                                        Vector3 lastVal = (Vector3)syncedProperty.LastUpdateValue;
                                        if (partVal.ApproxEquals(lastVal, POSITION_TOLERANCE))
                                            return false;
                                        break;
                                    }
                            }
                        }

                        // If we get here, the values are not equal and we need to update the cached value if the
                        //     new value is timestamp newer.
                        if (lastUpdateByLocalTS >= syncedProperty.LastUpdateTimeStamp)
                        {
                            // DebugLog.DebugFormat("{0} CompareValue_UpdateByLocal (property={1}): TS >= lastTS (updating SyncInfo)", LogHeader, property);
                            CurrentlySyncedProperties[property].UpdateSyncInfoByLocal(lastUpdateByLocalTS, syncID, value);
                            return true;
                        }
                    }
                    break;
            }
            return false;
        }

        public override void PostUpdateBySync(HashSet<SyncableProperties.Type> updatedProperties)
        {
            ScenePresence sp = (ScenePresence)SceneThing;
            if (sp == null)
            {
                DebugLog.ErrorFormat("{0}: PostUpdateBySync: update properties received before new scene presence created. UUID={1}",
                        LogHeader, this.UUID.ToString());
                return;
            }

            // Here is where we may need to force sending of updated properties, appearance, etc
            sp.Updated = true;
        }

        public override Object GetPropertyValue(SyncableProperties.Type property)
        {
            return GetPropertyValue((ScenePresence)SceneThing, property);
        }

        //The input, two ODSMaps, are assumed to be packed by AvatarAppearance.Pack(),
        //that is, they each have the fields:
        //serial
        //height
        //wearables
        //textures
        //visualparams
        //attachments
        private bool PropertyValueEquals_AvatarAppearance(OSDMap sceneValue, OSDMap syncValue)
        {
            
            if (sceneValue.ContainsKey("serial") && syncValue.ContainsKey("serial"))
            {
                if (!sceneValue["serial"].AsInteger().Equals(syncValue["serial"].AsInteger()))
                    return false;
            }

            if (sceneValue.ContainsKey("height") && syncValue.ContainsKey("height"))
            {
                if (!sceneValue["height"].AsReal().Equals(syncValue["height"].AsReal()))
                    return false;
            }

            if (sceneValue.ContainsKey("wearables") && syncValue.ContainsKey("wearables"))
            {
                OSDArray sceneWears = (OSDArray)sceneValue["wearables"];
                OSDArray syncWears = (OSDArray)syncValue["wearables"];

                if (sceneWears.Count != syncWears.Count)
                    return false;

                if (!sceneWears.ToString().Equals(syncWears.ToString()))
                    return false;
            }

            if (sceneValue.ContainsKey("textures") && syncValue.ContainsKey("textures"))
            {
                OSDArray sceneTextures = (OSDArray)sceneValue["textures"];
                OSDArray syncTextures = (OSDArray)syncValue["textures"];

                if (sceneTextures.Count != syncTextures.Count)
                    return false;

                if (!sceneTextures.ToString().Equals(syncTextures.ToString()))
                    return false;
            }

            if (sceneValue.ContainsKey("visualparams") && syncValue.ContainsKey("visualparams"))
            {
                OSDBinary sceneTextures = (OSDBinary)sceneValue["visualparams"];
                OSDBinary syncTextures = (OSDBinary)syncValue["visualparams"];

                byte[] sceneBytes = sceneTextures.AsBinary();
                byte[] syncBytes = syncTextures.AsBinary();
                if (sceneBytes.Length != syncBytes.Length)
                    return false;
                for (int i = 0; i < sceneBytes.Length; i++)
                {
                    if (!sceneBytes[i].Equals(syncBytes[i]))
                        return false;
                }
            }

            if (sceneValue.ContainsKey("attachments") && syncValue.ContainsKey("attachments"))
            {
                OSDArray sceneAttachs = (OSDArray)sceneValue["attachments"];
                OSDArray syncAttachs = (OSDArray)syncValue["attachments"];

                if (sceneAttachs.Count != syncAttachs.Count)
                    return false;

                if (!sceneAttachs.ToString().Equals(syncAttachs.ToString()))
                    return false;
            }

            return true;
        }

        // Gets the value out of the SP in local scene and returns it as an object
        private Object GetPropertyValue(ScenePresence sp, SyncableProperties.Type property)
        {
            if (sp == null) 
                return null;

            switch (property)
            {
                case SyncableProperties.Type.LocalId:
                    return sp.LocalId;
                case SyncableProperties.Type.AbsolutePosition:
                    return sp.AbsolutePosition;
                case SyncableProperties.Type.AgentCircuitData:
                    return Scene.AuthenticateHandler.GetAgentCircuitData(sp.ControllingClient.CircuitCode).PackAgentCircuitData();
                case SyncableProperties.Type.ParentId:
                    return sp.ParentID;
                case SyncableProperties.Type.AgentControlFlags:
                    return sp.AgentControlFlags;
                case SyncableProperties.Type.AllowMovement:
                    return sp.AllowMovement;
                case SyncableProperties.Type.Animations:
                    return sp.Animator.Animations.ToOSDArray();
                case SyncableProperties.Type.AvatarAppearance:
                    return sp.Appearance.Pack();
                case SyncableProperties.Type.Rotation:
                    return sp.Rotation;
                case SyncableProperties.Type.PA_Velocity:
                    if (sp.PhysicsActor == null)
                        return Vector3.Zero;
                    return sp.PhysicsActor.Velocity;
                case SyncableProperties.Type.RealRegion:
                    // Always just the local scene name the avatar is in when requested locally. 
                    return sp.Scene.Name;
                case SyncableProperties.Type.PA_TargetVelocity:
                    if (sp.PhysicsActor == null)
                        return Vector3.Zero;
                    return sp.PhysicsActor.TargetVelocity;
                case SyncableProperties.Type.Flying:
                    return sp.Flying;
                case SyncableProperties.Type.PresenceType:
                    return (int)sp.PresenceType;
                case SyncableProperties.Type.IsColliding:
                    return sp.IsColliding;
            }

            //DebugLog.ErrorFormat("{0}: GetPropertyValue could not get property {1} from {2}", LogHeader, property.ToString(), sp.UUID);
            return null;
        }

        public override void SetPropertyValue(SyncableProperties.Type property)
        {
            ScenePresence sp = (ScenePresence)SceneThing;
            SetPropertyValue(sp, property);
        }

        /// <summary>
        /// This function should only be triggered when an update update is received (i.e. 
        /// triggered by remote update instead of local update).
        /// </summary>
        /// <param name="sp"></param>
        /// <param name="property"></param>
        private void SetPropertyValue(ScenePresence sp, SyncableProperties.Type property)
        {
            if (sp == null) return;

            if (!CurrentlySyncedProperties.ContainsKey(property))
            {
                DebugLog.ErrorFormat("{0}: SetPropertyValue: property {1} not in sync cache for uuid {2}. ", LogHeader, property, UUID);
                return;
            }

            SyncedProperty pSyncInfo = CurrentlySyncedProperties[property];
            Object pValue = pSyncInfo.LastUpdateValue;
            switch (property)
            {
                case SyncableProperties.Type.LocalId:
                    sp.LocalId = (uint)pValue;
                    break;
                case SyncableProperties.Type.AbsolutePosition:
                    sp.AbsolutePosition = (Vector3)pValue;
                    break;
                case SyncableProperties.Type.AgentCircuitData:
                    DebugLog.WarnFormat("{0}: Received updated AgentCircuitData. Not implemented", LogHeader);
                    break;
                case SyncableProperties.Type.ParentId:
                    uint localID = (uint)pValue;
                    if (localID == 0)
                    {
                        // DebugLog.DebugFormat("{0}: SetPropertyValue:ParentID. Standup. Input={1}", LogHeader, localID); // DEBUG DEBUG
                        sp.StandUp();
                    }
                    else
                    {
                        SceneObjectPart parentPart = Scene.GetSceneObjectPart(localID);
                        if (parentPart != null) // TODO ??
                        {
                            sp.HandleAgentRequestSit(sp.ControllingClient, sp.ControllingClient.AgentId, parentPart.UUID, Vector3.Zero);
                            // DebugLog.DebugFormat("{0}: SetPropertyValue:ParentID. SitRequest. Input={1},sp={2},newParentID={3}",
                            //                 LogHeader, localID, (string)(sp == null ? "NULL" : sp.Name), sp.ParentID); // DEBUG DEBUG
                        }
                    }
                    //sp.ParentID = (uint)pValue;
                    break;
                case SyncableProperties.Type.AgentControlFlags:
                    sp.AgentControlFlags = (uint)pValue;
                    break;
                case SyncableProperties.Type.AllowMovement:
                    sp.AllowMovement = (bool)pValue;
                    break;
                case SyncableProperties.Type.AvatarAppearance:
                    sp.Appearance.Unpack((OSDMap)pValue);
                    break;
                case SyncableProperties.Type.Animations:
                    UpdateAvatarAnimations(sp, (OSDArray)pValue);
                    break;
                case SyncableProperties.Type.Rotation:
                    sp.Rotation = (Quaternion)pValue;
                    break;
                case SyncableProperties.Type.PA_Velocity:
                    if (sp.PhysicsActor != null)
                        sp.PhysicsActor.Velocity = (Vector3)pValue;
                    break;
                case SyncableProperties.Type.RealRegion:
                    ////// NOP //////
                    break;
                case SyncableProperties.Type.PA_TargetVelocity:
                    if(sp.PhysicsActor != null)
                        sp.PhysicsActor.TargetVelocity = (Vector3)pValue;
                     break;
                case SyncableProperties.Type.Flying:
                    sp.Flying = (bool)pValue;
                    break;
                case SyncableProperties.Type.PresenceType:
                    DebugLog.Warn("[SYNC INFO PRESENCE] Received updated PresenceType. Not implemented");
                    break;
                case SyncableProperties.Type.IsColliding:
                    if(sp.PhysicsActor != null)
                        sp.IsColliding = (bool)pValue;
                    break;
            }

            // When presence values are changed, we tell the simulator with an event
            GenerateAgentUpdated(sp);
        }

        // Received a list of animations for this avatar. Check to see if animation list has
        //   changed and update the scene presence.
        // Doing any updates to the Animator causes events to be sent out so don't change willy nilly.
        // Changes to the animation set must be done through sp.Animator so the proper side
        //   effects happen and updates are sent out.
        private void UpdateAvatarAnimations(ScenePresence sp, OSDArray pPackedAnimations)
        {
            AnimationSet newSet = new AnimationSet(pPackedAnimations);
            AnimationSet currentSet = sp.Animator.Animations;
            if (!newSet.Equals(currentSet))
            {
                // DebugLog.DebugFormat("{0} UpdateAvatarAnimations. spID={1},CurrAnims={2},NewAnims={3}",
                //                          LogHeader, sp.LocalId, currentSet, newSet); // DEBUG DEBUG

                // If something changed, stuff the new values in the existing animation collection.
                sp.Animator.Animations.FromOSDArray(pPackedAnimations);
            }
            // Doesn't matter if it changed or not. If someone sends us an animation update, tell any connected client.
            sp.Animator.SendAnimPack();
        }

        // Some presence property has changed. Generate a call into the scene presence
        // so the new values are evaluated (like AgentControlFlags).
        // The ScenePresence will trigger OnScenePresenceUpdated and we rely on the
        // fact that the values will all be equal to supress the generation of a
        // new outgoing property update message.
        private void GenerateAgentUpdated(ScenePresence sp)
        {
            // The call for the change of these values comes out of the client view
            // which has an OnAgentUpdate event that the scene presence connects to.
            // We can't use the OnAgentUpdate event subscription (we're not derived
            // from client view) so we fake the reception of a presenece changing
            // message by building up the parameter block and directly calling the
            // ScenePresence's handling routine.
            AgentUpdateArgs aua = new AgentUpdateArgs();

            aua.AgentID = sp.UUID;
            aua.BodyRotation = sp.Rotation;
            aua.CameraAtAxis = sp.CameraAtAxis;
            aua.CameraCenter = sp.CameraPosition;
            aua.CameraLeftAxis = sp.CameraLeftAxis;
            aua.CameraUpAxis = sp.CameraUpAxis;
            aua.ClientAgentPosition = sp.AbsolutePosition;
            aua.ControlFlags = sp.AgentControlFlags;
            aua.Far = sp.DrawDistance;
            aua.Flags = 0;
            aua.HeadRotation = sp.Rotation; // this is wrong but the only thing we can do
            aua.State = sp.State;
            aua.UseClientAgentPosition = true;

            sp.HandleAgentUpdate(null, aua);
        }
    }
}
