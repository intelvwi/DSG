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
using log4net;

namespace DSG.RegionSync
{
    public static class SyncableProperties
    {
        public enum Type
        {
            LocalId,
            
            //Following properties copied from SceneObjectSerializer(),
            AllowedDrop,
            CreatorID,
            CreatorData,
            FolderID,
            InventorySerial,
            TaskInventory,
            Name,
            Material,
            PassTouches,
            //RegionHandle,
            ScriptAccessPin,
            GroupPosition,
            OffsetPosition,
            RotationOffset,
            Velocity,
            AngularVelocity,
            //"Acceleration",
            SOP_Acceleration,  //SOP and PA read/write their own local copies of acceleration, so we distinguish the copies
            Description,
            Color,
            Text,
            SitName,
            TouchName,
            LinkNum,
            ClickAction,
            Shape,
            Scale,
            //UpdateFlag,
            SitTargetOrientation,
            SitTargetPosition,
            SitTargetPositionLL,
            SitTargetOrientationLL,
            //ParentID,
            CreationDate,
            Category,
            SalePrice,
            ObjectSaleType,
            OwnershipCost,
            GroupID,
            OwnerID,
            LastOwnerID,
            BaseMask,
            OwnerMask,
            GroupMask,
            EveryoneMask,
            NextOwnerMask,
            Flags,
            CollisionSound,
            CollisionSoundVolume,
            MediaUrl,
            TextureAnimation,
            ParticleSystem,
            //Property names below copied from PhysicsActor, they are necessary in
            //synchronization, but not covered by xml serialization
            //Physics properties "Velocity" is covered above
            Position,
            Size,
            Force,
            RotationalVelocity,
            PA_Acceleration,
            PA_Velocity,
            PA_TargetVelocity,
            Torque,
            Orientation,
            IsPhysical,
            Flying,
            Buoyancy,
            Kinematic,
            CollidingGround,
            IsColliding,

            //Properties need to be synced, but not in xml serializations
            AggregateScriptEvents,
            AttachedAvatar,
            AttachedPos,
            AttachmentPoint,
            IsAttachment,
            LocalFlags,
            //TODO!!!! To be handled in serialization/deserizaltion for synchronization
            Sound, //This indicates any Sound related property has changed: Sound, SoundGain, SoundFlags,SoundRadius,
            //Addition properties to be added here
            VolumeDetectActive,


            //Group properties
            AbsolutePosition,
            IsSelected,

            // Avatar Properties
            AvatarAppearance,
            AgentCircuitData,
            AgentControlFlags,
            ParentId,
            AllowMovement,
            PresenceType,
            Rotation
        }

        public readonly static HashSet<SyncableProperties.Type> TerseUpdateProperties = SyncableProperties.GetTerseUpdateProperties();
        public readonly static HashSet<SyncableProperties.Type> FullUpdateProperties = SyncableProperties.GetFullUpdateProperties();
        public readonly static HashSet<SyncableProperties.Type> PhysActorProperties = SyncableProperties.GetPhysActorProperties();
        public readonly static HashSet<SyncableProperties.Type> NonPhysActorProperties = SyncableProperties.GetNonPhysActorProperties();
        public readonly static HashSet<SyncableProperties.Type> GroupProperties = SyncableProperties.GetGroupProperties();
        public readonly static HashSet<SyncableProperties.Type> AvatarProperties = SyncableProperties.GetAvatarProperties();
        public readonly static HashSet<SyncableProperties.Type> AttachmentNonSyncProperties = SyncableProperties.GetAttachmentNonSyncProperties();

        /// <summary>
        /// Return the list of all prim (SOP) properties, in enum type excluding None, FullUpdate.
        /// </summary>
        /// <returns></returns>
        private static HashSet<SyncableProperties.Type> GetFullUpdateProperties()
        {
            HashSet<SyncableProperties.Type> allProperties = new HashSet<SyncableProperties.Type>();
            foreach (SyncableProperties.Type property in Enum.GetValues(typeof(SyncableProperties.Type)))
            {
                switch (property)
                {
                    // Don't include avatar properties
                    case Type.AvatarAppearance:
                    case Type.AgentCircuitData:
                    case Type.AgentControlFlags:
                    case Type.AllowMovement:
                    case Type.ParentId:
                    case Type.PresenceType:
                    case Type.Rotation:
                    case Type.PA_Velocity:
                    case Type.PA_TargetVelocity:
                        break;
                    default:
                        allProperties.Add(property);
                        break;
                }
            }
            return allProperties;
        }

        private static HashSet<SyncableProperties.Type> GetPhysActorProperties()
        {
            HashSet<SyncableProperties.Type> allProperties = new HashSet<SyncableProperties.Type>();
            foreach (SyncableProperties.Type property in Enum.GetValues(typeof(SyncableProperties.Type)))
            {
                switch (property)
                {
                    //PhysActor properties
                    case SyncableProperties.Type.Buoyancy:
                    case SyncableProperties.Type.Flying:
                    case SyncableProperties.Type.Force:
                    case SyncableProperties.Type.IsColliding:
                    case SyncableProperties.Type.CollidingGround:
                    case SyncableProperties.Type.IsPhysical:
                    case SyncableProperties.Type.Kinematic:
                    case SyncableProperties.Type.Orientation:
                    case SyncableProperties.Type.PA_Acceleration:
                    case SyncableProperties.Type.Position:
                    case SyncableProperties.Type.RotationalVelocity:
                    case SyncableProperties.Type.Size:
                    case SyncableProperties.Type.Torque:
                        allProperties.Add(property);
                        break;
                    default:
                        break;
                }
            }
            return allProperties;
        }

        private static HashSet<SyncableProperties.Type> GetNonPhysActorProperties()
        {
            HashSet<SyncableProperties.Type> allProperties = GetFullUpdateProperties();
            HashSet<SyncableProperties.Type> physActorProperties = GetPhysActorProperties();

            foreach (SyncableProperties.Type pProperty in physActorProperties)
            {
                allProperties.Remove(pProperty);
            }
            return allProperties;
        }

        private static HashSet<SyncableProperties.Type> GetGroupProperties()
        {
            HashSet<SyncableProperties.Type> groupProperties = new HashSet<SyncableProperties.Type>();
            groupProperties.Add(SyncableProperties.Type.IsSelected);
            groupProperties.Add(SyncableProperties.Type.AbsolutePosition);
            groupProperties.Add(SyncableProperties.Type.GroupPosition);
            return groupProperties;
        }

        private static HashSet<SyncableProperties.Type> GetTerseUpdateProperties()
        {
            HashSet<SyncableProperties.Type> allProperties = new HashSet<SyncableProperties.Type>();
            allProperties.Add(SyncableProperties.Type.Velocity);
            allProperties.Add(SyncableProperties.Type.RotationOffset);
            allProperties.Add(SyncableProperties.Type.AngularVelocity);
            allProperties.Add(SyncableProperties.Type.OffsetPosition);
            allProperties.Add(SyncableProperties.Type.Scale);
            allProperties.Add(SyncableProperties.Type.GroupPosition);
            allProperties.Add(SyncableProperties.Type.Orientation);
            allProperties.Add(SyncableProperties.Type.RotationalVelocity);
            allProperties.Add(SyncableProperties.Type.Position);
            allProperties.Add(SyncableProperties.Type.AbsolutePosition);
            allProperties.Add(SyncableProperties.Type.PA_Acceleration);

            return allProperties;
        }

        private static HashSet<SyncableProperties.Type> GetAvatarProperties()
        {
            HashSet<SyncableProperties.Type> allProperties = new HashSet<SyncableProperties.Type>();
            allProperties.Add(SyncableProperties.Type.AbsolutePosition); 
            allProperties.Add(SyncableProperties.Type.AgentCircuitData);
            allProperties.Add(SyncableProperties.Type.AgentControlFlags);
            allProperties.Add(SyncableProperties.Type.ParentId);
            allProperties.Add(SyncableProperties.Type.AllowMovement);
            allProperties.Add(SyncableProperties.Type.AvatarAppearance);
            allProperties.Add(SyncableProperties.Type.Rotation);
            allProperties.Add(SyncableProperties.Type.PA_Velocity);
            allProperties.Add(SyncableProperties.Type.PA_TargetVelocity);
            allProperties.Add(SyncableProperties.Type.Flying);
            allProperties.Add(SyncableProperties.Type.PresenceType);
            allProperties.Add(SyncableProperties.Type.IsColliding);

            return allProperties;
        }

        /// <summary>
        /// Return the list of all prim (SOP) properties, in enum type excluding None, FullUpdate.
        /// </summary>
        /// <returns></returns>
        private static HashSet<SyncableProperties.Type> GetAttachmentNonSyncProperties()
        {
            HashSet<SyncableProperties.Type> allProperties = new HashSet<SyncableProperties.Type>();
            allProperties.Add(SyncableProperties.Type.AbsolutePosition);
            allProperties.Add(SyncableProperties.Type.GroupPosition);

            return allProperties;
        }
    }
}
