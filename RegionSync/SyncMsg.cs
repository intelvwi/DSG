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
using System.IO;
using System.Text;

using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

using OpenMetaverse;
using OpenMetaverse.StructuredData;

using log4net;

namespace DSG.RegionSync
{
public abstract class SyncMsg
{
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
    }

    /// <summary>
    /// SyncMsg processing progression on reception:
    ///       The stream reader creates a SyncMsg instance by calling:
    ///              msg = SyncMsg.SyncMsgFactory(stream);
    ///       This creates the msg of the correct type with the binary constructor. For instance,
    ///              msg = new SyncMsgTimeStamp(type, length, data);
    ///       On a possibly different thread, the binary input is converted into load data via:
    ///              msg.ConvertIn(regionContext, connectorContext);
    ///       The message is acted on by calling:
    ///              msg.HandleIn(regionContext, connectorContext);
    /// The processing progression on sending is:
    ///       Someone creates a message of the desired type. For instance:
    ///              msg = new SyncMsgTimeStamp(DateTime.UtcNow.Ticks);
    ///       The message can be operated on as its methods allow (like adding updates, for instance).
    ///       Before sending, the local variables are converted into binary for sending via:
    ///              msg.ConvertOut(regionContext)
    ///       This prepares the message for sending and can be done once before sending on multiple SyncConnectors.
    ///       The binary data to send is fetched via:
    ///              byte[] outBytes = msg.GetWireBytes();
    ///       This last byte buffer contains the type and length header required for parsing at the other end.
    /// 
    /// A message has a 'direction' state: In or Out. This controls the processing of the buffers. The meaning:
    ///     SyncMsg.Direction.In: message received off the stream as a binary block.
    ///             The message has a "ConnectorContext".
    ///     SyncMsg.Direction.Out: the message created as a message to send.
    ///             The message has a "RegionContext"
    /// The operation of the base class methods for each mode are:
    /// 
    /// new(type, buff, buffLen)
    ///             set direction to In.
    /// new(typeSpecificParameters)
    ///             set direction to Out
    /// ConvertIn
    ///             In: convert m_data to local varaibles
    ///             Out: no operation
    /// HandleIn
    ///             In: operate on the local variables
    ///             Out: operate on the local variables
    /// ConvertOut
    ///             In: presumed sending binary buffer back out. No conversion done.
    ///                 To cause local variables to be converted, set direction to "Out".
    ///             Out: if not already converted, convert local variables to binary format.
    ///                 For SyncMsgOSDMap, if DataMap does not exist, create it from local variables and
    ///                 serialize to m_data.
    /// GetWireBytes
    ///             In: if binary buffer does not already exist create from m_data and DataLength to create wire format bytes.
    ///             Out: if binary buffer does not already exist create from m_data and DataLength to create wire format bytes.
    ///       
    /// </summary>
    protected static ILog m_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
    protected readonly string LogHeader = "[SYNCMSG]";
    protected readonly string ZeroUUID = "00000000-0000-0000-0000-000000000000";

    public enum Direction
    {
        In,
        Out
    }

    public Direction Dir { get; set; }

    // They type of this message
    public MsgType MType { get; protected set; }

    // The binary data this type of message is built from or converted into
    public int DataLength { get; protected set; }
    protected byte[] m_data;

    // The binary, wire encoding of the object. Includes the type and length header.
    private byte[] m_rawOutBytes;

    // The connector this message was received on.
    public SyncConnector ConnectorContext { get; set; }

    // The region the message was created for.
    public RegionSyncModule RegionContext { get; set; }

    // Given an incoming stream of bytes, create a SyncMsg from the next data on that stream.
    // The input stream should contain:
    //      4 bytes: the msgType code
    //      4 bytes: number of bytes following for the message data
    //      N bytes: the data for this type of messsage
    public static SyncMsg SyncMsgFactory(Stream pStream, SyncConnector pConnectorContext)
    {
        SyncMsg ret = null;
        MsgType mType = (MsgType)Utils.BytesToInt(GetBytesFromStream(pStream, 4));
        int length = Utils.BytesToInt(GetBytesFromStream(pStream, 4));
        byte[] data = GetBytesFromStream(pStream, length);

        switch (mType)
        {
            case MsgType.UpdatedProperties: ret = new SyncMsgUpdatedProperties(mType, length, data);    break;
            case MsgType.GetTerrain:        ret = new SyncMsgGetTerrain(mType, length, data);           break;
            case MsgType.GetObjects:        ret = new SyncMsgGetObjects(mType, length, data);           break;
            case MsgType.GetPresences:      ret = new SyncMsgGetPresences(mType, length, data);         break;
            case MsgType.GetRegionInfo:     ret = new SyncMsgGetRegionInfo(mType, length, data);        break;
            case MsgType.Terrain:           ret = new SyncMsgTerrain(mType, length, data);              break;
            case MsgType.RegionInfo:        ret = new SyncMsgRegionInfo(mType, length, data);           break;
            case MsgType.NewObject:         ret = new SyncMsgNewObject(mType, length, data);            break;
            case MsgType.RemovedObject:     ret = new SyncMsgRemovedObject(mType, length, data);        break;
            case MsgType.LinkObject:        ret = new SyncMsgLinkObject(mType, length, data);           break;
            case MsgType.DelinkObject:      ret = new SyncMsgDelinkObject(mType, length, data);         break;
            case MsgType.NewPresence:       ret = new SyncMsgNewPresence(mType, length, data);          break;
            case MsgType.RemovedPresence:   ret = new SyncMsgRemovedPresence(mType, length, data);      break;
            case MsgType.RegionName:        ret = new SyncMsgRegionName(mType, length, data);           break;
            case MsgType.ActorID:           ret = new SyncMsgActorID(mType, length, data);              break;
            case MsgType.RegionStatus:      ret = new SyncMsgRegionStatus(mType, length, data);         break;

            case MsgType.NewScript:         ret = new SyncMsgNewScript(mType, length, data);            break;
            case MsgType.UpdateScript:      ret = new SyncMsgUpdateScript(mType, length, data);         break;
            case MsgType.ScriptReset:       ret = new SyncMsgScriptReset(mType, length, data);          break;
            case MsgType.ChatFromClient:    ret = new SyncMsgChatFromClient(mType, length, data);       break;
            case MsgType.ChatFromWorld:     ret = new SyncMsgChatFromWorld(mType, length, data);        break;
            case MsgType.ChatBroadcast:     ret = new SyncMsgChatBroadcast(mType, length, data);        break;
            case MsgType.ObjectGrab:        ret = new SyncMsgObjectGrab(mType, length, data);           break;
            case MsgType.ObjectGrabbing:    ret = new SyncMsgObjectGrabbing(mType, length, data);       break;
            case MsgType.ObjectDeGrab:      ret = new SyncMsgObjectDeGrab(mType, length, data);         break;
            case MsgType.Attach:            ret = new SyncMsgAttach(mType, length, data);               break;

            // case MsgType.PhysicsCollision:      ret = new SyncMsgPhysicsCollision(mType, length, data);     break;
            case MsgType.ScriptCollidingStart:  ret = new SyncMsgScriptCollidingStart(mType, length, data); break;
            case MsgType.ScriptColliding:       ret = new SyncMsgScriptColliding(mType, length, data);      break;
            case MsgType.ScriptCollidingEnd:    ret = new SyncMsgScriptCollidingEnd(mType, length, data);   break;
            case MsgType.ScriptLandCollidingStart:ret = new SyncMsgScriptLandCollidingStart(mType, length, data);   break;
            case MsgType.ScriptLandColliding:   ret = new SyncMsgScriptLandColliding(mType, length, data);  break;
            case MsgType.ScriptLandCollidingEnd:ret = new SyncMsgScriptLandCollidingEnd(mType, length, data);   break;

            case MsgType.TimeStamp:         ret = new SyncMsgTimeStamp(mType, length, data);            break;
            // case MsgType.UpdatedBucketProperties: ret = new SyncMsgUpdatedBucketProperties(mType, length, data); break;

            default:
                break;
        }

        if (ret != null)
        {
            ret.ConnectorContext = pConnectorContext;
        }

        return ret;
    }

    public SyncMsg()
    {
        DataLength = 0;
        m_rawOutBytes = null;
        RegionContext = null;
        ConnectorContext = null;
    }
    public SyncMsg(MsgType pType, int pLength, byte[] pData)
    {
        Dir = Direction.In;
        MType = pType;
        DataLength = pLength;
        m_data = pData;
        m_rawOutBytes = null;
        RegionContext = null;
        ConnectorContext = null;
    }
    private static byte[] GetBytesFromStream(Stream stream, int count)
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

    // Get the bytes to put on the stream.
    // This creates the raw message format with the header type and length.
    // If there was any internal data manipulated, someone must call ProcessOut() before this is called.
    public byte[] GetWireBytes()
    {
        if (m_rawOutBytes == null)
        {
            m_rawOutBytes = new byte[m_data.Length + 8];
            Utils.IntToBytes((int)MType, m_rawOutBytes, 0);
            Utils.IntToBytes(m_data.Length, m_rawOutBytes, 4);
            Array.Copy(m_data, 0, m_rawOutBytes, 8, m_data.Length);
        }
        return m_rawOutBytes;
    }

    // Called after message received to convert binary stream data into internal representation.
    // A separate call so parsing can happen on a different thread from the input reader.
    // Return 'true' if successful handling.
    public virtual bool ConvertIn(RegionSyncModule regionContext)
    {
        RegionContext = regionContext;
        m_rawOutBytes = null;
        return true;
    }
    // Handle the received message. ConvertIn() has been called before this.
    public virtual bool HandleIn(RegionSyncModule regionContext)
    {
        RegionContext = regionContext;
        if (ConnectorContext != null)
        {
            LogReception(regionContext, ConnectorContext);
        }
        return true;
    }
    // Called before message is sent to convert the internal representation into binary stream data.
    // A separate call so parsing can happen on a different thread from the output writer.
    // Return 'true' if successful handling.
    public virtual bool ConvertOut(RegionSyncModule regionContext)
    {
        RegionContext = regionContext;
        m_rawOutBytes = null;
        return true;
    }

    // Overwritten by each msg class so log messages will be uniquely tagged.
    public virtual string DetailLogTagRcv { get { return "RcvSyncMsg"; } }
    public virtual string DetailLogTagSnd { get { return "SndSyncMsg"; } }

    // Called in HandleIn() to do any logging about the reception of the message.
    // Invoked in HandleIn() because the message has been decoded and useful stuff is available to print.
    public virtual void LogReception(RegionSyncModule regionContext, SyncConnector connectorContext)
    {
        if (regionContext != null && connectorContext != null)
            regionContext.DetailedUpdateWrite(DetailLogTagRcv, ZeroUUID, 0, ZeroUUID, connectorContext.otherSideActorID, DataLength);
    }
    // Called before the message is written to the wire.
    // Not done inside the message processing since a message can be sent repeatedly to multiple receptors.
    public virtual void LogTransmission(SyncConnector connectorContext)
    {
        if (RegionContext != null && connectorContext != null)
            RegionContext.DetailedUpdateWrite(DetailLogTagSnd, ZeroUUID, 0, ZeroUUID, connectorContext.otherSideActorID, DataLength);
    }
}

// ====================================================================================================
// A base class for sync messages whose underlying structure is just an OSDMap
public abstract class SyncMsgOSDMapData : SyncMsg
{
    protected OSDMap DataMap { get; set; }

    public SyncMsgOSDMapData()
        : base()
    {
        DataMap = null;
    }
    public SyncMsgOSDMapData(MsgType pType, int pLength, byte[] pData)
        : base(pType, pLength, pData)
    {
        DataMap = null;
    }
    // Convert the received block of binary bytes into an OSDMap (DataMap)
    // Return 'true' if successfully converted.
    public override bool ConvertIn(RegionSyncModule regionContext)
    {
        base.ConvertIn(regionContext);
        switch (Dir)
        {
            case Direction.In:
                // A received message so convert the buffer data to an OSDMap for use by children classes
                DataMap = DeserializeMessage();
                break;
            case Direction.Out:
                // A message being built for output.
                // It is actually an error that this method is called as there should be no binary data.
                DataMap = null;
                break;
        }
        return (DataMap != null);
    }
    // Convert the OSDMap of data into a binary array of bytes.
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        switch (Dir)
        {
            case Direction.In:
                // A message received being sent out again.
                // Current architecture is to just output the existing binary buffer so
                //     nothing is converted.
                break;
            case Direction.Out:
                // Message being created for output. The children of this base class
                //    should have created a DataMap from the local variables.
                if (DataMap != null)
                {
                    string s = OSDParser.SerializeJsonString(DataMap, true);
                    m_data = System.Text.Encoding.ASCII.GetBytes(s);
                    DataLength = m_data.Length;
                }
                else
                {
                    DataLength = 0;
                }
                break;
        }
        return base.ConvertOut(regionContext);
    }

    // Turn the binary bytes into and OSDMap
    private static HashSet<string> exceptions = new HashSet<string>();
    private OSDMap DeserializeMessage()
    {
        OSDMap data = null;
        try
        {
            data = OSDParser.DeserializeJson(Encoding.ASCII.GetString(m_data, 0, DataLength)) as OSDMap;
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
                    m_log.Error(LogHeader + " " + Encoding.ASCII.GetString(m_data, 0, DataLength));
                    // after all of that, print out the actual error
                    m_log.ErrorFormat("{0}: {1}", LogHeader, e);
                }
            }
            data = null;
        }
        return data;
    }

    #region Encode/DecodeSceneObject and Encode/DecodeScenePresence
    /// <summary>
    /// Encode a SOG. Values of each part's properties are copied from SyncInfo, instead of from SOP's data. 
    /// If the SyncInfo is not maintained by SyncInfoManager yet, add it first.
    /// </summary>
    /// <param name="sog"></param>
    /// <returns></returns>
    protected OSDMap EncodeSceneObject(SceneObjectGroup sog, RegionSyncModule regionContext)
    {
        //This should not happen, but we deal with it by inserting a newly created PrimSynInfo
        if (!regionContext.InfoManager.SyncInfoExists(sog.RootPart.UUID))
        {
            m_log.ErrorFormat("{0}: EncodeSceneObject -- SOP {1},{2} not in SyncInfoManager's record yet. Adding.", LogHeader, sog.RootPart.Name, sog.RootPart.UUID);
            regionContext.InfoManager.InsertSyncInfo(sog.RootPart.UUID, DateTime.UtcNow.Ticks, regionContext.SyncID);
        }

        OSDMap data = new OSDMap();
        data["uuid"] = OSD.FromUUID(sog.UUID);
        data["absPosition"] = OSDMap.FromVector3(sog.AbsolutePosition);
        data["RootPart"] = regionContext.InfoManager.EncodeProperties(sog.RootPart.UUID, sog.RootPart.PhysActor == null ? SyncableProperties.NonPhysActorProperties : SyncableProperties.FullUpdateProperties);

        OSDArray otherPartsArray = new OSDArray();
        foreach (SceneObjectPart part in sog.Parts)
        {
            if (!part.UUID.Equals(sog.RootPart.UUID))
            {
                if (!regionContext.InfoManager.SyncInfoExists(part.UUID))
                {
                    m_log.ErrorFormat("{0}: EncodeSceneObject -- SOP {1},{2} not in SyncInfoManager's record yet", 
                                LogHeader, part.Name, part.UUID);
                    //This should not happen, but we deal with it by inserting a newly created PrimSynInfo
                    regionContext.InfoManager.InsertSyncInfo(part.UUID, DateTime.UtcNow.Ticks, regionContext.SyncID);
                }
                OSDMap partData = regionContext.InfoManager.EncodeProperties(part.UUID, part.PhysActor == null ? SyncableProperties.NonPhysActorProperties : SyncableProperties.FullUpdateProperties);
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
    /// Encode a SP. Values of each part's properties are copied from SyncInfo, instead of from SP's data. 
    /// If the SyncInfo is not maintained by SyncInfoManager yet, add it first.
    /// </summary>
    /// <param name="sog"></param>
    /// <returns></returns>
    protected OSDMap EncodeScenePresence(ScenePresence sp, RegionSyncModule regionContext)
    {
        //This should not happen, but we deal with it by inserting it now
        if (!regionContext.InfoManager.SyncInfoExists(sp.UUID))
        {
            m_log.ErrorFormat("{0}: ERROR: EncodeScenePresence -- SP {1},{2} not in SyncInfoManager's record yet. Adding.", LogHeader, sp.Name, sp.UUID);
            regionContext.InfoManager.InsertSyncInfo(sp.UUID, DateTime.UtcNow.Ticks, regionContext.SyncID);
        }

        OSDMap data = new OSDMap();
        data["uuid"] = OSD.FromUUID(sp.UUID);
        data["absPosition"] = OSDMap.FromVector3(sp.AbsolutePosition);
        data["ScenePresence"] = regionContext.InfoManager.EncodeProperties(sp.UUID, SyncableProperties.AvatarProperties);

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
    protected bool DecodeSceneObject(OSDMap data, out SceneObjectGroup sog, out Dictionary<UUID, SyncInfoBase> syncInfos, Scene scene)
    {
        sog = new SceneObjectGroup();
        syncInfos = new Dictionary<UUID, SyncInfoBase>();
        bool ret = true;

        try{
            UUID uuid = ((OSDMap)data["RootPart"])["uuid"].AsUUID();

            OSDMap propertyData = (OSDMap)((OSDMap)data["RootPart"])["propertyData"];
            //m_log.WarnFormat("{0} DecodeSceneObject for RootPart uuid: {1}", LogHeader, uuid);

            //Decode and copy to the list of PrimSyncInfo
            SyncInfoPrim sip = new SyncInfoPrim(uuid, propertyData, scene);
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
                    sip = new SyncInfoPrim(uuid, propertyData, scene);
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

    // Decodes scene presence data into sync info
    protected void DecodeScenePresence(OSDMap data, out SyncInfoBase syncInfo, Scene scene)
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
            syncInfo = new SyncInfoPresence(presenceData["uuid"], (OSDMap)presenceData["propertyData"], scene);
        }
        catch (Exception e)
        {
            m_log.ErrorFormat("{0} DecodeScenePresence caught exception: {1}", LogHeader, e);
            return;
        }
    }
    #endregion // Encode/DecodeSceneObject and Encode/DecodeScenePresence
}

// ====================================================================================================
public class SyncMsgUpdatedProperties : SyncMsgOSDMapData
{
    public HashSet<SyncableProperties.Type> SyncableProperties { get; set; }
    public UUID Uuid { get; set; }

    HashSet<SyncedProperty> SyncedProperties;

    public SyncMsgUpdatedProperties(UUID pUuid, HashSet<SyncableProperties.Type> pSyncableProperties)
        : base()
    {
        Uuid = pUuid;
        SyncableProperties = pSyncableProperties;
    }
    public SyncMsgUpdatedProperties(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool ConvertIn(RegionSyncModule regionContext)
    {
        bool ret = false;
        if (base.ConvertIn(regionContext))
        {
            ret = true;
            // Decode synced properties from the message
            SyncedProperties = SyncedProperty.DecodeProperties(DataMap);
            if (SyncedProperties == null)
            {
                m_log.ErrorFormat("{0} HandleUpdatedProperties could not get syncedProperties", LogHeader);
                ret = false;
            }
            Uuid = DataMap["uuid"].AsUUID();
            if (Uuid == null)
            {
                m_log.ErrorFormat("{0} HandleUpdatedProperties could not get UUID!", LogHeader);
                ret = false;
            }
        }
        return ret;
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        if (base.HandleIn(regionContext))
        {
            if (SyncableProperties.Count > 0)
            {
                // Update local sync info and scene object/presence
                regionContext.RememberLocallyGeneratedEvent(MType);
                HashSet<SyncableProperties.Type> propertiesUpdated = regionContext.InfoManager.UpdateSyncInfoBySync(Uuid, SyncedProperties);
                regionContext.ForgetLocallyGeneratedEvent();

                regionContext.DetailedUpdateLogging(Uuid, propertiesUpdated, SyncedProperties, "RecUpdateN", ConnectorContext.otherSideActorID, DataLength);

                // Relay the update properties
                if (regionContext.IsSyncRelay)
                    regionContext.EnqueueUpdatedProperty(Uuid, propertiesUpdated);
            }
        }
        return true;
    }
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        if (Dir == Direction.Out && DataMap == null)
        {
            DataMap = regionContext.InfoManager.EncodeProperties(Uuid, SyncableProperties);
        }
        return base.ConvertOut(regionContext);
    }
    public override void LogReception(RegionSyncModule regionContext, SyncConnector connectorContext)
    {
        // This override is here so the reception will not be default logged and the actual logging can occur
        //     after the properties have been processed.
    }
    public override void LogTransmission(SyncConnector connectorContext)
    {
        if (RegionContext != null)
            RegionContext.DetailedUpdateLogging(Uuid, SyncableProperties, null, "SendUpdate", connectorContext.otherSideActorID, DataLength);
    }
}
// ====================================================================================================
// Send to have other side send us their region info.
// If received, send our region info.
public class SyncMsgGetRegionInfo : SyncMsgOSDMapData
{
    public override string DetailLogTagRcv { get { return "RcvGetRegn"; } }
    public override string DetailLogTagSnd { get { return "SndGetRegn"; } }

    public SyncMsgGetRegionInfo()
        : base()
    {
    }
    public SyncMsgGetRegionInfo(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool ConvertIn(RegionSyncModule regionContext)
    {
        return base.ConvertIn(regionContext);
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        if (base.HandleIn(regionContext))
        {
            SyncMsgRegionInfo msg = new SyncMsgRegionInfo(regionContext.Scene.RegionInfo);
            ConnectorContext.ImmediateOutgoingMsg(msg);
        }
        return true;
    }
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        return base.ConvertOut(regionContext);
    }
}
// ====================================================================================================
// Sent to tell the other end our region info.
// When received, it is the other side's region info.
public class SyncMsgRegionInfo : SyncMsgOSDMapData
{
    public override string DetailLogTagRcv { get { return "RcvRgnInfo"; } }
    public override string DetailLogTagSnd { get { return "SndRgnInfo"; } }

    public RegionInfo RegInfo { get; set; }

    public SyncMsgRegionInfo(RegionInfo pRegionInfo)
        : base()
    {
        RegInfo = pRegionInfo;
    }
    public SyncMsgRegionInfo(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool ConvertIn(RegionSyncModule regionContext)
    {
        return base.ConvertIn(regionContext);
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        if (base.HandleIn(regionContext))
        {
            RegInfo = regionContext.Scene.RegionInfo;
            RegInfo.RegionSettings.TerrainTexture1 = DataMap["tex1"].AsUUID();
            RegInfo.RegionSettings.TerrainTexture2 = DataMap["tex2"].AsUUID();
            RegInfo.RegionSettings.TerrainTexture3 = DataMap["tex3"].AsUUID();
            RegInfo.RegionSettings.TerrainTexture4 = DataMap["tex4"].AsUUID();
            RegInfo.RegionSettings.WaterHeight = DataMap["waterheight"].AsReal();
            IEstateModule estate = regionContext.Scene.RequestModuleInterface<IEstateModule>();
            if (estate != null)
                estate.sendRegionHandshakeToAll();
        }
        return true;
    }
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        if (Dir == Direction.Out && DataMap == null)
        {
            OSDMap data = new OSDMap(5);
            data["tex1"] = OSD.FromUUID(RegInfo.RegionSettings.TerrainTexture1);
            data["tex2"] = OSD.FromUUID(RegInfo.RegionSettings.TerrainTexture2);
            data["tex3"] = OSD.FromUUID(RegInfo.RegionSettings.TerrainTexture3);
            data["tex4"] = OSD.FromUUID(RegInfo.RegionSettings.TerrainTexture4);
            data["waterheight"] = OSD.FromReal(RegInfo.RegionSettings.WaterHeight);
            DataMap = data;
        }
        return base.ConvertOut(regionContext);
    }
}
// ====================================================================================================
public class SyncMsgTimeStamp : SyncMsgOSDMapData
{
    public override string DetailLogTagRcv { get { return "RcvTimStmp"; } }
    public override string DetailLogTagSnd { get { return "SndTimStmp"; } }

    public long TickTime { get; set; }

    public SyncMsgTimeStamp(long pTickTime)
        : base()
    {
        TickTime = pTickTime;
    }
    public SyncMsgTimeStamp(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool ConvertIn(RegionSyncModule regionContext)
    {
        return base.ConvertIn(regionContext);
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        // Do something interesting with the time code from the other side
        return base.HandleIn(regionContext);
    }
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        if (Dir == Direction.Out && DataMap == null)
        {
            OSDMap data = new OSDMap(1);
            data["timeStamp"] = OSD.FromLong(TickTime);
            DataMap = data;
        }
        return base.ConvertOut(regionContext);
    }
}
// ====================================================================================================
// Sending asks the other end to send us information about the terrain.
// When received, send back information about the terrain.
public class SyncMsgGetTerrain : SyncMsgOSDMapData
{
    public override string DetailLogTagRcv { get { return "RcvGetTerr"; } }
    public override string DetailLogTagSnd { get { return "SndGetTerr"; } }

    public SyncMsgGetTerrain()
        : base()
    {
    }
    public SyncMsgGetTerrain(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool ConvertIn(RegionSyncModule regionContext)
    {
        return base.ConvertIn(regionContext);
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        if (base.HandleIn(regionContext))
        {
            SyncMsgTerrain msg = new SyncMsgTerrain(regionContext.TerrainSyncInfo);
            msg.ConvertOut(regionContext);
            msg.ConnectorContext.ImmediateOutgoingMsg(msg);
        }
        return true;
    }
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        return base.ConvertOut(regionContext);
    }
    public override void LogReception(RegionSyncModule regionContext, SyncConnector connectorContext)
    {
        regionContext.DetailedUpdateWrite(DetailLogTagRcv, ZeroUUID, regionContext.TerrainSyncInfo.LastUpdateTimeStamp, ZeroUUID, ConnectorContext.otherSideActorID, 0);
    }
    public override void LogTransmission(SyncConnector connectorContext)
    {
        if (RegionContext != null)
            RegionContext.DetailedUpdateWrite(DetailLogTagSnd, ZeroUUID, 0, ZeroUUID, ConnectorContext.otherSideActorID, DataLength);
    }
}
// ====================================================================================================
public class SyncMsgTerrain : SyncMsgOSDMapData
{
    public override string DetailLogTagRcv { get { return "RcvTerrain"; } }
    public override string DetailLogTagSnd { get { return "SndTerrain"; } }

    public string TerrainData { get; set; }
    public long LastUpdateTimeStamp { get; set; }
    public string LastUpdateActorID { get; set; }

    public SyncMsgTerrain(TerrainSyncInfo pTerrainInfo)
        : base()
    {
    }
    public SyncMsgTerrain(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool ConvertIn(RegionSyncModule regionContext)
    {
        bool ret = false;
        if (base.ConvertIn(regionContext))
        {
            TerrainData = DataMap["terrain"].AsString();
            LastUpdateTimeStamp = DataMap["timeStamp"].AsLong();
            LastUpdateActorID = DataMap["actorID"].AsString();
            ret = true;
        }
        return ret;
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        if (base.HandleIn(regionContext))
        {
            //update the terrain if the incoming terrain data has a more recent timestamp
            regionContext.TerrainSyncInfo.UpdateTerrianBySync(LastUpdateTimeStamp, LastUpdateActorID, TerrainData);
        }
        return true;
    }
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        if (Dir == Direction.Out && DataMap == null)
        {
            OSDMap data = new OSDMap(3);
            data["terrain"] = OSD.FromString((string)regionContext.TerrainSyncInfo.LastUpdateValue);
            data["actorID"] = OSD.FromString(regionContext.TerrainSyncInfo.LastUpdateActorID);
            data["timeStamp"] = OSD.FromLong(regionContext.TerrainSyncInfo.LastUpdateTimeStamp);
            DataMap = data;
        }
        return base.ConvertOut(regionContext);
    }
}
// ====================================================================================================
public class SyncMsgGetObjects : SyncMsgOSDMapData
{
    public override string DetailLogTagRcv { get { return "RcvGetObjj"; } }
    public override string DetailLogTagSnd { get { return "SndGetObjj"; } }

    public SyncMsgGetObjects()
        : base()
    {
    }
    public SyncMsgGetObjects(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool ConvertIn(RegionSyncModule regionContext)
    {
        return base.ConvertIn(regionContext);
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        if (base.HandleIn(regionContext))
        {
            regionContext.Scene.ForEachSOG(delegate(SceneObjectGroup sog)
            {
                SyncMsgNewObject msg = new SyncMsgNewObject(sog);
                msg.ConvertOut(regionContext);
                ConnectorContext.ImmediateOutgoingMsg(msg);
            });
        }
        return true;
    }
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        return base.ConvertOut(regionContext);
    }
}
// ====================================================================================================
public class SyncMsgGetPresences : SyncMsgOSDMapData
{
    public override string DetailLogTagRcv { get { return "RcvGetPres"; } }
    public override string DetailLogTagSnd { get { return "SndGetPres"; } }

    public SyncMsgGetPresences()
        : base()
    {
    }
    public SyncMsgGetPresences(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool ConvertIn(RegionSyncModule regionContext)
    {
        return base.ConvertIn(regionContext);
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        if (base.HandleIn(regionContext))
        {
            EntityBase[] entities = regionContext.Scene.GetEntities();
            foreach (EntityBase e in entities)
            {
                ScenePresence sp = e as ScenePresence;
                if (sp != null)
                {
                    // This will sync the appearance that's currently in the agent circuit data.
                    // If the avatar has updated their appearance since they connected, the original data will still be in ACD.
                    // The ACD normally only gets updated when an avatar is moving between regions.
                    SyncMsgNewPresence msg = new SyncMsgNewPresence(sp);
                    msg.ConvertOut(regionContext);
                    m_log.DebugFormat("{0}: Send NewPresence message for {1} ({2})", LogHeader, sp.Name, sp.UUID);
                    ConnectorContext.ImmediateOutgoingMsg(msg);
                }
            }
        }
        return true;
    }
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        return base.ConvertOut(regionContext);
    }
}
// ====================================================================================================
public class SyncMsgNewObject : SyncMsgOSDMapData
{
    public override string DetailLogTagRcv { get { return "RcvNewObjj"; } }
    public override string DetailLogTagSnd { get { return "SndNewObjj"; } }

    public SceneObjectGroup SOG;
    public Dictionary<UUID, SyncInfoBase> SyncInfos;

    public SyncMsgNewObject(SceneObjectGroup pSog)
        : base()
    {
        SOG = pSog;
    }
    public SyncMsgNewObject(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool ConvertIn(RegionSyncModule regionContext)
    {
        bool ret = false;
        if (base.ConvertIn(regionContext))
        {
            if (!DecodeSceneObject(DataMap, out SOG, out SyncInfos, regionContext.Scene))
            {
                m_log.WarnFormat("{0}: Failed to decode scene object in HandleSyncNewObject", LogHeader);
                return false;
            }

            if (SOG.RootPart.Shape == null)
            {
                m_log.WarnFormat("{0}: group.RootPart.Shape is null", LogHeader);
                return false;
            }
            ret = true;
        }
        return ret;
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        if (base.HandleIn(regionContext))
        {
            // If this is a relay node, forward the message
            if (regionContext.IsSyncRelay)
                regionContext.SendSpecialUpdateToRelevantSyncConnectors(ConnectorContext.otherSideActorID, this);

            //Add the list of PrimSyncInfo to SyncInfoManager
            foreach (SyncInfoBase syncInfo in SyncInfos.Values)
                regionContext.InfoManager.InsertSyncInfo(syncInfo.UUID, syncInfo);

            // Add the decoded object to Scene
            // This will invoke OnObjectAddedToScene but the syncinfo has already been created so that's a NOP
            regionContext.Scene.AddNewSceneObject(SOG, true);

            // If it's an attachment, connect this to the presence
            if (SOG.IsAttachmentCheckFull())
            {
                //m_log.WarnFormat("{0}: HandleSyncNewObject: Adding attachement to presence", LogHeader);
                ScenePresence sp = regionContext.Scene.GetScenePresence(SOG.AttachedAvatar);
                if (sp != null)
                {
                    sp.AddAttachment(SOG);
                    SOG.RootPart.SetParentLocalId(sp.LocalId);

                    // In case it is later dropped, don't let it get cleaned up
                    SOG.RootPart.RemFlag(PrimFlags.TemporaryOnRez);

                    SOG.HasGroupChanged = true;
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
            if (SOG.RootPart.PhysActor != null)
            {
                foreach (SyncInfoBase syncInfo in SyncInfos.Values)
                {
                    // m_log.DebugFormat("{0}: HandleSyncNewObject: setting physical properties", LogHeader);
                    syncInfo.SetPropertyValues(SyncableProperties.PhysActorProperties);
                }
            }

            SOG.CreateScriptInstances(0, false, regionContext.Scene.DefaultScriptEngine, 0);
            SOG.ResumeScripts();

            // Trigger aggregateScriptEventSubscriptions since it may access PhysActor to link collision events
            foreach (SceneObjectPart part in SOG.Parts)
                part.aggregateScriptEvents();

            SOG.ScheduleGroupForFullUpdate();
        }
        return true;
    }
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        if (Dir == Direction.Out && DataMap == null)
        {
            DataMap = EncodeSceneObject(SOG, regionContext);
        }
        return base.ConvertOut(regionContext);
    }
    public override void LogTransmission(SyncConnector connectorContext)
    {
        if (RegionContext != null)
            RegionContext.DetailedUpdateWrite(DetailLogTagSnd, SOG == null ? UUID.Zero : SOG.UUID, 0, ZeroUUID, connectorContext.otherSideActorID, DataLength);
    }

}
// ====================================================================================================
public class SyncMsgRemovedObject : SyncMsgOSDMapData
{
    public override string DetailLogTagRcv { get { return "RcvRemObjj"; } }
    public override string DetailLogTagSnd { get { return "SndRemObjj"; } }

    public UUID Uuid { get; set; }
    public bool SoftDelete { get; set; }
    public string ActorID { get; set; }

    public SyncMsgRemovedObject(UUID pUuid, string pActorID, bool pSoftDelete)
        : base()
    {
        Uuid = pUuid;
        SoftDelete = pSoftDelete;
        ActorID = pActorID;
    }
    public SyncMsgRemovedObject(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool ConvertIn(RegionSyncModule regionContext)
    {
        bool ret = false;
        if (base.ConvertIn(regionContext))
        {

            Uuid = DataMap["uuid"].AsUUID();
            SoftDelete = DataMap["softDelete"].AsBoolean();
            ActorID = DataMap["actorID"].AsString();

            if (regionContext.InfoManager.SyncInfoExists(Uuid))
                ret = true;
        }
        return ret;
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        if (base.HandleIn(regionContext))
        {
            // If this is a relay node, forward the message
            if (regionContext.IsSyncRelay)
                regionContext.SendSpecialUpdateToRelevantSyncConnectors(ConnectorContext.otherSideActorID, this);

            SceneObjectGroup sog = regionContext.Scene.GetGroupByPrim(Uuid);

            if (sog != null)
            {
                if (!SoftDelete)
                {
                    //m_log.DebugFormat("{0}: hard delete object {1}", LogHeader, sog.UUID);
                    foreach (SceneObjectPart part in sog.Parts)
                    {
                        regionContext.InfoManager.RemoveSyncInfo(part.UUID);
                    }
                    regionContext.Scene.DeleteSceneObject(sog, false);
                }
                else
                {
                    //m_log.DebugFormat("{0}: soft delete object {1}", LogHeader, sog.UUID);
                    regionContext.Scene.UnlinkSceneObject(sog, true);
                }
            }
        }
        return true;
    }
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        if (Dir == Direction.Out && DataMap == null)
        {
            OSDMap data = new OSDMap(2);
            data["uuid"] = OSD.FromUUID(Uuid);
            data["softDelete"] = OSD.FromBoolean(SoftDelete);
            data["actorID"] = OSD.FromString(ActorID);
            DataMap = data;
        }
        return base.ConvertOut(regionContext);
    }
    public override void LogTransmission(SyncConnector connectorContext)
    {
        if (RegionContext != null)
            RegionContext.DetailedUpdateWrite(DetailLogTagSnd, Uuid, 0, ZeroUUID, connectorContext.otherSideActorID, DataLength);
    }
}
// ====================================================================================================
public class SyncMsgLinkObject : SyncMsgOSDMapData
{
    public override string DetailLogTagRcv { get { return "RcvLinkObj"; } }
    public override string DetailLogTagSnd { get { return "SndLinkObj"; } }

    public SceneObjectGroup LinkedGroup;
    public UUID RootUUID;
    public List<UUID> ChildrenIDs;
    public string ActorID;

    public Dictionary<UUID, SyncInfoBase> GroupSyncInfos;
    public OSDMap EncodedSOG;
    public int PartCount;

    public SyncMsgLinkObject(SceneObjectGroup pLinkedGroup, UUID pRootUUID, List<UUID> pChildrenUUIDs, string pActorID)
        : base()
    {
        LinkedGroup = pLinkedGroup;
        RootUUID = pRootUUID;
        ChildrenIDs = pChildrenUUIDs;
        ActorID = pActorID;
    }
    public SyncMsgLinkObject(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool ConvertIn(RegionSyncModule regionContext)
    {
        bool ret = false;
        if (base.ConvertIn(regionContext))
        {
            EncodedSOG = (OSDMap)DataMap["linkedGroup"];
            if (!DecodeSceneObject(EncodedSOG, out LinkedGroup, out GroupSyncInfos, regionContext.Scene))
            {
                m_log.WarnFormat("{0}: Failed to decode scene object in HandleSyncLinkObject", LogHeader);
                return false; ;
            }

            if (LinkedGroup == null)
            {
                m_log.ErrorFormat("{0}: HandleSyncLinkObject, no valid Linked-Group has been deserialized", LogHeader);
                return false;
            }

            RootUUID = DataMap["rootID"].AsUUID();
            PartCount = DataMap["partCount"].AsInteger();
            ChildrenIDs = new List<UUID>();

            for (int i = 0; i < PartCount; i++)
            {
                string partTempID = "part" + i;
                ChildrenIDs.Add(DataMap[partTempID].AsUUID());
            }
            ret = true;
        }
        return ret;
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        if (base.HandleIn(regionContext))
        {
            // if this is a relay node, forward the message
            if (regionContext.IsSyncRelay)
            {
                //SendSceneEventToRelevantSyncConnectors(senderActorID, msg, linkedGroup);
                regionContext.SendSpecialUpdateToRelevantSyncConnectors(ConnectorContext.otherSideActorID, this);
            }

            //m_log.DebugFormat("{0}: received LinkObject from {1}", LogHeader, senderActorID);

            //Update properties, if any has changed
            foreach (KeyValuePair<UUID, SyncInfoBase> partSyncInfo in GroupSyncInfos)
            {
                UUID uuid = partSyncInfo.Key;
                SyncInfoBase updatedPrimSyncInfo = partSyncInfo.Value;

                SceneObjectPart part = regionContext.Scene.GetSceneObjectPart(uuid);
                if (part == null)
                {
                    m_log.ErrorFormat("{0}: HandleSyncLinkObject, prim {1} not in local Scene Graph after LinkObjectBySync is called", LogHeader, uuid);
                }
                else
                {
                    regionContext.InfoManager.UpdateSyncInfoBySync(part.UUID, updatedPrimSyncInfo);
                }
            }
        }
        return true;
    }
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        if (Dir == Direction.Out && DataMap == null)
        {
            //Now encode the linkedGroup for sync
            OSDMap data = new OSDMap();
            OSDMap encodedSOG = EncodeSceneObject(LinkedGroup, regionContext);
            data["linkedGroup"] = encodedSOG;
            data["rootID"] = OSD.FromUUID(RootUUID);
            data["partCount"] = OSD.FromInteger(ChildrenIDs.Count);
            data["actorID"] = OSD.FromString(ActorID);
            int partNum = 0;

            string debugString = "";
            foreach (UUID partUUID in ChildrenIDs)
            {
                string partTempID = "part" + partNum;
                data[partTempID] = OSD.FromUUID(partUUID);
                partNum++;

                //m_log.DebugFormat("{0}: SendLinkObject to link {1},{2} with {3}, {4}", part.Name, part.UUID, root.Name, root.UUID);
                debugString += partUUID + ", ";
            }
            // m_log.DebugFormat("SyncLinkObject: SendLinkObject to link parts {0} with {1}, {2}", debugString, root.Name, root.UUID);
        }
        return base.ConvertOut(regionContext);
    }
}
// ====================================================================================================
public class SyncMsgDelinkObject : SyncMsgOSDMapData
{
    public override string DetailLogTagRcv { get { return "RcvDLnkObj"; } }
    public override string DetailLogTagSnd { get { return "SndDLnkObj"; } }

    //public List<SceneObjectPart> LocalPrims = new List<SceneObjectPart>();
    public List<UUID> DelinkPrimIDs;
    public List<UUID> BeforeDelinkGroupIDs;
    public List<SceneObjectGroup> AfterDelinkGroups;
    public List<Dictionary<UUID, SyncInfoBase>> PrimSyncInfo;

    public SyncMsgDelinkObject(List<UUID> pDelinkPrimIDs, List<UUID> pBeforeDlinkGroupIDs, List<SceneObjectGroup> pAfterDelinkGroups)
        : base()
    {
        DelinkPrimIDs = pDelinkPrimIDs;
        BeforeDelinkGroupIDs = pBeforeDlinkGroupIDs;
        AfterDelinkGroups = pAfterDelinkGroups;
    }
    public SyncMsgDelinkObject(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool ConvertIn(RegionSyncModule regionContext)
    {
        bool ret = false;
        if (base.ConvertIn(regionContext))
        {

            //LocalPrims = new List<SceneObjectPart>();
            DelinkPrimIDs = new List<UUID>();
            BeforeDelinkGroupIDs = new List<UUID>();
            AfterDelinkGroups = new List<SceneObjectGroup>();
            PrimSyncInfo = new List<Dictionary<UUID, SyncInfoBase>>();

            int partCount = DataMap["partCount"].AsInteger();
            for (int i = 0; i < partCount; i++)
            {
                string partTempID = "part" + i;
                UUID primID = DataMap[partTempID].AsUUID();
                //SceneObjectPart localPart = Scene.GetSceneObjectPart(primID);
                //localPrims.Add(localPart);
                DelinkPrimIDs.Add(primID);
            }

            int beforeGroupCount = DataMap["beforeGroupsCount"].AsInteger();
            for (int i = 0; i < beforeGroupCount; i++)
            {
                string groupTempID = "beforeGroup" + i;
                UUID beforeGroupID = DataMap[groupTempID].AsUUID();
                BeforeDelinkGroupIDs.Add(beforeGroupID);
            }

            int afterGroupsCount = DataMap["afterGroupsCount"].AsInteger();
            for (int i = 0; i < afterGroupsCount; i++)
            {
                string groupTempID = "afterGroup" + i;
                //string sogxml = data[groupTempID].AsString();
                SceneObjectGroup afterGroup;
                OSDMap encodedSOG = (OSDMap)DataMap[groupTempID];
                Dictionary<UUID, SyncInfoBase> groupSyncInfo;
                if (!DecodeSceneObject(encodedSOG, out afterGroup, out groupSyncInfo, regionContext.Scene))
                {
                    m_log.WarnFormat("{0}: Failed to decode scene object in HandleSyncDelinkObject", LogHeader);
                    return false;
                }

                AfterDelinkGroups.Add(afterGroup);
                PrimSyncInfo.Add(groupSyncInfo);
            }
            ret = true;
        }
        return ret;
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        if (base.HandleIn(regionContext))
        {
            // if this is a relay node, forward the message
            if (regionContext.IsSyncRelay)
            {
                List<SceneObjectGroup> tempBeforeDelinkGroups = new List<SceneObjectGroup>();
                foreach (UUID sogID in BeforeDelinkGroupIDs)
                {
                    SceneObjectGroup sog = regionContext.Scene.GetGroupByPrim(sogID);
                    tempBeforeDelinkGroups.Add(sog);
                }
                regionContext.SendDelinkObjectToRelevantSyncConnectors(ConnectorContext.otherSideActorID, tempBeforeDelinkGroups, this);
            }

            //DSL Scene.DelinkObjectsBySync(delinkPrimIDs, beforeDelinkGroupIDs, incomingAfterDelinkGroups);

            //Sync properties 
            //Update properties, for each prim in each deLinked-Object
            foreach (Dictionary<UUID, SyncInfoBase> primsSyncInfo in PrimSyncInfo)
            {
                foreach (KeyValuePair<UUID, SyncInfoBase> inPrimSyncInfo in primsSyncInfo)
                {
                    UUID uuid = inPrimSyncInfo.Key;
                    SyncInfoBase updatedPrimSyncInfo = inPrimSyncInfo.Value;

                    SceneObjectPart part = regionContext.Scene.GetSceneObjectPart(uuid);
                    if (part == null)
                    {
                        m_log.ErrorFormat("{0}: HandleSyncDelinkObject, prim {1} not in local Scene Graph after DelinkObjectsBySync is called", LogHeader, uuid);
                    }
                    else
                    {
                        regionContext.InfoManager.UpdateSyncInfoBySync(part.UUID, updatedPrimSyncInfo);
                    }
                }
            }
        }
        return true;
    }
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        if (Dir == Direction.Out && DataMap == null)
        {
            OSDMap data = new OSDMap();
            data["partCount"] = OSD.FromInteger(DelinkPrimIDs.Count);
            int partNum = 0;
            foreach (UUID partUUID in DelinkPrimIDs)
            {
                string partTempID = "part" + partNum;
                data[partTempID] = OSD.FromUUID(partUUID);
                partNum++;
            }
            //We also include the IDs of beforeDelinkGroups, for now it is more for sanity checking at the receiving end, so that the receiver 
            //could make sure its delink starts with the same linking state of the groups/prims.
            data["beforeGroupsCount"] = OSD.FromInteger(BeforeDelinkGroupIDs.Count);
            int groupNum = 0;
            foreach (UUID affectedGroupUUID in BeforeDelinkGroupIDs)
            {
                string groupTempID = "beforeGroup" + groupNum;
                data[groupTempID] = OSD.FromUUID(affectedGroupUUID);
                groupNum++;
            }

            //include the property values of each object after delinking, for synchronizing the values
            data["afterGroupsCount"] = OSD.FromInteger(AfterDelinkGroups.Count);
            groupNum = 0;
            foreach (SceneObjectGroup afterGroup in AfterDelinkGroups)
            {
                string groupTempID = "afterGroup" + groupNum;
                //string sogxml = SceneObjectSerializer.ToXml2Format(afterGroup);
                //data[groupTempID] = OSD.FromString(sogxml);
                OSDMap encodedSOG = EncodeSceneObject(afterGroup, regionContext);
                data[groupTempID] = encodedSOG;
                groupNum++;
            }
            DataMap = data;
        }
        return base.ConvertOut(regionContext);
    }
}
// ====================================================================================================
public class SyncMsgNewPresence : SyncMsgOSDMapData
{
    public override string DetailLogTagRcv { get { return "RcvNewPres"; } }
    public override string DetailLogTagSnd { get { return "SndNewPres"; } }

    public UUID Uuid = UUID.Zero;
    public ScenePresence SP { get; set; }

    public SyncMsgNewPresence(ScenePresence pSP)
        : base()
    {
        SP = pSP;
        Uuid = SP.UUID;
    }
    public SyncMsgNewPresence(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool ConvertIn(RegionSyncModule regionContext)
    {
        return base.ConvertIn(regionContext);
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        if (base.HandleIn(regionContext))
        {
            // Decode presence and syncInfo from message data
            SyncInfoBase syncInfo;
            DecodeScenePresence(DataMap, out syncInfo, regionContext.Scene);
            Uuid = syncInfo.UUID;

            // if this is a relay node, forward the message
            if (regionContext.IsSyncRelay)
                regionContext.SendSpecialUpdateToRelevantSyncConnectors(ConnectorContext.otherSideActorID, this);

            //Add the SyncInfo to SyncInfoManager
            regionContext.InfoManager.InsertSyncInfo(syncInfo.UUID, syncInfo);

            // Get ACD and PresenceType from decoded SyncInfoPresence
            // NASTY CASTS AHEAD!
            AgentCircuitData acd = new AgentCircuitData();
            acd.UnpackAgentCircuitData((OSDMap)(((SyncInfoPresence)syncInfo).CurrentlySyncedProperties[SyncableProperties.Type.AgentCircuitData].LastUpdateValue));
            // Unset the ViaLogin flag since this presence is being added to the scene by sync (not via login)
            acd.teleportFlags &= ~(uint)TeleportFlags.ViaLogin;
            PresenceType pt = (PresenceType)(int)(((SyncInfoPresence)syncInfo).CurrentlySyncedProperties[SyncableProperties.Type.PresenceType].LastUpdateValue);

            // Add the decoded circuit to local scene
            regionContext.Scene.AuthenticateHandler.AddNewCircuit(acd.circuitcode, acd);

            // Create a client and add it to the local scene
            IClientAPI client = new RegionSyncAvatar(acd.circuitcode, regionContext.Scene, acd.AgentID, acd.firstname, acd.lastname, acd.startpos);
            syncInfo.SceneThing = regionContext.Scene.AddNewClient(client, pt);
            // Might need to trigger something here to send new client messages to connected clients
        }
        return true;
    }
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        if (Dir == Direction.Out && DataMap == null)
        {
            DataMap = EncodeScenePresence(SP, regionContext);
        }
        return base.ConvertOut(regionContext);
    }
    public override void LogTransmission(SyncConnector connectorContext)
    {
        if (RegionContext != null)
            RegionContext.DetailedUpdateWrite(DetailLogTagSnd, Uuid, 0, ZeroUUID, connectorContext.otherSideActorID, DataLength);
    }
}
// ====================================================================================================
public class SyncMsgRemovedPresence : SyncMsgOSDMapData
{
    public override string DetailLogTagRcv { get { return "RcvRemPres"; } }
    public override string DetailLogTagSnd { get { return "SndRemPres"; } }

    public UUID Uuid = UUID.Zero;

    public SyncMsgRemovedPresence(UUID pUuid)
        : base()
    {
        Uuid = pUuid;
    }
        
    public SyncMsgRemovedPresence(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool ConvertIn(RegionSyncModule regionContext)
    {
        bool ret = false;
        if (base.ConvertIn(regionContext))
        {
            Uuid = DataMap["uuid"].AsUUID();
            ret = true;
        }
        return ret;
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        if (base.HandleIn(regionContext))
        {
            if (!regionContext.InfoManager.SyncInfoExists(Uuid))
                return false;

            // if this is a relay node, forward the message
            if (regionContext.IsSyncRelay)
            {
                regionContext.SendSpecialUpdateToRelevantSyncConnectors(ConnectorContext.otherSideActorID, this);
            }

            // This limits synced avatars to real clients (no npcs) until we sync PresenceType field
            regionContext.Scene.RemoveClient(Uuid, false);
        }
        return true;
    }
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        if (Dir == Direction.Out && DataMap == null)
        {
            OSDMap data = new OSDMap();
            data["uuid"] = OSD.FromUUID(Uuid);
            DataMap = data;
        }
        return base.ConvertOut(regionContext);
    }
    public override void LogTransmission(SyncConnector connectorContext)
    {
        if (RegionContext != null)
            RegionContext.DetailedUpdateWrite(DetailLogTagSnd, Uuid, 0, ZeroUUID, connectorContext.otherSideActorID, DataLength);
    }
}
// ====================================================================================================
public class SyncMsgRegionName : SyncMsgOSDMapData
{
    public override string DetailLogTagRcv { get { return "RcvRegName"; } }
    public override string DetailLogTagSnd { get { return "SndRegName"; } }

    public string RegName;

    public SyncMsgRegionName(string pRegionName)
        : base()
    {
        RegName = pRegionName;
    }
    public SyncMsgRegionName(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool ConvertIn(RegionSyncModule regionContext)
    {
        bool ret = false;
        if (base.ConvertIn(regionContext))
        {
            RegName = DataMap["regionName"];
            ret = true;
        }
        else
        {
            RegName = "UNKNOWN";
        }
        return ret;
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        if (base.HandleIn(regionContext))
        {
            ConnectorContext.otherSideRegionName = RegName;
            if (regionContext.IsSyncRelay)
            {
                SyncMsgRegionName msg = new SyncMsgRegionName(regionContext.Scene.RegionInfo.RegionName);
                msg.ConvertOut(regionContext);
                ConnectorContext.ImmediateOutgoingMsg(msg);
            }
        }
        return true;
    }
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        if (Dir == Direction.Out && DataMap == null)
        {
            OSDMap data = new OSDMap();
            data["regionName"] = RegName;
            DataMap = data;
        }
        return base.ConvertOut(regionContext);
    }
    public override void LogTransmission(SyncConnector connectorContext)
    {
        if (RegionContext != null)
            RegionContext.DetailedUpdateWrite(DetailLogTagSnd, ZeroUUID, 0, RegionContext.Scene.RegionInfo.RegionName,
                                    connectorContext.otherSideActorID, DataLength);
    }
}
// ====================================================================================================
public class SyncMsgActorID : SyncMsgOSDMapData
{
    public override string DetailLogTagRcv { get { return "RcvActorID"; } }
    public override string DetailLogTagSnd { get { return "SndActorID"; } }

    private string ActorID;

    public SyncMsgActorID(string pActorID)
        : base()
    {
        ActorID = pActorID;
    }
    public SyncMsgActorID(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool ConvertIn(RegionSyncModule regionContext)
    {
        bool ret = false;
        if (base.ConvertIn(regionContext))
        {
            ActorID = DataMap["actorID"];
            ret = true;
        }
        else
        {
            ActorID = "UNKNOWN";
        }
        return ret;
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        if (base.HandleIn(regionContext))
        {
            ConnectorContext.otherSideActorID = ActorID;
            if (regionContext.IsSyncRelay)
            {
                SyncMsgActorID msg = new SyncMsgActorID(regionContext.ActorID);
                msg.ConvertOut(regionContext);
                ConnectorContext.ImmediateOutgoingMsg(msg);
            }
        }
        return true;
    }
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        if (Dir == Direction.Out && DataMap == null)
        {
            OSDMap data = new OSDMap();
            data["actorID"] = ActorID;
            DataMap = data;
        }
        return base.ConvertOut(regionContext);
    }
    public override void LogTransmission(SyncConnector connectorContext)
    {
        if (RegionContext != null)
            RegionContext.DetailedUpdateWrite(DetailLogTagSnd, ZeroUUID, 0, ActorID, ConnectorContext.otherSideActorID, DataLength);
    }
}
// ====================================================================================================
public class SyncMsgRegionStatus : SyncMsgOSDMapData
{
    public override string DetailLogTagRcv { get { return "RcvRgnStat"; } }
    public override string DetailLogTagSnd { get { return "SndRgnStat"; } }

    public SyncMsgRegionStatus()
        : base()
    {
    }
    public SyncMsgRegionStatus(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool ConvertIn(RegionSyncModule regionContext)
    {
        return base.ConvertIn(regionContext);
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        return base.HandleIn(regionContext);
    }
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        return base.ConvertOut(regionContext);
    }
}
// ====================================================================================================
public abstract class SyncMsgEvent : SyncMsgOSDMapData
{
    public override string DetailLogTagRcv { get { return "RcvEventtt"; } }
    public override string DetailLogTagSnd { get { return "SndEventtt"; } }

    public string SyncID { get; set; }
    public ulong SequenceNum { get; set; }

    public SyncMsgEvent()
        : base()
    {
        SyncID = "UNKNOWN";
        SequenceNum = 1;
    }
    public SyncMsgEvent(string pSyncID, ulong pSeqNum)
        : base()
    {
        SyncID = pSyncID;
        SequenceNum = pSeqNum;
    }
    public SyncMsgEvent(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool ConvertIn(RegionSyncModule regionContext)
    {
        bool ret = false;
        if (base.ConvertIn(regionContext))
        {
            SyncID = DataMap["syncID"].AsString();
            SequenceNum = DataMap["seqNum"].AsULong();
            ret = true;
        }
        return ret;
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        bool ret = false;
        if (base.HandleIn(regionContext))
        {
            //check if this is a duplicate event message that we have received before
            if (regionContext.EventRecord.IsSEQReceived(SyncID, SequenceNum))
            {
                m_log.ErrorFormat("Duplicate event {0} originated from {1}, seq# {2} has been received", MType, SyncID, SequenceNum);
                return false;
            }
            else
            {
                regionContext.EventRecord.RecordEventReceived(SyncID, SequenceNum);
            }

            // if this is a relay node, forward the message
            if (regionContext.IsSyncRelay)
            {
                regionContext.SendSceneEventToRelevantSyncConnectors(ConnectorContext.otherSideActorID, this, null);
            }
            ret = true;
        }
        return ret;
    }
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        if (Dir == Direction.Out && DataMap != null)
        {
            DataMap["syncID"] = OSD.FromString(SyncID);
            DataMap["seqNum"] = OSD.FromULong(SequenceNum);
        }
        return base.ConvertOut(regionContext);
    }
    // Helper routines.
    // TODO: There could be an intermediate class SyncMsgEventChat
    protected OSChatMessage PrepareOnChatArgs(OSDMap data, RegionSyncModule regionContext)
    {
        OSChatMessage args = new OSChatMessage();
        args.Channel = data["channel"].AsInteger();
        args.Message = data["msg"].AsString();
        args.Position = data["pos"].AsVector3();
        args.From = data["name"].AsString();
        args.SenderUUID = data["id"].AsUUID();
        args.Scene = regionContext.Scene;
        args.Type = (ChatTypeEnum)data["type"].AsInteger();

        // Need to look up the sending object within this scene!
        args.SenderObject = regionContext.Scene.GetScenePresence(args.SenderUUID);
        if(args.SenderObject != null)
            args.Sender = ((ScenePresence)args.SenderObject).ControllingClient;
        else
            args.SenderObject = regionContext.Scene.GetSceneObjectPart(args.SenderUUID);
        //m_log.WarnFormat("RegionSyncModule.PrepareOnChatArgs: name:\"{0}\" msg:\"{1}\" pos:{2} id:{3}", args.From, args.Message, args.Position, args.SenderUUID);
        return args;
    }
    protected OSDMap PrepareChatArgs(OSChatMessage chat)
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
    public override void LogTransmission(SyncConnector connectorContext)
    {
        // Suppress automatic logging as message logging is done in RegionSyncModule when more info is known.
    }
}
// ====================================================================================================
public class SyncMsgNewScript : SyncMsgEvent
{
    public override string DetailLogTagRcv { get { return "RcvNewScpt"; } }
    public override string DetailLogTagSnd { get { return "SndNewScpt"; } }

    public UUID Uuid { get; set; }
    public UUID AgentID { get; set; }
    public UUID ItemID { get; set; }
    public HashSet<SyncableProperties.Type> SyncableProperties;

    public HashSet<SyncedProperty> UpdatedProperties;

    public SyncMsgNewScript(UUID pUuid, UUID pAgentID, UUID pItemID, HashSet<SyncableProperties.Type> pSyncableProperties)
        : base()
    {
        Uuid = pUuid;
        AgentID = pAgentID;
        ItemID = pItemID;
        SyncableProperties = pSyncableProperties;
    }
    public SyncMsgNewScript(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool ConvertIn(RegionSyncModule regionContext)
    {
        bool ret = false;
        if (base.ConvertIn(regionContext))
        {
            AgentID = DataMap["agentID"].AsUUID();
            Uuid = DataMap["uuid"].AsUUID();
            ItemID = DataMap["itemID"].AsUUID();
            UpdatedProperties = SyncedProperty.DecodeProperties(DataMap);
            ret = true;
        }
        return ret;
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        if (base.HandleIn(regionContext))
        {
            SceneObjectPart localPart = regionContext.Scene.GetSceneObjectPart(Uuid);

            if (localPart == null || localPart.ParentGroup.IsDeleted)
            {
                m_log.ErrorFormat("{0}: HandleRemoteEvent_OnNewScript: prim {1} no longer in local SceneGraph", LogHeader, Uuid);
                return false;
            }

            if (UpdatedProperties.Count > 0)
            {
                HashSet<SyncableProperties.Type> propertiesUpdated = regionContext.InfoManager.UpdateSyncInfoBySync(Uuid, UpdatedProperties);
            }

            //The TaskInventory value might have already been sync'ed by UpdatedPrimProperties, 
            //but we still need to create the script instance by reading out the inventory.
            regionContext.RememberLocallyGeneratedEvent(MsgType.NewScript, AgentID, localPart, ItemID);
            regionContext.Scene.EventManager.TriggerNewScript(AgentID, localPart, ItemID);
            regionContext.ForgetLocallyGeneratedEvent();
        }
        return true;
    }
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        if (Dir == Direction.Out && DataMap == null)
        {
            OSDMap data = regionContext.InfoManager.EncodeProperties(Uuid, SyncableProperties);
            //syncData already includes uuid, add agentID and itemID next
            data["agentID"] = OSD.FromUUID(AgentID);
            data["itemID"] = OSD.FromUUID(ItemID);
            DataMap = data;
        }
        return base.ConvertOut(regionContext);
    }
}
// ====================================================================================================
public class SyncMsgUpdateScript : SyncMsgEvent
{
    public override string DetailLogTagRcv { get { return "RcvUpDScpt"; } }
    public override string DetailLogTagSnd { get { return "SndUpDScpt"; } }

    public UUID AgentID { get; set; }
    public UUID ItemID { get; set; }
    public UUID PrimID { get; set; }
    public bool IsRunning { get; set; }
    public UUID AssetID { get; set; }

    public SyncMsgUpdateScript(UUID pAgentID, UUID pItemID, UUID pPrimID, bool pIsRunning, UUID pAssetID)
        : base()
    {
        AgentID = pAgentID;
        ItemID = pItemID;
        PrimID = pPrimID;
        IsRunning = pIsRunning;
        AssetID = pAssetID;
    }
    public SyncMsgUpdateScript(string pSyncID, ulong pSeqNum)
        : base(pSyncID, pSeqNum)
    {
    }
    public SyncMsgUpdateScript(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool ConvertIn(RegionSyncModule regionContext)
    {
        bool ret = false;
        if (base.ConvertIn(regionContext))
        {
            AgentID = DataMap["agentID"].AsUUID();
            ItemID = DataMap["itemID"].AsUUID();
            PrimID = DataMap["primID"].AsUUID();
            IsRunning = DataMap["running"].AsBoolean();
            AssetID = DataMap["assetID"].AsUUID();
            ret = true;
        }
        return ret;
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        if (base.HandleIn(regionContext))
        {
            //trigger the event in the local scene
            regionContext.RememberLocallyGeneratedEvent(MsgType.UpdateScript, AgentID, ItemID, PrimID, IsRunning, AssetID);
            regionContext.Scene.EventManager.TriggerUpdateScript(AgentID, ItemID, PrimID, IsRunning, AssetID);
            regionContext.ForgetLocallyGeneratedEvent();
        }
        return true;
    }
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        if (Dir == Direction.Out && DataMap == null)
        {
            OSDMap data = new OSDMap(5 + 2);
            data["agentID"] = OSD.FromUUID(AgentID);
            data["itemID"] = OSD.FromUUID(ItemID);
            data["primID"] = OSD.FromUUID(PrimID);
            data["running"] = OSD.FromBoolean(IsRunning);
            data["assetID"] = OSD.FromUUID(AssetID);
            DataMap = data;
        }
        return base.ConvertOut(regionContext);
    }
}
// ====================================================================================================
public class SyncMsgScriptReset : SyncMsgEvent
{
    public override string DetailLogTagRcv { get { return "RcvRstScpt"; } }
    public override string DetailLogTagSnd { get { return "SndRstScpt"; } }

    public UUID ItemID { get; set; }
    public UUID PrimID { get; set; }

    public SyncMsgScriptReset(UUID pItemID, UUID pPrimID)
        : base()
    {
        ItemID = pItemID;
        PrimID = pPrimID;
    }
    public SyncMsgScriptReset(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool ConvertIn(RegionSyncModule regionContext)
    {
        bool ret = false;
        if (base.ConvertIn(regionContext))
        {
            ItemID = DataMap["itemID"].AsUUID();
            PrimID = DataMap["primID"].AsUUID();
            ret = true;
        }
        return ret;
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        if (base.HandleIn(regionContext))
        {
            SceneObjectPart part = regionContext.Scene.GetSceneObjectPart(PrimID);
            if (part == null || part.ParentGroup.IsDeleted)
            {
                m_log.ErrorFormat("{0}: part {1} does not exist, or is deleted", LogHeader, PrimID);
                return false;
            }
            regionContext.RememberLocallyGeneratedEvent(MsgType.ScriptReset, part.LocalId, ItemID);
            regionContext.Scene.EventManager.TriggerScriptReset(part.LocalId, ItemID);
            regionContext.ForgetLocallyGeneratedEvent();
        }
        return false;
    }
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        if (Dir == Direction.Out && DataMap == null)
        {
            OSDMap data = new OSDMap(3 + 2);
            data["itemID"] = OSD.FromUUID(ItemID);
            data["primID"] = OSD.FromUUID(PrimID);
            DataMap = data;
        }
        return base.ConvertOut(regionContext);
    }
}
// ====================================================================================================
public class SyncMsgChatFromClient : SyncMsgEvent
{
    public override string DetailLogTagRcv { get { return "RcvChatClt"; } }
    public override string DetailLogTagSnd { get { return "SndChatClt"; } }

    public OSChatMessage ChatMessage { get; set; }

    public SyncMsgChatFromClient(OSChatMessage pChatMessage)
        : base()
    {
        ChatMessage = pChatMessage;
    }
    public SyncMsgChatFromClient(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool ConvertIn(RegionSyncModule regionContext)
    {
        bool ret = false;
        if (base.ConvertIn(regionContext))
        {
            ChatMessage = PrepareOnChatArgs(DataMap, regionContext);
            ret = true;
        }
        return ret;
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        bool ret = false;
        if (base.HandleIn(regionContext))
        {
            //m_log.WarnFormat("RegionSyncModule.HandleRemoteEvent_OnChatFromClient {0}:{1}", args.From, args.Message);
            if (ChatMessage.Sender is RegionSyncAvatar)
                ((RegionSyncAvatar)ChatMessage.Sender).SyncChatFromClient(ChatMessage);
            ret = true;
        }
        return ret;
    }
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        if (Dir == Direction.Out && DataMap == null)
        {
            DataMap = PrepareChatArgs(ChatMessage);
        }
        return base.ConvertOut(regionContext);
    }
}
// ====================================================================================================
public class SyncMsgChatFromWorld : SyncMsgEvent
{
    public override string DetailLogTagRcv { get { return "RcvChatWld"; } }
    public override string DetailLogTagSnd { get { return "SndChatWld"; } }

    public OSChatMessage ChatMessage { get; set; }

    public SyncMsgChatFromWorld(OSChatMessage pChatMessage)
        : base()
    {
        ChatMessage = pChatMessage;
    }
    public SyncMsgChatFromWorld(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool ConvertIn(RegionSyncModule regionContext)
    {
        bool ret = false;
        if (base.ConvertIn(regionContext))
        {
            ChatMessage = PrepareOnChatArgs(DataMap, regionContext);
            ret = true;
        }
        return ret;
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        if (base.HandleIn(regionContext))
        {
            //m_log.WarnFormat("RegionSyncModule.HandleRemoteEvent_OnChatFromWorld {0}:{1}", args.From, args.Message);
            regionContext.RememberLocallyGeneratedEvent(MsgType.ChatFromWorld, ChatMessage);
            // Let ChatModule get the event and deliver it to avatars
            regionContext.Scene.EventManager.TriggerOnChatFromWorld(ChatMessage.SenderObject, ChatMessage);
            regionContext.ForgetLocallyGeneratedEvent();
        }
        return true;
    }
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        if (Dir == Direction.Out && DataMap == null)
        {
            DataMap = PrepareChatArgs(ChatMessage);
        }
        return base.ConvertOut(regionContext);
    }
}
// ====================================================================================================
public class SyncMsgChatBroadcast : SyncMsgEvent
{
    public override string DetailLogTagRcv { get { return "RcvChatBct"; } }
    public override string DetailLogTagSnd { get { return "SndChatBct"; } }

    public OSChatMessage ChatMessage { get; set; }

    public SyncMsgChatBroadcast(OSChatMessage pChatMessage)
        : base()
    {
        ChatMessage = pChatMessage;
    }
    public SyncMsgChatBroadcast(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool ConvertIn(RegionSyncModule regionContext)
    {
        bool ret = false;
        if (base.ConvertIn(regionContext))
        {
            ChatMessage = PrepareOnChatArgs(DataMap, regionContext);
            ret = true;
        }
        return ret;
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        if (base.HandleIn(regionContext))
        {
            //m_log.WarnFormat("RegionSyncModule.HandleRemoteEvent_OnChatBroadcast {0}:{1}", args.From, args.Message);
            regionContext.RememberLocallyGeneratedEvent(MsgType.ChatBroadcast, ChatMessage);
            regionContext.Scene.EventManager.TriggerOnChatBroadcast(ChatMessage.SenderObject, ChatMessage);
            regionContext.ForgetLocallyGeneratedEvent();
        }
        return true;
    }
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        if (Dir == Direction.Out && DataMap == null)
        {
            DataMap = PrepareChatArgs(ChatMessage);
        }
        return base.ConvertOut(regionContext);
    }
}
// ====================================================================================================
public abstract class SyncMsgEventGrabber : SyncMsgEvent
{
    public UUID AgentID { get; set; }
    public UUID PrimID { get; set; }
    public UUID OriginalPrimID { get; set; }
    public Vector3 OffsetPos { get; set; }
    public SurfaceTouchEventArgs SurfaceArgs { get; set; }

    public SceneObjectPart SOP { get; set; }
    public uint OriginalID { get; set; }
    public ScenePresence SP;
    
    public SyncMsgEventGrabber(UUID pAgentID, UUID pPrimID, UUID pOrigPrimID, Vector3 pOffset, SurfaceTouchEventArgs pTouchArgs)
        : base()
    {
        AgentID = pAgentID;
        PrimID = pPrimID;
        OriginalPrimID = pOrigPrimID;
        OffsetPos = pOffset;
        SurfaceArgs = pTouchArgs;
    }
    public SyncMsgEventGrabber(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool ConvertIn(RegionSyncModule regionContext)
    {
        bool ret = false;
        if (base.ConvertIn(regionContext))
        {
            AgentID = DataMap["agentID"].AsUUID();
            PrimID = DataMap["primID"].AsUUID();
            OriginalPrimID = DataMap["originalPrimID"].AsUUID();
            OffsetPos = DataMap["offsetPos"].AsVector3();
            SurfaceArgs = new SurfaceTouchEventArgs();
            SurfaceArgs.Binormal = DataMap["binormal"].AsVector3();
            SurfaceArgs.FaceIndex = DataMap["faceIndex"].AsInteger();
            SurfaceArgs.Normal = DataMap["normal"].AsVector3();
            SurfaceArgs.Position = DataMap["position"].AsVector3();
            SurfaceArgs.STCoord = DataMap["stCoord"].AsVector3();
            SurfaceArgs.UVCoord = DataMap["uvCoord"].AsVector3();
            ret = true;
        }
        return ret;
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        if (base.HandleIn(regionContext))
        {
            SOP = regionContext.Scene.GetSceneObjectPart(PrimID);
            if (SOP == null)
            {
                m_log.ErrorFormat("{0}: HandleRemoteEvent_OnObjectGrab: no prim with ID {1}", LogHeader, PrimID);
                return false;
            }
            if (OriginalPrimID != UUID.Zero)
            {
                SceneObjectPart originalPart = regionContext.Scene.GetSceneObjectPart(OriginalPrimID);
                OriginalID = originalPart.LocalId;
            }

            // Get the scene presence in local scene that triggered the event
            if (!regionContext.Scene.TryGetScenePresence(AgentID, out SP))
            {
                m_log.ErrorFormat("{0} HandleRemoteEvent_OnObjectGrab: could not get ScenePresence for uuid {1}", LogHeader, AgentID);
                return false;
            }
        }
        return true;
    }
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        if (Dir == Direction.Out && DataMap == null)
        {
            OSDMap data = new OSDMap();
            data["agentID"] = OSD.FromUUID(AgentID);
            data["primID"] = OSD.FromUUID(PrimID);
            data["originalPrimID"] = OSD.FromUUID(OriginalPrimID);
            data["offsetPos"] = OSD.FromVector3(OffsetPos);
            data["binormal"] = OSD.FromVector3(SurfaceArgs.Binormal);
            data["faceIndex"] = OSD.FromInteger(SurfaceArgs.FaceIndex);
            data["normal"] = OSD.FromVector3(SurfaceArgs.Normal);
            data["position"] = OSD.FromVector3(SurfaceArgs.Position);
            data["stCoord"] = OSD.FromVector3(SurfaceArgs.STCoord);
            data["uvCoord"] = OSD.FromVector3(SurfaceArgs.UVCoord);
            DataMap = data;
        }
        return base.ConvertOut(regionContext);
    }
}
// ====================================================================================================
public class SyncMsgObjectGrab : SyncMsgEventGrabber
{
    public override string DetailLogTagRcv { get { return "RcvGrabbbb"; } }
    public override string DetailLogTagSnd { get { return "SndGrabbbb"; } }

    public SyncMsgObjectGrab(UUID pAgentID, UUID pPrimID, UUID pOrigPrimID, Vector3 pOffset, SurfaceTouchEventArgs pTouchArgs)
        : base(pAgentID, pPrimID, pOrigPrimID, pOffset, pTouchArgs)
    {
    }
    public SyncMsgObjectGrab(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool ConvertIn(RegionSyncModule regionContext)
    {
        return base.ConvertIn(regionContext);
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        bool ret = base.HandleIn(regionContext);
        if (ret)
        {
            regionContext.RememberLocallyGeneratedEvent(MsgType.ObjectGrab, SOP.LocalId, OriginalID, OffsetPos, SP.ControllingClient, SurfaceArgs);
            regionContext.Scene.EventManager.TriggerObjectGrab(SOP.LocalId, OriginalID, OffsetPos, SP.ControllingClient, SurfaceArgs);
            regionContext.ForgetLocallyGeneratedEvent();
        }
        return ret;
    }
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        return base.ConvertOut(regionContext);
    }
}
// ====================================================================================================
public class SyncMsgObjectGrabbing : SyncMsgEventGrabber
{
    public override string DetailLogTagRcv { get { return "RcvGrabing"; } }
    public override string DetailLogTagSnd { get { return "SndGrabing"; } }

    public SyncMsgObjectGrabbing(UUID pAgentID, UUID pPrimID, UUID pOrigPrimID, Vector3 pOffset, SurfaceTouchEventArgs pTouchArgs)
        : base(pAgentID, pPrimID, pOrigPrimID, pOffset, pTouchArgs)
    {
    }
    public SyncMsgObjectGrabbing(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool ConvertIn(RegionSyncModule regionContext)
    {
        return base.ConvertIn(regionContext);
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        bool ret = base.HandleIn(regionContext);
        if (ret)
        {
            regionContext.RememberLocallyGeneratedEvent(MsgType.ObjectGrabbing, SOP.LocalId, OriginalID, OffsetPos, SP.ControllingClient, SurfaceArgs);
            regionContext.Scene.EventManager.TriggerObjectGrabbing(SOP.LocalId, OriginalID, OffsetPos, SP.ControllingClient, SurfaceArgs);
            regionContext.ForgetLocallyGeneratedEvent();
        }
        return ret;
    }
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        return base.ConvertOut(regionContext);
    }
}
// ====================================================================================================
public class SyncMsgObjectDeGrab : SyncMsgEventGrabber
{
    public override string DetailLogTagRcv { get { return "RcvDeGrabb"; } }
    public override string DetailLogTagSnd { get { return "SndDeGrabb"; } }

    public SyncMsgObjectDeGrab(UUID pAgentID, UUID pPrimID, UUID pOrigPrimID, Vector3 pOffset, SurfaceTouchEventArgs pTouchArgs)
        : base(pAgentID, pPrimID, pOrigPrimID, pOffset, pTouchArgs)
    {
    }
    public SyncMsgObjectDeGrab(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool ConvertIn(RegionSyncModule regionContext)
    {
        return base.ConvertIn(regionContext);
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        bool ret = base.HandleIn(regionContext);
        if (ret)
        {
            regionContext.RememberLocallyGeneratedEvent(MsgType.ObjectDeGrab, SOP.LocalId, OriginalID, SP.ControllingClient, SurfaceArgs);
            regionContext.Scene.EventManager.TriggerObjectDeGrab(SOP.LocalId, OriginalID, SP.ControllingClient, SurfaceArgs);
            regionContext.ForgetLocallyGeneratedEvent();
        }
        return ret;
    }
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        return base.ConvertOut(regionContext);
    }
}
// ====================================================================================================
public class SyncMsgAttach : SyncMsgEvent
{
    public override string DetailLogTagRcv { get { return "RcvAttachh";  } }
    public override string DetailLogTagSnd { get { return "SndAttachh"; } }

    public UUID PrimID { get; set; }
    public UUID ItemID { get; set; }
    public UUID AvatarID { get; set; }

    public SyncMsgAttach(UUID pPrimID, UUID pItemID, UUID pAvatarID)
        : base()
    {
        PrimID = pPrimID;
        ItemID = pItemID;
        AvatarID = pAvatarID;
    }
    public SyncMsgAttach(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool ConvertIn(RegionSyncModule regionContext)
    {
        bool ret = false;
        if (base.ConvertIn(regionContext))
        {
            PrimID = DataMap["primID"].AsUUID();
            ItemID = DataMap["itemID"].AsUUID();
            AvatarID = DataMap["avatarID"].AsUUID();
            ret = true;
        }
        return ret;
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        if (base.HandleIn(regionContext))
        {
            SceneObjectPart part = regionContext.Scene.GetSceneObjectPart(PrimID);
            if (part == null)
            {
                m_log.WarnFormat("{0} HandleRemoteEvent_OnAttach: no part with UUID {1} found", LogHeader, PrimID);
                return false;
            }

            uint localID = part.LocalId;
            regionContext.RememberLocallyGeneratedEvent(MsgType.Attach, localID, ItemID, AvatarID);
            regionContext.Scene.EventManager.TriggerOnAttach(localID, ItemID, AvatarID);
            regionContext.ForgetLocallyGeneratedEvent();
        }
        return true;
    }
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        if (Dir == Direction.Out && DataMap == null)
        {
            OSDMap data = new OSDMap(3 + 2);
            data["primID"] = OSD.FromUUID(PrimID);
            data["itemID"] = OSD.FromUUID(ItemID);
            data["avatarID"] = OSD.FromUUID(AvatarID);
            DataMap = data;
        }
        return base.ConvertOut(regionContext);
    }
}
// ====================================================================================================
public abstract class SyncMsgEventCollision : SyncMsgEvent
{
    public UUID CollideeUUID;
    public uint CollideeID;
    public List<DetectedObject> Colliders;
    protected List<UUID> CollidersNotFound;

    public SyncMsgEventCollision(UUID pCollidee, uint pCollideeID, List<DetectedObject> pColliders)
        : base()
    {
        CollideeUUID = pCollidee;
        CollideeID = pCollideeID;
        Colliders = pColliders;
    }
    public SyncMsgEventCollision(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool ConvertIn(RegionSyncModule regionContext)
    {
        bool ret = false;
        if (base.ConvertIn(regionContext))
        {
            CollideeUUID = DataMap["uuid"].AsUUID();
            SceneObjectPart collisionPart = regionContext.Scene.GetSceneObjectPart(CollideeUUID);
            if (collisionPart == null)
            {
                m_log.ErrorFormat("{0}: HandleRemoteEvent_{1}: no part with UUID {2} found, event initiator {3}", 
                                LogHeader, MType.ToString(), CollideeUUID, regionContext.SyncID);
                return false;
            }
            CollideeID = collisionPart.LocalId;

            // Loop through all the passed UUIDs and build DetectedObject's for the collided with objects
            OSDArray collisionUUIDs = (OSDArray)DataMap["collisionUUIDs"];
            if (collisionUUIDs != null)
            {
                Colliders = new List<DetectedObject>();
                CollidersNotFound = new List<UUID>();

                switch (MType)
                {
                    case MsgType.ScriptColliding:
                    case MsgType.ScriptCollidingStart:
                    case MsgType.ScriptCollidingEnd:
                        BuildColliders(collisionUUIDs, regionContext);
                        break;
                    case MsgType.ScriptLandColliding:
                    case MsgType.ScriptLandCollidingStart:
                    case MsgType.ScriptLandCollidingEnd:
                        BuildLandColliders(collisionPart, collisionUUIDs, regionContext);
                        break;
                }
                if (CollidersNotFound.Count > 0)
                {
                    // If collision events are received for objects that are not found, that usually
                    //     means the events beat the object creation. This re-queues the collision event to
                    //     replay when, hopefully, the object has been created.
                    RequeueCollisionsForNotFoundObjects(regionContext, collisionPart, CollidersNotFound);
                }
            }
            ret = true;
        }
        return ret;
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        return base.HandleIn(regionContext);
    }
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        if (Dir == Direction.Out && DataMap == null)
        {
            OSDMap data = new OSDMap();
            OSDArray collisionUUIDs = new OSDArray();
            foreach (DetectedObject detObj in Colliders)
            {
                collisionUUIDs.Add(OSD.FromUUID(detObj.keyUUID));
            }

            data["uuid"] = OSD.FromUUID(CollideeUUID);
            data["collisionUUIDs"] = collisionUUIDs;

            DataMap = data;
        }
        return base.ConvertOut(regionContext);
    }

    #region UUID To DetectedObject converters
    private void BuildColliders(OSDArray collisionUUIDs, RegionSyncModule regionContext)
    {
        for (int i = 0; i < collisionUUIDs.Count; i++)
        {
            OSD arg = collisionUUIDs[i];
            UUID collidingUUID = arg.AsUUID();

            SceneObjectPart obj = regionContext.Scene.GetSceneObjectPart(collidingUUID);
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
                Colliders.Add(detobj);
            }
            else
            {
                //collision object is not a prim, check if it's an avatar
                ScenePresence av = regionContext.Scene.GetScenePresence(collidingUUID);
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
                    Colliders.Add(detobj);
                }
                else
                {
                    // m_log.WarnFormat("HandleRemoteEvent_ScriptCollidingStart for SOP {0},{1} with SOP/SP {2}, but the latter is not found in local Scene. Saved for later processing",
                    //             collisionPart.Name, collisionPart.UUID, collidingUUID);
                    CollidersNotFound.Add(collidingUUID);
                }
            }
        }
    }
    private void BuildLandColliders(SceneObjectPart collisionPart, OSDArray collisionUUIDs, RegionSyncModule regionContext)
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
                Colliders.Add(detobj);
            }
        }
    }
    // If collision events are received for objects that are not found, that usually means the events beat the
    //     object creation. This re-queues the collision event to replay when, hopefully, the object has been created.
    // TODO:
    private void RequeueCollisionsForNotFoundObjects(RegionSyncModule regionContext, SceneObjectPart collisionPart, List<UUID> CollidersNotFound)
    {
    }
    #endregion // UUID To DetectedObject converters
}
// ====================================================================================================
public class SyncMsgScriptCollidingStart : SyncMsgEventCollision
{
    public override string DetailLogTagRcv { get { return "RcvColStrt"; } }
    public override string DetailLogTagSnd { get { return "SndColStrt"; } }

    public SyncMsgScriptCollidingStart(UUID pCollidee, uint pCollideeID, List<DetectedObject> pColliders)
        : base(pCollidee, pCollideeID, pColliders)
    {
    }
    public SyncMsgScriptCollidingStart(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool ConvertIn(RegionSyncModule regionContext)
    {
        return base.ConvertIn(regionContext);
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        bool ret = false;
        if (base.HandleIn(regionContext))
        {
            ColliderArgs CollidingMessage = new ColliderArgs();
            CollidingMessage.Colliders = Colliders;

            m_log.DebugFormat("ScriptCollidingStart received for {0}", CollideeID);
            regionContext.RememberLocallyGeneratedEvent(MType, CollideeID, CollidingMessage);
            regionContext.Scene.EventManager.TriggerScriptCollidingStart(CollideeID, CollidingMessage);
            regionContext.ForgetLocallyGeneratedEvent();

            ret = true;
        }
        return ret;
    }
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        return base.ConvertOut(regionContext);
    }
}
// ====================================================================================================
public class SyncMsgScriptColliding : SyncMsgEventCollision
{
    public override string DetailLogTagRcv { get { return "RcvColidng"; } }
    public override string DetailLogTagSnd { get { return "SndColidng"; } }

    public SyncMsgScriptColliding(UUID pCollidee, uint pCollideeID, List<DetectedObject> pColliders)
        : base(pCollidee, pCollideeID, pColliders)
    {
    }
    public SyncMsgScriptColliding(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool ConvertIn(RegionSyncModule regionContext)
    {
        return base.ConvertIn(regionContext);
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        if (base.HandleIn(regionContext))
        {
            ColliderArgs CollidingMessage = new ColliderArgs();
            CollidingMessage.Colliders = Colliders;

            m_log.DebugFormat("ScriptColliding received for {0}", CollideeID);
            regionContext.RememberLocallyGeneratedEvent(MType, CollideeID, CollidingMessage);
            regionContext.Scene.EventManager.TriggerScriptColliding(CollideeID, CollidingMessage);
            regionContext.ForgetLocallyGeneratedEvent();
        }
        return true;
    }
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        return base.ConvertOut(regionContext);
    }
}
// ====================================================================================================
public class SyncMsgScriptCollidingEnd : SyncMsgEventCollision
{
    public override string DetailLogTagRcv { get { return "RcvCollEnd"; } }
    public override string DetailLogTagSnd { get { return "SndCollEnd"; } }

    public SyncMsgScriptCollidingEnd(UUID pCollidee, uint pCollideeID, List<DetectedObject> pColliders)
        : base(pCollidee, pCollideeID, pColliders)
    {
    }
    public SyncMsgScriptCollidingEnd(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool ConvertIn(RegionSyncModule regionContext)
    {
        return base.ConvertIn(regionContext);
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        if (base.HandleIn(regionContext))
        {
            ColliderArgs CollidingMessage = new ColliderArgs();
            CollidingMessage.Colliders = Colliders;

            m_log.DebugFormat("ScriptCollidingEnd received for {0}", CollideeID);
            regionContext.RememberLocallyGeneratedEvent(MType, CollideeID, CollidingMessage);
            regionContext.Scene.EventManager.TriggerScriptCollidingEnd(CollideeID, CollidingMessage);
            regionContext.ForgetLocallyGeneratedEvent();
        }
        return true;
    }
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        return base.ConvertOut(regionContext);
    }
}
// ====================================================================================================
public class SyncMsgScriptLandCollidingStart : SyncMsgEventCollision
{
    public override string DetailLogTagRcv { get { return "RcvLColSrt"; } }
    public override string DetailLogTagSnd { get { return "SndLColSrt"; } }

    public SyncMsgScriptLandCollidingStart(UUID pCollidee, uint pCollideeID, List<DetectedObject> pColliders)
        : base(pCollidee, pCollideeID, pColliders)
    {
    }
    public SyncMsgScriptLandCollidingStart(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool ConvertIn(RegionSyncModule regionContext)
    {
        return base.ConvertIn(regionContext);
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        if (base.HandleIn(regionContext))
        {
            ColliderArgs CollidingMessage = new ColliderArgs();
            CollidingMessage.Colliders = Colliders;

            m_log.DebugFormat("ScriptLandCollidingStart received for {0}", CollideeID);
            regionContext.RememberLocallyGeneratedEvent(MType, CollideeID, CollidingMessage);
            regionContext.Scene.EventManager.TriggerScriptLandCollidingStart(CollideeID, CollidingMessage);
            regionContext.ForgetLocallyGeneratedEvent();
        }
        return true;
    }
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        return base.ConvertOut(regionContext);
    }
}
// ====================================================================================================
public class SyncMsgScriptLandColliding : SyncMsgEventCollision
{
    public override string DetailLogTagRcv { get { return "RcvLColing"; } }
    public override string DetailLogTagSnd { get { return "SndLColing"; } }

    public SyncMsgScriptLandColliding(UUID pCollidee, uint pCollideeID, List<DetectedObject> pColliders)
        : base(pCollidee, pCollideeID, pColliders)
    {
    }
    public SyncMsgScriptLandColliding(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool ConvertIn(RegionSyncModule regionContext)
    {
        return base.ConvertIn(regionContext);
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        if (base.HandleIn(regionContext))
        {
            ColliderArgs CollidingMessage = new ColliderArgs();
            CollidingMessage.Colliders = Colliders;

            m_log.DebugFormat("ScriptLandColliding received for {0}", CollideeID);
            regionContext.RememberLocallyGeneratedEvent(MType, CollideeID, CollidingMessage);
            regionContext.Scene.EventManager.TriggerScriptLandColliding(CollideeID, CollidingMessage);
            regionContext.ForgetLocallyGeneratedEvent();
        }
        return true;
    }
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        return base.ConvertOut(regionContext);
    }
}
// ====================================================================================================
public class SyncMsgScriptLandCollidingEnd : SyncMsgEventCollision
{
    public override string DetailLogTagRcv { get { return "RcvLColEnd"; } }
    public override string DetailLogTagSnd { get { return "SndLColEnd"; } }

    public SyncMsgScriptLandCollidingEnd(UUID pCollidee, uint pCollideeID, List<DetectedObject> pColliders)
        : base(pCollidee, pCollideeID, pColliders)
    {
    }
    public SyncMsgScriptLandCollidingEnd(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool ConvertIn(RegionSyncModule regionContext)
    {
        return base.ConvertIn(regionContext);
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        if (base.HandleIn(regionContext))
        {
            ColliderArgs CollidingMessage = new ColliderArgs();
            CollidingMessage.Colliders = Colliders;

            m_log.DebugFormat("ScriptLandCollidingEnd received for {0}", CollideeID);
            regionContext.RememberLocallyGeneratedEvent(MType, CollideeID, CollidingMessage);
            regionContext.Scene.EventManager.TriggerScriptLandCollidingEnd(CollideeID, CollidingMessage);
            regionContext.ForgetLocallyGeneratedEvent();
        }
        return true;
    }
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        return base.ConvertOut(regionContext);
    }
}
}
