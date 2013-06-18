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
using System.IO;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using log4net;
using System.Collections.Generic;

namespace DSG.RegionSync
{
    #region ActorType Enum
    public enum ActorType
    {
        Null,
        ClientManager,
        ScriptEngine,
        PhysicsEngine
    }
    #endregion

    #region ActorStatus Enum
    public enum ActorStatus
    {
        Null,
        Idle,
        Sync
    }
    #endregion 

    /// <summary>
    /// Types of symmetric sync messages among actors. 
    /// </summary>
    public class SymmetricSyncMessage
    {
        #region MsgType Enum
        public enum MsgType
        {
            Null,

            UpdatedProperties, // per property sync for SP and SOP

            // Actor -> SIM(Scene)
            GetTerrain,
            GetObjects,
            GetPresences,
            GetRegionInfo,
            
            // SIM <-> CM
            Terrain,
            RegionInfo,
            
            NewObject,       // objects
            RemovedObject,   // objects
            LinkObject,
            DelinkObject,
            
            UpdatedBucketProperties, //object properties in one bucket
            
            
            NewPresence,
            RemovedPresence,
            RegionName,
            RegionStatus,
            ActorID,
            ActorType,
            //events
            NewScript,
            UpdateScript,
            ScriptReset,
            ChatFromClient,
            ChatFromWorld,
            ChatBroadcast,
            ObjectGrab,
            ObjectGrabbing,
            ObjectDeGrab,
            Attach,
            PhysicsCollision,
            ScriptCollidingStart,
            ScriptColliding,
            ScriptCollidingEnd,
            ScriptLandCollidingStart,
            ScriptLandColliding,
            ScriptLandCollidingEnd,
            //control command
            SyncStateReport,
            TimeStamp,

            //quarks related
            SyncQuarksSubscription, //sent by a sync node who just connects to another
            SyncQuarksSubscriptionAck, //reply to the sender of SyncQuarksNotification
            QuarkCrossingFullUpdate,    // property changing and sog crossing quark boundry
            QuarkCrossingSPFullUpdate   // scene presence crossing quarks
        }
        #endregion

        #region Member Data
        private MsgType m_type;
        private byte[] m_data;
        static ILog m_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        #endregion

        #region Constructors

        static HashSet<SymmetricSyncMessage.MsgType> logged = new HashSet<SymmetricSyncMessage.MsgType>();
        private void WriteOutMessage(SymmetricSyncMessage.MsgType t, string s)
        {
            lock (logged)
            {
                //if (!logged.Contains(t))
                {
                    m_log.WarnFormat("[MSGDUMP]: TYPE=\"{0}\", CONTENTS=\"{1}\"", t, s);
                    //logged.Add(t);
                }
            }
        }

        public SymmetricSyncMessage(MsgType type, string msg)
        {
            m_type = type;
            m_data = System.Text.Encoding.ASCII.GetBytes(msg);
            //WriteOutMessage(type, msg);
        }

        public SymmetricSyncMessage(MsgType type, OSDMap mapOfData)
        {
            m_type = type;
            string s = OSDParser.SerializeJsonString(mapOfData, true);
            m_data = System.Text.Encoding.ASCII.GetBytes(s);
            //WriteOutMessage(type, s);
        }

        public SymmetricSyncMessage(MsgType type)
        {
            m_type = type;
            m_data = new byte[0];
        }

        public SymmetricSyncMessage(Stream stream)
        {
            //ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
            //try
            {
                m_type = (MsgType)Utils.BytesToInt(GetBytesFromStream(stream, 4));
                int length = Utils.BytesToInt(GetBytesFromStream(stream, 4));
                m_data = GetBytesFromStream(stream, length);
                //log.WarnFormat("RegionSyncMessage Constructed {0} ({1} bytes)", m_type.ToString(), length);
            }
        }

        private byte[] GetBytesFromStream(Stream stream, int count)
        {
            // Loop to receive the message length
            byte[] ret = new byte[count];
            int i = 0;
            while (i < count)
            {
                i += stream.Read(ret, i, count - i);
            }
            return ret;
        }

        #endregion

        #region Accessors
        public MsgType Type
        {
            get { return m_type; }
        }

        public int Length
        {
            get { return m_data.Length; }
        }

        public byte[] Data
        {
            get { return m_data; }
        }
        // the reason this message was created. Used in debug logging.
        public string Reason { get; set; }
        #endregion

        #region Conversions
        public byte[] ToBytes()
        {
            byte[] buf = new byte[m_data.Length + 8];
            Utils.IntToBytes((int)m_type, buf, 0);
            Utils.IntToBytes(m_data.Length, buf, 4);
            Array.Copy(m_data, 0, buf, 8, m_data.Length);
            return buf;
        }

        public override string ToString()
        {
            return String.Format("{0} ({1} bytes)", m_type.ToString(), m_data.Length.ToString());
        }
        #endregion


        public static void HandleSuccess(string header, SymmetricSyncMessage msg, string message)
        {
            m_log.WarnFormat("{0} Handled {1}: {2}", header, msg.ToString(), message);
        }

        public static void HandleTrivial(string header, SymmetricSyncMessage msg, string message)
        {
            m_log.WarnFormat("{0} Issue handling {1}: {2}", header, msg.ToString(), message);
        }

        public static void HandleWarning(string header, SymmetricSyncMessage msg, string message)
        {
            m_log.WarnFormat("{0} Warning handling {1}: {2}", header, msg.ToString(), message);
        }

        public static void HandleError(string header, SymmetricSyncMessage msg, string message)
        {
            m_log.WarnFormat("{0} Error handling {1}: {2}", header, msg.ToString(), message);
        }

        public static bool HandleDebug(string header, SymmetricSyncMessage msg, string message)
        {
            m_log.WarnFormat("{0} DBG ({1}): {2}", header, msg.ToString(), message);
            return true;
        }
    }
}
