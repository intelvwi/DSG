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
    public class SyncInfoPrim : SyncInfoBase
    {
        private static string LogHeader = "[SYNC INFO PRIM]";

        #region Constructors

        // Constructor used for initializing SyncInfo from local (SceneObjectPart) data before syncing it out
        /// <param name="sop">Part to use for initial synced property values</param>
        /// <param name="initUpdateTimestamp">Initial update timestamp</param>
        /// <param name="syncID"></param>
        /// <param name="scene"></param>
        public SyncInfoPrim(SceneObjectPart sop, long initUpdateTimestamp, string syncID, Scene scene)
        {
            UUID = sop.UUID;
            Scene = scene;
            SceneThing = sop;

            //DebugLog.WarnFormat("{0}: Constructing SyncInfoPrim (from scene) for uuid {1}", LogHeader, UUID);

            // If the part does not have a PhysActor then don't store physical property initial values
            HashSet<SyncableProperties.Type> initialProperties = sop.PhysActor == null ?
                new HashSet<SyncableProperties.Type>(SyncableProperties.NonPhysActorProperties) :
                new HashSet<SyncableProperties.Type>(SyncableProperties.FullUpdateProperties);
            lock (m_syncLock)
            {
                CurrentlySyncedProperties = new Dictionary<SyncableProperties.Type, SyncedProperty>();
                foreach (SyncableProperties.Type property in initialProperties)
                {
                    Object initValue = GetPropertyValue(sop, property);
                    SyncedProperty syncInfo = new SyncedProperty(property, initValue, initUpdateTimestamp, syncID);
                    CurrentlySyncedProperties.Add(property, syncInfo);
                }
            }
        }

        // Constructor used for initializing SyncInfo from remote (OSDMap) data before syncing it locally
        public SyncInfoPrim(UUID id, OSDMap syncInfoData, Scene scene)
        {
            UUID = id;
            Scene = scene;

            // Create an SOP based on the set of decoded properties
            // The SOP will be stored internally and someone will add it to the scene later
            SceneThing = new SceneObjectPart();
            ((SceneObjectPart)SceneThing).UUID = UUID;

            //DebugLog.WarnFormat("{0}: Constructing SyncInfoPrim (from map) for uuid {1}", LogHeader, UUID);

            lock (m_syncLock)
            {
                // First decode syncInfoData into CurrentlySyncedProperties
                CurrentlySyncedProperties = new Dictionary<SyncableProperties.Type, SyncedProperty>();
                HashSet<SyncableProperties.Type> full = SyncableProperties.FullUpdateProperties;
                //if (!full.Contains(SyncableProperties.Type.Position))
                //    DebugLog.ErrorFormat("{0} SyncableProperties.FullUpdateProperties missing Position!", LogHeader);
                foreach (SyncableProperties.Type property in SyncableProperties.FullUpdateProperties)
                {
                    //if(property == SyncableProperties.Type.Position)
                    //    DebugLog.WarnFormat("{0} {1} {2} {3}", LogHeader, UUID, property, syncInfoData.ContainsKey(property.ToString()));
                    if (syncInfoData.ContainsKey(property.ToString()))
                    {
                        SyncedProperty syncedProperty = new SyncedProperty(property, (OSDMap)syncInfoData[property.ToString()]);
                        //DebugLog.WarnFormat("{0}: Adding property {1} to CurrentlySyncedProperties", LogHeader, property);
                        CurrentlySyncedProperties.Add(property, syncedProperty);
                        try
                        {
                            SetPropertyValue((SceneObjectPart)SceneThing, property);
                        }
                        catch (Exception e)
                        {
                            DebugLog.ErrorFormat("{0}: Error setting SOP property {1}: {2}", LogHeader, property, e.Message);
                        }
                    }
                    else
                    {
                        // For Phantom prims, they don't have PhysActor properties. So this branch could happen. 
                        // Should ensure we're dealing with a phantom prim.
                        //DebugLog.WarnFormat("{0}: Property {1} not included in OSDMap passed to constructor", LogHeader, property);
                    }
                }
            }
        }

        #endregion //Constructors

        //Triggered when a set of local writes just happened, and ScheduleFullUpdate 
        //or ScheduleTerseUpdate has been called.
        /// <summary>
        /// Update copies of the given list of properties in the prim's SyncInfo.
        /// </summary>
        /// <param name="part"></param>
        /// <param name="updatedProperties"></param>
        /// <param name="lastUpdateTS"></param>
        /// <param name="syncID"></param>
        public override HashSet<SyncableProperties.Type> UpdatePropertiesByLocal(UUID uuid, HashSet<SyncableProperties.Type> updatedProperties, long lastUpdateTS, string syncID)
        {
            // DebugLog.WarnFormat("[SYNC INFO PRIM] UpdatePropertiesByLocal uuid={0}, updatedProperties.Count={1}", uuid, updatedProperties.Count);
            SceneObjectPart part = (SceneObjectPart)SceneThing;

            // For each updated property, find out which ones really differ from values in SyncedProperty
            HashSet<SyncableProperties.Type> propertiesUpdatedByLocal = new HashSet<SyncableProperties.Type>();
            lock (m_syncLock)
            {
                foreach (SyncableProperties.Type property in updatedProperties)
                {
                    if (CompareValue_UpdateByLocal(part, property, lastUpdateTS, syncID))
                    {
                        propertiesUpdatedByLocal.Add(property);
                    }
                }
            }

            /*
            string debugprops = "";
            foreach (SyncableProperties.Type p in propertiesUpdatedByLocal)
                debugprops += p.ToString() + ",";
            DebugLog.DebugFormat("[PRIM SYNC INFO] UpdatePropertiesByLocal ended for {0}. propertiesUpdatedByLocal.Count = {1}: {2}", part.UUID, propertiesUpdatedByLocal.Count, debugprops);
            */
            // DebugLog.WarnFormat("[SYNC INFO PRIM] UpdatePropertiesByLocal uuid={0}, propertiesUpdatedByLocal.Count={1}", uuid, propertiesUpdatedByLocal.Count);
            return propertiesUpdatedByLocal;
        }


        const float ROTATION_TOLERANCE = 0.01f;
        const float VELOCITY_TOLERANCE = 0.001f;
        const float POSITION_TOLERANCE = 0.05f;

        /// <summary>
        /// Compare the value (not "reference") of the given property. 
        /// Assumption: the caller has already checked if PhysActor exists
        /// if there are physics properties updated.
        /// If the value maintained here is different from that in SOP data,
        /// synchronize the two: 
        /// (1) if the value here has a timestamp newer than lastUpdateByLocalTS 
        /// (e.g. due to clock drifts among different sync nodes, a remote
        /// write might have a newer timestamp than the local write), 
        /// overwrite the SOP's property with the value here (effectively 
        /// disvalidate the local write operation that just happened). 
        /// (2) otherwise, copy SOP's data and update timestamp and syncID 
        /// as indicated by "lastUpdateByLocalTS" and "syncID".
        /// </summary>
        /// <param name="part"></param>
        /// <param name="property"></param>
        /// <param name="lastUpdateByLocalTS"></param>
        /// <param name="syncID"></param>
        /// <returns>Return true if the property's value maintained in this SyncInfoPrim is replaced by SOP's data.</returns>
        private bool CompareValue_UpdateByLocal(SceneObjectPart part, SyncableProperties.Type property, long lastUpdateByLocalTS, string syncID)
        {
            //DebugLog.WarnFormat("[SYNC INFO PRIM] CompareValue_UpdateByLocal: Updating property {0} on part {1}", property.ToString(), part.UUID);
            if (!CurrentlySyncedProperties.ContainsKey(property))
            {
                Object initValue = GetPropertyValue(part, property);
                SyncedProperty syncInfo = new SyncedProperty(property, initValue, lastUpdateByLocalTS, syncID);
                CurrentlySyncedProperties.Add(property, syncInfo);
                return true;
            }

            // First, check if the value maintained here is different from that in SOP's. 
            // If different, next check if the timestamp in SyncInfo is newer than lastUpdateByLocalTS; 
            // if so (although ideally should not happen, but due to things likc clock not so perfectly 
            // sync'ed, it might happen), overwrite SOP's value with what's maintained
            // in SyncInfo; otherwise, copy SOP's data to SyncInfo.

            switch (property)
            {
                case SyncableProperties.Type.GroupPosition:
                    return CompareAndUpdateSOPGroupPositionByLocal(part, lastUpdateByLocalTS, syncID);

                default:
                    SyncedProperty syncedProperty = CurrentlySyncedProperties[property];
                    Object partValue = GetPropertyValue(part, property);

                    // If both null, no update needed
                    if (syncedProperty.LastUpdateValue == null && partValue == null)
                        return false;

                    // If one is null and the other is not, or if they are not equal, the property was changed.
                    if ((partValue == null && syncedProperty.LastUpdateValue != null) || 
                        (partValue != null && syncedProperty.LastUpdateValue == null) ||
                        (!partValue.Equals(syncedProperty.LastUpdateValue)))
                    {
                        if (partValue != null)
                        {
                            switch (property)
                            {
                                case SyncableProperties.Type.Velocity:
                                case SyncableProperties.Type.PA_Velocity:
                                case SyncableProperties.Type.PA_TargetVelocity:
                                case SyncableProperties.Type.RotationalVelocity:
                                    {
                                        Vector3 partVal = (Vector3)partValue;
                                        Vector3 lastVal = (Vector3)syncedProperty.LastUpdateValue;
                                        // If velocity difference is small but not zero, don't update
                                        if (partVal.ApproxEquals(lastVal, VELOCITY_TOLERANCE) && !partVal.Equals(Vector3.Zero))
                                            return false;
                                        break;
                                    }
                                case SyncableProperties.Type.RotationOffset:
                                case SyncableProperties.Type.Orientation:
                                    {
                                        Quaternion partVal = (Quaternion)partValue;
                                        Quaternion lastVal = (Quaternion)syncedProperty.LastUpdateValue;
                                        if (partVal.ApproxEquals(lastVal, ROTATION_TOLERANCE))
                                            return false;
                                        break;
                                    }
                                case SyncableProperties.Type.AngularVelocity:
                                    {
                                        Vector3 partVal = (Vector3)partValue;
                                        Vector3 lastVal = (Vector3)syncedProperty.LastUpdateValue;
                                        if (partVal.ApproxEquals(lastVal, VELOCITY_TOLERANCE))
                                            return false;
                                        break;
                                    }
                                case SyncableProperties.Type.OffsetPosition:
                                    {
                                        Vector3 partVal = (Vector3)partValue;
                                        Vector3 lastVal = (Vector3)syncedProperty.LastUpdateValue;
                                        if (partVal.ApproxEquals(lastVal, POSITION_TOLERANCE))
                                            return false;
                                        break;
                                    }
                            }
                        }
                        if (property == SyncableProperties.Type.Shape)
                        {
                            //DebugLog.WarnFormat("[SYNC INFO PRIM]: SHAPES DIFFER {0} {1}", (string)partValue, (string)syncedProperty.LastUpdateValue);
                        }
                        // DebugLog.WarnFormat("[SYNC INFO PRIM] CompareValue_UpdateByLocal (property={0}): partValue != syncedProperty.LastUpdateValue", property.ToString());
                        if (lastUpdateByLocalTS >= syncedProperty.LastUpdateTimeStamp)
                        {
                            // DebugLog.WarnFormat("[SYNC INFO PRIM] CompareValue_UpdateByLocal (property={0}): TS >= lastTS (updating SyncInfo)", property.ToString());
                            CurrentlySyncedProperties[property].UpdateSyncInfoByLocal(lastUpdateByLocalTS, syncID, partValue);

                            // Updating either absolute position or position also requires checking for updates to group position
                            if (property == SyncableProperties.Type.AbsolutePosition || property == SyncableProperties.Type.Position)
                                CompareValue_UpdateByLocal(part, SyncableProperties.Type.GroupPosition, lastUpdateByLocalTS, syncID);

                            return true;
                        }
                        // DebugLog.WarnFormat("[SYNC INFO PRIM] CompareValue_UpdateByLocal (property={0}): TS < lastTS (updating SOP)", property.ToString());
                        SetPropertyValue(property);
                    }
                    break;
            }
            return false;
        }

        public override void PostUpdateBySync(HashSet<SyncableProperties.Type> updatedProperties)
        {
            SceneObjectPart sop = (SceneObjectPart)SceneThing;
            UUID uuid = sop.UUID;

            // All updated properties are in the set of TerseUpdateProperites
            bool allTerseUpdates = updatedProperties.IsSubsetOf(SyncableProperties.TerseUpdateProperties);

            // Any updated properties are in the set of GroupProperties
            bool hasGroupUpdates = updatedProperties.Overlaps(SyncableProperties.GroupProperties);

            if (!hasGroupUpdates || sop.ParentGroup == null)
            {
                if (allTerseUpdates)
                    sop.ScheduleTerseUpdate();
                else
                    sop.ScheduleFullUpdate();
            }
            else
            {
                if (allTerseUpdates)
                    sop.ParentGroup.ScheduleGroupForTerseUpdate();
                else
                    sop.ParentGroup.ScheduleGroupForFullUpdate();
            }

            /*
            string props = "";
            foreach (SyncableProperties.Type t in updatedProperties)
                props += t.ToString() + ",";
            DebugLog.WarnFormat("{0}: PostUpdateBySync: uuid={1}, allterse={2}, groupupates={3}, props={4}", LogHeader, uuid, allTerseUpdates, hasGroupUpdates, props);
            */
        }

        public override Object GetPropertyValue(SyncableProperties.Type property)
        {
            return GetPropertyValue((SceneObjectPart)SceneThing, property);
        }

        private Object GetPropertyValue(SceneObjectPart part, SyncableProperties.Type property)
        {
            switch (property)
            {
                ///////////////////////
                //SOP properties
                ///////////////////////
                case SyncableProperties.Type.AggregateScriptEvents:
                    return (int)part.AggregateScriptEvents;
                case SyncableProperties.Type.AllowedDrop:
                    return part.AllowedDrop;
                case SyncableProperties.Type.AngularVelocity:
                    return part.AngularVelocity;
                case SyncableProperties.Type.AttachedAvatar:
                    return part.ParentGroup.AttachedAvatar;
                case SyncableProperties.Type.AttachedPos:
                    return part.AttachedPos;
                case SyncableProperties.Type.AttachmentPoint:
                    return part.ParentGroup.AttachmentPoint;
                case SyncableProperties.Type.BaseMask:
                    return part.BaseMask;
                case SyncableProperties.Type.Category:
                    return part.Category;
                case SyncableProperties.Type.ClickAction:
                    return part.ClickAction;
                case SyncableProperties.Type.CollisionSound:
                    return part.CollisionSound;
                case SyncableProperties.Type.CollisionSoundVolume:
                    return part.CollisionSoundVolume;
                case SyncableProperties.Type.Color:
                    return PropertySerializer.SerializeColor(part.Color);
                case SyncableProperties.Type.CreationDate:
                    return part.CreationDate;
                case SyncableProperties.Type.CreatorData:
                    return part.CreatorData;
                case SyncableProperties.Type.CreatorID:
                    return part.CreatorID;
                case SyncableProperties.Type.Description:
                    return part.Description;
                case SyncableProperties.Type.EveryoneMask:
                    return part.EveryoneMask;
                case SyncableProperties.Type.Flags:
                    return (int)part.Flags;
                case SyncableProperties.Type.FolderID:
                    return part.FolderID;
                //Skip SyncableProperties.FullUpdate, which should be handled seperatedly
                case SyncableProperties.Type.GroupID:
                    return part.GroupID;
                case SyncableProperties.Type.GroupMask:
                    return part.GroupMask;
                case SyncableProperties.Type.GroupPosition:
                    return part.GroupPosition;
                case SyncableProperties.Type.InventorySerial:
                    return part.InventorySerial;
                case SyncableProperties.Type.IsAttachment:
                    return part.ParentGroup.IsAttachment;
                case SyncableProperties.Type.LastOwnerID:
                    return part.LastOwnerID;
                case SyncableProperties.Type.LinkNum:
                    return part.LinkNum;
                case SyncableProperties.Type.LocalId:
                    return part.LocalId;
                case SyncableProperties.Type.LocalFlags:
                    return (int)part.LocalFlags;
                case SyncableProperties.Type.Material:
                    return part.Material;
                case SyncableProperties.Type.MediaUrl:
                    return part.MediaUrl;
                case SyncableProperties.Type.Name:
                    return part.Name;
                case SyncableProperties.Type.NextOwnerMask:
                    return part.NextOwnerMask;
                case SyncableProperties.Type.ObjectSaleType:
                    return part.ObjectSaleType;
                case SyncableProperties.Type.OffsetPosition:
                    return part.OffsetPosition;
                case SyncableProperties.Type.OwnerID:
                    return part.OwnerID;
                case SyncableProperties.Type.OwnerMask:
                    return part.OwnerMask;
                case SyncableProperties.Type.OwnershipCost:
                    return part.OwnershipCost;
                case SyncableProperties.Type.ParticleSystem:
                    //byte[], return a cloned copy
                    return part.ParticleSystem;//.Clone()
                case SyncableProperties.Type.PassTouches:
                    return part.PassTouches;
                case SyncableProperties.Type.RotationOffset:
                    return part.RotationOffset;
                case SyncableProperties.Type.SalePrice:
                    return part.SalePrice;
                case SyncableProperties.Type.Scale:
                    return part.Scale;
                case SyncableProperties.Type.ScriptAccessPin:
                    return part.ScriptAccessPin;
                case SyncableProperties.Type.Shape:
                    return PropertySerializer.SerializeShape(part.Shape);
                case SyncableProperties.Type.SitName:
                    return part.SitName;
                case SyncableProperties.Type.SitTargetOrientation:
                    return part.SitTargetOrientation;
                case SyncableProperties.Type.SitTargetOrientationLL:
                    return part.SitTargetOrientationLL;
                case SyncableProperties.Type.SitTargetPosition:
                    return part.SitTargetPosition;
                case SyncableProperties.Type.SitTargetPositionLL:
                    return part.SitTargetPositionLL;
                case SyncableProperties.Type.SOP_Acceleration:
                    return part.Acceleration;
                case SyncableProperties.Type.Sound:
                    return part.Sound;
                case SyncableProperties.Type.TaskInventory:
                    return PropertySerializer.SerializeTaskInventory(part.TaskInventory, Scene);
                case SyncableProperties.Type.Text:
                    return part.Text;
                case SyncableProperties.Type.TextureAnimation:
                    return part.TextureAnimation;//.Clone();
                case SyncableProperties.Type.TouchName:
                    return part.TouchName;
                /*
                case SyncableProperties.Type.UpdateFlag:
                    return part.UpdateFlag;
                */
                case SyncableProperties.Type.Velocity:
                    return part.Velocity;
                case SyncableProperties.Type.VolumeDetectActive:
                    return part.VolumeDetectActive;

                ///////////////////////
                //PhysActor properties
                ///////////////////////
                case SyncableProperties.Type.Buoyancy:
                    if (part.PhysActor == null)
                        return null;
                    return part.PhysActor.Buoyancy;
                case SyncableProperties.Type.Flying:
                    if (part.PhysActor == null)
                        return null;
                    return part.PhysActor.Flying;
                case SyncableProperties.Type.Force:
                    if (part.PhysActor == null)
                        return null;
                    return part.PhysActor.Force;
                case SyncableProperties.Type.IsColliding:
                    if (part.PhysActor == null)
                        return null;
                    return part.PhysActor.IsColliding;
                case SyncableProperties.Type.CollidingGround:
                    if (part.PhysActor == null)
                        return null;
                    return part.PhysActor.CollidingGround;
                case SyncableProperties.Type.Kinematic:
                    if (part.PhysActor == null)
                        return null;
                    return part.PhysActor.Kinematic;
                case SyncableProperties.Type.Orientation:
                    if (part.PhysActor == null)
                        return null;
                    return part.PhysActor.Orientation;
                case SyncableProperties.Type.PA_Acceleration:
                    if (part.PhysActor == null)
                        return null;
                    return part.PhysActor.Acceleration;
                case SyncableProperties.Type.PA_Velocity:
                    if (part.PhysActor == null)
                        return null;
                    return part.PhysActor.Velocity;
                case SyncableProperties.Type.PA_TargetVelocity:
                    if (part.PhysActor == null)
                        return null;
                    return part.PhysActor.TargetVelocity;
                case SyncableProperties.Type.Position:
                    if (part.PhysActor == null)
                        return null;
                    return part.PhysActor.Position;
                case SyncableProperties.Type.RotationalVelocity:
                    if (part.PhysActor == null)
                        return null;
                    return part.PhysActor.RotationalVelocity;
                case SyncableProperties.Type.Size:
                    if (part.PhysActor == null)
                        return null;
                    return part.PhysActor.Size;
                case SyncableProperties.Type.Torque:
                    if (part.PhysActor == null)
                        return null;
                    return part.PhysActor.Torque;

                ///////////////////////
                //SOG properties
                ///////////////////////
                case SyncableProperties.Type.AbsolutePosition:
                    return part.ParentGroup.AbsolutePosition;
                case SyncableProperties.Type.IsSelected:
                    return part.ParentGroup.IsSelected;
            }

            //DebugLog.ErrorFormat("{0}: GetPropertyValue could not get property {1} from {2}", LogHeader, property.ToString(), part.UUID);
            return null;
        }

        public override void SetPropertyValue(SyncableProperties.Type property)
        {
            SceneObjectPart part = (SceneObjectPart)SceneThing;
            // DebugLog.WarnFormat("[SYNC INFO PRIM] SetPropertyValue(property={0})", property.ToString());
            SetPropertyValue(part, property);
            part.ParentGroup.HasGroupChanged = true;
        }

        /// <summary>
        /// Set the property's value based on the value maintained in SyncInfoManager.
        /// Assumption: caller will call ScheduleFullUpdate to enqueue updates properly to
        /// update viewers.
        /// This function should only be triggered when a prim update is received (i.e. 
        /// triggered by remote update instead of local update).
        /// </summary>
        /// <param name="part"></param>
        /// <param name="property"></param>
        private void SetPropertyValue(SceneObjectPart part, SyncableProperties.Type property)
        {
            //DebugLog.WarnFormat("[SYNC INFO PRIM] SetPropertyValue(part={0}, property={1})", part.UUID, property.ToString());

            // If this is a physical property but the part's PhysActor is null, then we can't set it.
            if (SyncableProperties.PhysActorProperties.Contains(property) && !CurrentlySyncedProperties.ContainsKey(property) && part.PhysActor == null)
            {
                // DebugLog.WarnFormat("{0}: SetPropertyValue: property {1} not in record.", LogHeader, property.ToString());
                //For phantom prims, they don't have physActor properties, so for those properties, simply return
                return;
            }

            if (!CurrentlySyncedProperties.ContainsKey(property))
            {
                DebugLog.ErrorFormat("{0}: SetPropertyValue: property {1} not in sync cache for uuid {2}. CSP.Count={3} ", LogHeader, property, UUID, CurrentlySyncedProperties.Count);
                return;
            }
            SyncedProperty pSyncInfo = CurrentlySyncedProperties[property];
            Object LastUpdateValue = pSyncInfo.LastUpdateValue;

            switch (property)
            {
                ///////////////////////
                //SOP properties
                ///////////////////////
                case SyncableProperties.Type.AggregateScriptEvents:
                    part.AggregateScriptEvents = (scriptEvents)(int)LastUpdateValue;
                    //DebugLog.DebugFormat("set {0} value to be {1}", property.ToString(), part.AggregateScriptEvents);
                    //DSL part.aggregateScriptEventSubscriptions();
                    break;

                case SyncableProperties.Type.AllowedDrop:
                    part.AllowedDrop = (bool)LastUpdateValue;
                    break;

                case SyncableProperties.Type.AngularVelocity:
                    part.AngularVelocity = (Vector3)LastUpdateValue;
                    break;

                case SyncableProperties.Type.AttachedAvatar:
                    //part.AttachedAvatar = (UUID)LastUpdateValue;
                    UUID attachedAvatar = (UUID)LastUpdateValue;
                    if (part.ParentGroup != null && !part.ParentGroup.AttachedAvatar.Equals(attachedAvatar))
                    {
                        part.ParentGroup.AttachedAvatar = attachedAvatar;
                        if (attachedAvatar != UUID.Zero)
                        {
                            ScenePresence avatar = Scene.GetScenePresence(attachedAvatar);
                            //It is possible that the avatar has not been fully 
                            //created locally when attachment objects are sync'ed.
                            //So we need to check if the avatar already exists.
                            //If not, handling of NewPresence will evetually trigger
                            //calling of SetParentLocalId.
                            if (avatar != null)
                            {
                                if (part.ParentGroup != null)
                                {
                                    part.ParentGroup.RootPart.SetParentLocalId(avatar.LocalId);
                                }
                                else
                                {
                                    //If this SOP is not a part of group yet, record the 
                                    //avatar's localID for now. If this SOP is rootpart of
                                    //the group, then the localID is the right setting; 
                                    //otherwise, this SOP will be linked to the SOG it belongs
                                    //to later, and that will rewrite the parent localID.
                                    part.SetParentLocalId(avatar.LocalId);
                                }
                            }
                        }
                        else
                        {
                            part.SetParentLocalId(0);
                        }

                    }
                    break;
                case SyncableProperties.Type.AttachedPos:
                    part.AttachedPos = (Vector3)LastUpdateValue;
                    break;
                case SyncableProperties.Type.AttachmentPoint:
                    //part.AttachmentPoint = (uint)LastUpdateValue;
                    //part.SetAttachmentPoint((uint)LastUpdateValue);
                    if (part.ParentGroup != null)
                        part.ParentGroup.AttachmentPoint = ((uint)LastUpdateValue);
                    break;
                case SyncableProperties.Type.BaseMask:
                    part.BaseMask = (uint)LastUpdateValue;
                    break;
                case SyncableProperties.Type.Category:
                    part.Category = (uint)LastUpdateValue;
                    break;
                case SyncableProperties.Type.ClickAction:
                    part.ClickAction = (byte)LastUpdateValue;
                    break;
                case SyncableProperties.Type.CollisionSound:
                    SetSOPCollisionSound(part, (UUID)LastUpdateValue);
                    break;
                case SyncableProperties.Type.CollisionSoundVolume:
                    part.CollisionSoundVolume = (float)LastUpdateValue;
                    break;
                case SyncableProperties.Type.Color:
                    part.Color = PropertySerializer.DeSerializeColor((string)LastUpdateValue);
                    break;
                case SyncableProperties.Type.CreationDate:
                    part.CreationDate = (int)LastUpdateValue;
                    break;
                case SyncableProperties.Type.CreatorData:
                    part.CreatorData = (string)LastUpdateValue;
                    break;
                case SyncableProperties.Type.CreatorID:
                    part.CreatorID = (UUID)LastUpdateValue;
                    break;
                case SyncableProperties.Type.Description:
                    part.Description = (string)LastUpdateValue;
                    break;
                case SyncableProperties.Type.EveryoneMask:
                    part.EveryoneMask = (uint)LastUpdateValue;
                    break;
                case SyncableProperties.Type.Flags:
                    SetSOPFlags(part, (PrimFlags)(int)LastUpdateValue);
                    break;
                case SyncableProperties.Type.FolderID:
                    part.FolderID = (UUID)LastUpdateValue;
                    break;
                //Skip SyncableProperties.FullUpdate, which should be handled seperatedly
                case SyncableProperties.Type.GroupID:
                    part.GroupID = (UUID)LastUpdateValue;
                    break;
                case SyncableProperties.Type.GroupMask:
                    part.GroupMask = (uint)LastUpdateValue;
                    break;
                case SyncableProperties.Type.GroupPosition:
                    part.GroupPosition = (Vector3)LastUpdateValue;
                    break;
                case SyncableProperties.Type.InventorySerial:
                    part.InventorySerial = (uint)LastUpdateValue;
                    break;
                case SyncableProperties.Type.IsAttachment:
                    if (part.ParentGroup != null)
                        part.ParentGroup.IsAttachment = (bool)LastUpdateValue;
                    break;
                case SyncableProperties.Type.LastOwnerID:
                    part.LastOwnerID = (UUID)LastUpdateValue;
                    break;
                case SyncableProperties.Type.LinkNum:
                    part.LinkNum = (int)LastUpdateValue;
                    break;
                case SyncableProperties.Type.LocalFlags:
                    part.LocalFlags = (PrimFlags)(int)LastUpdateValue;
                    break;
                case SyncableProperties.Type.LocalId:
                    part.LocalId = (uint)LastUpdateValue;
                    break;
                case SyncableProperties.Type.Material:
                    part.Material = (byte)LastUpdateValue;
                    break;
                case SyncableProperties.Type.MediaUrl:
                    part.MediaUrl = (string)LastUpdateValue;
                    break;
                case SyncableProperties.Type.Name:
                    part.Name = (string)LastUpdateValue;
                    break;
                case SyncableProperties.Type.NextOwnerMask:
                    part.NextOwnerMask = (uint)LastUpdateValue;
                    break;
                case SyncableProperties.Type.ObjectSaleType:
                    part.ObjectSaleType = (byte)LastUpdateValue;
                    break;
                case SyncableProperties.Type.OffsetPosition:
                    part.OffsetPosition = (Vector3)LastUpdateValue;
                    break;
                case SyncableProperties.Type.OwnerID:
                    part.OwnerID = (UUID)LastUpdateValue;
                    break;
                case SyncableProperties.Type.OwnerMask:
                    part.OwnerMask = (uint)LastUpdateValue;
                    break;
                case SyncableProperties.Type.OwnershipCost:
                    part.OwnershipCost = (int)LastUpdateValue;
                    break;
                case SyncableProperties.Type.ParticleSystem:
                    //byte[], return a cloned copy
                    //byte[] pValue = (byte[])LastUpdateValue;
                    //part.ParticleSystem = (byte[])pValue.Clone();
                    part.ParticleSystem = (byte[])LastUpdateValue;
                    break;
                case SyncableProperties.Type.PassTouches:
                    part.PassTouches = (bool)LastUpdateValue;
                    break;
                case SyncableProperties.Type.RotationOffset:
                    part.RotationOffset = (Quaternion)LastUpdateValue;
                    break;
                case SyncableProperties.Type.SalePrice:
                    part.SalePrice = (int)LastUpdateValue;
                    break;
                case SyncableProperties.Type.Scale:
                    part.Scale = (Vector3)LastUpdateValue;
                    break;
                case SyncableProperties.Type.ScriptAccessPin:
                    part.ScriptAccessPin = (int)LastUpdateValue;
                    break;
                case SyncableProperties.Type.Shape:
                    PrimitiveBaseShape shapeVal = PropertySerializer.DeSerializeShape((string)LastUpdateValue);
                    // Scale and State are actually synchronized as part properties so existing values
                    // should be preserved when updating a shape
                    if (part.Shape != null)
                    {
                        shapeVal.Scale = part.Shape.Scale;
                        shapeVal.State = part.Shape.State;
                    }
                    part.Shape = shapeVal;
                    break;
                case SyncableProperties.Type.SitName:
                    part.SitName = (string)LastUpdateValue;
                    break;
                case SyncableProperties.Type.SitTargetOrientation:
                    part.SitTargetOrientation = (Quaternion)LastUpdateValue;
                    break;
                case SyncableProperties.Type.SitTargetOrientationLL:
                    part.SitTargetOrientationLL = (Quaternion)LastUpdateValue;
                    break;
                case SyncableProperties.Type.SitTargetPosition:
                    part.SitTargetPosition = (Vector3)LastUpdateValue;
                    break;
                case SyncableProperties.Type.SitTargetPositionLL:
                    part.SitTargetPositionLL = (Vector3)LastUpdateValue;
                    break;
                case SyncableProperties.Type.SOP_Acceleration:
                    part.Acceleration = (Vector3)LastUpdateValue;
                    break;
                case SyncableProperties.Type.Sound:
                    part.Sound = (UUID)LastUpdateValue;
                    break;
                case SyncableProperties.Type.TaskInventory:
                    TaskInventoryDictionary taskVal = PropertySerializer.DeSerializeTaskInventory((string)LastUpdateValue);
                    if (taskVal != null)
                    {
                        part.TaskInventory = taskVal;
                        //Mark the inventory as has changed, for proper backup
                        part.Inventory.ForceInventoryPersistence();
                    }
                    break;
                case SyncableProperties.Type.Text:
                    part.Text = (string)LastUpdateValue;
                    break;
                case SyncableProperties.Type.TextureAnimation:
                    //byte[], return a cloned copy
                    //byte[] tValue = (byte[])LastUpdateValue;
                    //part.TextureAnimation = (byte[])tValue.Clone();
                    part.TextureAnimation = (byte[])LastUpdateValue;
                    break;
                case SyncableProperties.Type.TouchName:
                    part.TouchName = (string)LastUpdateValue;
                    break;
                /*
                case SyncableProperties.Type.UpdateFlag:
                    part.UpdateFlag = (UpdateRequired)LastUpdateValue;
                    break;
                */
                case SyncableProperties.Type.Velocity:
                    part.Velocity = (Vector3)LastUpdateValue;
                    break;
                case SyncableProperties.Type.VolumeDetectActive:
                    //part.ParentGroup.UpdatePrimFlagsBySync(part.LocalId, part., IsTemporary, IsPhantom, part.VolumeDetectActive);
                    bool isVD = (bool)LastUpdateValue;
                    //VD DEBUG
                    //DebugLog.DebugFormat("VolumeDetectActive updated on SOP {0}, to {1}", part.Name, isVD);
                    if (part.ParentGroup != null)
                    {
                        //VD DEBUG
                        //DebugLog.DebugFormat("calling ScriptSetVolumeDetectBySync");
                        //DSL part.ParentGroup.ScriptSetVolumeDetectBySync(isVD);
                    }
                    part.VolumeDetectActive = isVD;
                    //DSL part.aggregateScriptEventSubscriptions();
                    break;

                ///////////////////////
                //PhysActor properties
                ///////////////////////
                case SyncableProperties.Type.Buoyancy:
                    if (part.PhysActor != null)
                        part.PhysActor.Buoyancy = (float)LastUpdateValue;
                    break;
                case SyncableProperties.Type.Flying:
                    if (part.PhysActor != null)
                        part.PhysActor.Flying = (bool)LastUpdateValue;
                    break;
                case SyncableProperties.Type.Force:
                    if (part.PhysActor != null)
                        part.PhysActor.Force = (Vector3)LastUpdateValue;
                    break;
                case SyncableProperties.Type.IsColliding:
                    if (part.PhysActor != null)
                        part.PhysActor.IsColliding = (bool)LastUpdateValue;
                    break;
                case SyncableProperties.Type.CollidingGround:
                    if (part.PhysActor != null)
                        part.PhysActor.CollidingGround = (bool)LastUpdateValue;
                    break;
                case SyncableProperties.Type.Kinematic:
                    if (part.PhysActor != null)
                        part.PhysActor.Kinematic = (bool)LastUpdateValue;
                    break;
                case SyncableProperties.Type.Orientation:
                    if (part.PhysActor != null)
                        part.PhysActor.Orientation = (Quaternion)LastUpdateValue;
                    break;
                case SyncableProperties.Type.PA_Acceleration:
                    if (part.PhysActor != null)
                        part.PhysActor.Acceleration = (Vector3)LastUpdateValue;
                    break;
                case SyncableProperties.Type.PA_Velocity:
                    if (part.PhysActor != null)
                        part.PhysActor.Velocity = (Vector3)LastUpdateValue;
                    break;
                case SyncableProperties.Type.PA_TargetVelocity:
                    if (part.PhysActor != null)
                        part.PhysActor.TargetVelocity = (Vector3)LastUpdateValue;
                    break;
                case SyncableProperties.Type.Position:
                    if (part.PhysActor != null)
                        part.PhysActor.Position = (Vector3)LastUpdateValue;
                    break;
                case SyncableProperties.Type.RotationalVelocity:
                    if (part.PhysActor != null)
                        part.PhysActor.RotationalVelocity = (Vector3)LastUpdateValue;
                    break;
                case SyncableProperties.Type.Size:
                    if (part.PhysActor != null)
                        part.PhysActor.Size = (Vector3)LastUpdateValue;
                    break;
                case SyncableProperties.Type.Torque:
                    if (part.PhysActor != null)
                        part.PhysActor.Torque = (Vector3)LastUpdateValue;
                    break;

                ///////////////////////
                //SOG properties
                ///////////////////////
                case SyncableProperties.Type.AbsolutePosition:
                    SetSOPAbsolutePosition(part, pSyncInfo);
                    break;
                case SyncableProperties.Type.IsSelected:
                    if (part.ParentGroup != null)
                        part.ParentGroup.IsSelected = (bool)LastUpdateValue;
                    break;
            }

            //Calling ScheduleFullUpdate to trigger enqueuing updates for sync'ing (relay sync nodes need to do so)
            //part.ScheduleFullUpdate(new List<SyncableProperties.Type>() { property }); 
        }

        private void SetSOPAbsolutePosition(SceneObjectPart part, SyncedProperty pSyncInfo)
        {
            // DebugLog.WarnFormat("[SYNC INFO PRIM] SetSOPAbsolutePosition");
            if (part.ParentGroup != null)
            {
                part.ParentGroup.AbsolutePosition = (Vector3)pSyncInfo.LastUpdateValue;

                SyncedProperty gPosSyncInfo;

                if (part.ParentGroup.IsAttachment)
                    return;

                if (CurrentlySyncedProperties.ContainsKey(SyncableProperties.Type.GroupPosition))
                {
                    gPosSyncInfo = CurrentlySyncedProperties[SyncableProperties.Type.GroupPosition];
                    gPosSyncInfo.UpdateSyncInfoBySync(pSyncInfo.LastUpdateTimeStamp, pSyncInfo.LastUpdateSyncID, part.GroupPosition, pSyncInfo.LastSyncUpdateRecvTime);
                }
                else
                {
                    gPosSyncInfo = new SyncedProperty(SyncableProperties.Type.GroupPosition,
                        part.GroupPosition, pSyncInfo.LastUpdateTimeStamp, pSyncInfo.LastUpdateSyncID);
                    CurrentlySyncedProperties.Add(SyncableProperties.Type.GroupPosition, gPosSyncInfo);
                }

                if (part.PhysActor != null)
                {
                    SyncedProperty posSyncInfo;
                    if (CurrentlySyncedProperties.ContainsKey(SyncableProperties.Type.Position))
                    {
                        posSyncInfo = CurrentlySyncedProperties[SyncableProperties.Type.Position];
                        posSyncInfo.UpdateSyncInfoBySync(pSyncInfo.LastUpdateTimeStamp, pSyncInfo.LastUpdateSyncID, part.PhysActor.Position, pSyncInfo.LastSyncUpdateRecvTime);
                    }
                    else
                    {
                        posSyncInfo = new SyncedProperty(SyncableProperties.Type.Position,
                            part.PhysActor.Position, pSyncInfo.LastUpdateTimeStamp, pSyncInfo.LastUpdateSyncID);
                        CurrentlySyncedProperties.Add(SyncableProperties.Type.Position, posSyncInfo);
                    }
                }
                //the above operation may change GroupPosition and PhysActor.Postiion
                //as well. so update their values
            }
        }

        //Do not call "part.CollisionSound =" to go through its set function.
        //We don't want the side effect of calling aggregateScriptEvents.
        private void SetSOPCollisionSound(SceneObjectPart part, UUID cSound)
        {
            return;
            /*
            //DSL if (part.UpdateCollisionSound(cSound))
            {
                part.ParentGroup.Scene.EventManager.TriggerAggregateScriptEvents(part);
            }
             */
        }

        private void SetSOPFlags(SceneObjectPart part, PrimFlags flags)
        {
            //Do not set part.Flags yet, 
            //part.Flags = flags;
            // DebugLog.WarnFormat("[SYNC INFO PRIM] SetSOPFlags");

            bool UsePhysics = (flags & PrimFlags.Physics) != 0;
            bool IsTemporary = (flags & PrimFlags.TemporaryOnRez) != 0;
            //bool IsVolumeDetect = part.VolumeDetectActive;
            bool IsPhantom = (flags & PrimFlags.Phantom) != 0;

            if (part.ParentGroup != null)
            {
                //part.ParentGroup.UpdatePrimFlagsBySync(part.LocalId, UsePhysics, IsTemporary, IsPhantom, part.VolumeDetectActive);
                part.ParentGroup.UpdatePrimFlags(part.LocalId, UsePhysics, IsTemporary, IsPhantom, part.VolumeDetectActive);
                //part.UpdatePrimFlagsBySync(UsePhysics, IsTemporary, IsPhantom, part.VolumeDetectActive);
            }
            part.Flags = flags;
            //DSL part.aggregateScriptEventSubscriptions();
            part.ScheduleFullUpdate();
        }

        //In SOP's implementation, GroupPosition and SOP.PhysActor.Position are 
        //correlated. We need to make sure that they are both properly synced.
        private bool CompareAndUpdateSOPGroupPositionByLocal(SceneObjectPart part, long lastUpdateByLocalTS, string syncID)
        {
            if (part.ParentGroup.IsAttachment)
                return false;

            // DebugLog.WarnFormat("[SYNC INFO PRIM] CompareAndUpdateSOPGroupPositionByLocal");
            if (!part.GroupPosition.ApproxEquals((Vector3)CurrentlySyncedProperties[SyncableProperties.Type.GroupPosition].LastUpdateValue, (float)0.001))
            {
                if (lastUpdateByLocalTS >= CurrentlySyncedProperties[SyncableProperties.Type.GroupPosition].LastUpdateTimeStamp)
                {
                    //Update cached value with SOP.GroupPosition
                    CurrentlySyncedProperties[SyncableProperties.Type.GroupPosition].UpdateSyncInfoByLocal(lastUpdateByLocalTS, syncID, (Object)part.GroupPosition);

                    //Also may need to cached PhysActor.Position
                    if (part.PhysActor != null)
                    {
                        if (!CurrentlySyncedProperties.ContainsKey(SyncableProperties.Type.Position))
                        {
                            Object initValue = GetPropertyValue(part, SyncableProperties.Type.Position);
                            SyncedProperty syncInfo = new SyncedProperty(SyncableProperties.Type.Position, initValue, lastUpdateByLocalTS, syncID);
                            CurrentlySyncedProperties.Add(SyncableProperties.Type.Position, syncInfo);
                        }
                        else
                        {
                            if (!part.PhysActor.Position.Equals(CurrentlySyncedProperties[SyncableProperties.Type.Position].LastUpdateValue))
                            {
                                CurrentlySyncedProperties[SyncableProperties.Type.Position].UpdateSyncInfoByLocal(lastUpdateByLocalTS, syncID, (Object)part.PhysActor.Position);
                            }
                        }
                    }
                    return true;
                }
                else if (lastUpdateByLocalTS < CurrentlySyncedProperties[SyncableProperties.Type.GroupPosition].LastUpdateTimeStamp)
                {
                    //overwrite SOP's data, set function of GroupPosition updates PhysActor.Position as well
                    part.GroupPosition = (Vector3)CurrentlySyncedProperties[SyncableProperties.Type.GroupPosition].LastUpdateValue;

                    //PhysActor.Position is just updated by setting GroupPosition 
                    //above, so need to update the cached value of Position here.
                    if (part.PhysActor != null)
                    {
                        //Set the timestamp and syncID to be the same with GroupPosition
                        long lastUpdateTimestamp = CurrentlySyncedProperties[SyncableProperties.Type.GroupPosition].LastUpdateTimeStamp;
                        string lastUpdateSyncID = CurrentlySyncedProperties[SyncableProperties.Type.GroupPosition].LastUpdateSyncID;

                        if (!CurrentlySyncedProperties.ContainsKey(SyncableProperties.Type.Position))
                        {
                            Object initValue = GetPropertyValue(part, SyncableProperties.Type.Position);
                            SyncedProperty syncInfo = new SyncedProperty(SyncableProperties.Type.Position, initValue, lastUpdateTimestamp, lastUpdateSyncID);
                            CurrentlySyncedProperties.Add(SyncableProperties.Type.Position, syncInfo);
                        }
                        else
                        {
                            if (!part.PhysActor.Position.Equals(CurrentlySyncedProperties[SyncableProperties.Type.Position].LastUpdateValue))
                            {
                                //Set the timestamp and syncID to be the same with GroupPosition
                                //long lastUpdateTimestamp = CurrentlySyncedProperties[SyncableProperties.Type.GroupPosition].LastUpdateTimeStamp;
                                //string lastUpdateSyncID = CurrentlySyncedProperties[SyncableProperties.Type.GroupPosition].LastUpdateSyncID;
                                CurrentlySyncedProperties[SyncableProperties.Type.Position].UpdateSyncInfoByLocal(lastUpdateTimestamp,
                                    lastUpdateSyncID, (Object)part.PhysActor.Position);
                            }
                        }
                    }
                }
            }
            return false;
        }
        
        public override int Size
        {
            get 
            {
                int estimateBytes = 0;
                lock (m_syncLock)
                {
                    //estimateBytes += primSyncInfo.CurrentlySyncedProperties.
                    foreach (KeyValuePair<SyncableProperties.Type, SyncedProperty> valPair in CurrentlySyncedProperties)
                    {
                        SyncedProperty syncedProperty = valPair.Value;
                        estimateBytes += 8; //syncedProperty.LastSyncUpdateRecvTime bytes, long
                        estimateBytes += 4; //syncedProperty.LastUpdateSource, enum
                        estimateBytes += syncedProperty.LastUpdateSyncID.Length;

                        if (syncedProperty.LastUpdateValue != null)
                        {
                            /*
                            Type pType = syncedProperty.LastUpdateValue.GetType(); 
                            switch (pType){
                                case Vector3:
                                    estimateBytes += ((Vector3) syncedProperty.LastUpdateValue).GetBytes();
                                    break;
                                case String:
                                    estimateBytes += ((String) syncedProperty.LastUpdateValue).Length;
                                    break;
                                case Int32:
                                    estimateBytes += 4;
                                    break;
                                case Quaternion:
                                    estimateBytes += ((Quaternion) syncedProperty.LastUpdateValue).GetBytes();
                                    break;
                                case Quaternion:
                                    estimateBytes += ((Quaternion) syncedProperty.LastUpdateValue).GetBytes();
                                    break;
                                default: 
                                    break;
                            }
                             * */
                            switch (valPair.Key)
                            {


                                ////////////////////////////
                                //SOP properties, enum types
                                ////////////////////////////
                                case SyncableProperties.Type.AggregateScriptEvents:
                                case SyncableProperties.Type.Flags:
                                case SyncableProperties.Type.LocalFlags:
                                    estimateBytes += 4;
                                    break;
                                ////////////////////////////
                                //SOP properties, bool types
                                ////////////////////////////
                                case SyncableProperties.Type.AllowedDrop:
                                case SyncableProperties.Type.IsAttachment:
                                case SyncableProperties.Type.PassTouches:
                                case SyncableProperties.Type.VolumeDetectActive:
                                    estimateBytes += 1;
                                    break;

                                ////////////////////////////
                                //SOP properties, Vector3 types
                                ////////////////////////////
                                case SyncableProperties.Type.AngularVelocity:
                                case SyncableProperties.Type.AttachedPos:
                                case SyncableProperties.Type.GroupPosition:
                                case SyncableProperties.Type.OffsetPosition:
                                case SyncableProperties.Type.Scale:
                                case SyncableProperties.Type.SitTargetPosition:
                                case SyncableProperties.Type.SitTargetPositionLL:
                                case SyncableProperties.Type.SOP_Acceleration:
                                case SyncableProperties.Type.Velocity:
                                    estimateBytes += ((Vector3)syncedProperty.LastUpdateValue).GetBytes().Length;
                                    break;

                                ////////////////////////////
                                //SOP properties, UUID types
                                ////////////////////////////
                                case SyncableProperties.Type.AttachedAvatar:
                                case SyncableProperties.Type.CollisionSound:
                                case SyncableProperties.Type.CreatorID:
                                case SyncableProperties.Type.FolderID:
                                case SyncableProperties.Type.GroupID:
                                case SyncableProperties.Type.LastOwnerID:
                                case SyncableProperties.Type.OwnerID:
                                case SyncableProperties.Type.Sound:
                                    estimateBytes += ((UUID)syncedProperty.LastUpdateValue).GetBytes().Length;
                                    break;

                                //case SceneObjectPartProperties.AttachedPos:
                                ////////////////////////////
                                //SOP properties, uint types
                                ////////////////////////////
                                case SyncableProperties.Type.LocalId:
                                case SyncableProperties.Type.AttachmentPoint:
                                case SyncableProperties.Type.BaseMask:
                                case SyncableProperties.Type.Category:
                                case SyncableProperties.Type.EveryoneMask:
                                case SyncableProperties.Type.GroupMask:
                                case SyncableProperties.Type.InventorySerial:
                                case SyncableProperties.Type.NextOwnerMask:
                                case SyncableProperties.Type.OwnerMask:
                                    estimateBytes += 4;
                                    break;

                                //case SceneObjectPartProperties.BaseMask:
                                //case SceneObjectPartProperties.Category:

                                ////////////////////////////
                                //SOP properties, byte types
                                ////////////////////////////                    
                                case SyncableProperties.Type.ClickAction:
                                case SyncableProperties.Type.Material:
                                case SyncableProperties.Type.ObjectSaleType:
                                    //case SyncableProperties.Type.UpdateFlag:
                                    estimateBytes += 1;
                                    break;
                                //case SceneObjectPartProperties.CollisionSound:

                                ////////////////////////////
                                //SOP properties, float types
                                ////////////////////////////
                                case SyncableProperties.Type.CollisionSoundVolume:
                                    estimateBytes += 4;
                                    break;

                                ////////////////////////////
                                //SOP properties, int types
                                ////////////////////////////
                                case SyncableProperties.Type.CreationDate:
                                case SyncableProperties.Type.LinkNum:
                                case SyncableProperties.Type.OwnershipCost:
                                case SyncableProperties.Type.SalePrice:
                                case SyncableProperties.Type.ScriptAccessPin:
                                    estimateBytes += 4;
                                    break;

                                ////////////////////////////
                                //SOP properties, string types
                                ////////////////////////////
                                case SyncableProperties.Type.Color:
                                case SyncableProperties.Type.CreatorData:
                                case SyncableProperties.Type.Description:
                                case SyncableProperties.Type.MediaUrl:
                                case SyncableProperties.Type.Name:
                                case SyncableProperties.Type.Shape:
                                case SyncableProperties.Type.SitName:
                                case SyncableProperties.Type.TaskInventory:
                                case SyncableProperties.Type.Text:
                                case SyncableProperties.Type.TouchName:
                                    estimateBytes += ((string)syncedProperty.LastUpdateValue).Length;
                                    break;
                                ////////////////////////////
                                //SOP properties, byte[]  types
                                ////////////////////////////
                                case SyncableProperties.Type.ParticleSystem:
                                case SyncableProperties.Type.TextureAnimation:
                                    estimateBytes += ((byte[])syncedProperty.LastUpdateValue).Length;
                                    break;

                                ////////////////////////////
                                //SOP properties, Quaternion  types
                                ////////////////////////////
                                case SyncableProperties.Type.RotationOffset:
                                case SyncableProperties.Type.SitTargetOrientation:
                                case SyncableProperties.Type.SitTargetOrientationLL:
                                    //propertyData["Value"] = OSD.FromQuaternion((Quaternion)LastUpdateValue);
                                    estimateBytes += ((Quaternion)syncedProperty.LastUpdateValue).GetBytes().Length;
                                    break;

                                ////////////////////////////////////
                                //PhysActor properties, float type
                                ////////////////////////////////////
                                case SyncableProperties.Type.Buoyancy:
                                    //propertyData["Value"] = OSD.FromReal((float)LastUpdateValue);
                                    estimateBytes += 4;
                                    break;

                                ////////////////////////////////////
                                //PhysActor properties, bool type
                                ////////////////////////////////////
                                case SyncableProperties.Type.Flying:
                                case SyncableProperties.Type.IsColliding:
                                case SyncableProperties.Type.CollidingGround:
                                case SyncableProperties.Type.Kinematic:
                                    estimateBytes += 1;
                                    break;

                                ////////////////////////////////////
                                //PhysActor properties, Vector3 type
                                ////////////////////////////////////
                                case SyncableProperties.Type.Force:
                                case SyncableProperties.Type.PA_Acceleration:
                                case SyncableProperties.Type.PA_Velocity:
                                case SyncableProperties.Type.PA_TargetVelocity:
                                case SyncableProperties.Type.Position:
                                case SyncableProperties.Type.RotationalVelocity:
                                case SyncableProperties.Type.Size:
                                case SyncableProperties.Type.Torque:
                                    estimateBytes += ((Vector3)syncedProperty.LastUpdateValue).GetBytes().Length;
                                    break;

                                ////////////////////////////////////
                                //PhysActor properties, Quaternion type
                                ////////////////////////////////////
                                case SyncableProperties.Type.Orientation:
                                    //propertyData["Value"] = OSD.FromQuaternion((Quaternion)LastUpdateValue);
                                    estimateBytes += ((Quaternion)syncedProperty.LastUpdateValue).GetBytes().Length;
                                    break;

                                ///////////////////////
                                //SOG properties
                                ///////////////////////
                                case SyncableProperties.Type.AbsolutePosition:
                                    estimateBytes += ((Vector3)syncedProperty.LastUpdateValue).GetBytes().Length;
                                    break;
                                case SyncableProperties.Type.IsSelected:
                                    //propertyData["Value"] = OSD.FromBoolean((bool)LastUpdateValue);
                                    estimateBytes += 1;
                                    break;

                                default:
                                    break;

                            }
                        }
                    }
                }
                return estimateBytes;
            }
        }
    }
}
