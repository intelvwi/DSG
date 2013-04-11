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
    /// </summary>
    protected static ILog m_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
    protected readonly string LogHeader = "[SYNCMSG]";
    protected readonly string ZeroUUID = "00000000-0000-0000-0000-000000000000";

    // The connector this message was received on.
    public SyncConnector ConnectorContext { get; set; }

    // They type of this message
    public MsgType MType { get; protected set; }

    // The binary data this type of message is built from or converted into
    public int DataLength { get; protected set; }
    protected byte[] m_data;

    // The binary, wire encoding of the object. Includes the type and length header.
    private byte[] m_rawOutBytes;

    // Given an incoming stream of bytes, create a SyncMsg from the next data on that stream.
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

            // TODO: could there be a common SyncMsgCollision() ?
            case MsgType.PhysicsCollision:      ret = new SyncMsgPhysicsCollision(mType, length, data);     break;
            case MsgType.ScriptCollidingStart:  ret = new SyncMsgScriptCollidingStart(mType, length, data); break;
            case MsgType.ScriptColliding:       ret = new SyncMsgScriptColliding(mType, length, data);      break;
            case MsgType.ScriptCollidingEnd:    ret = new SyncMsgScriptCollidingEnd(mType, length, data);   break;
            case MsgType.ScriptLandCollidingStart:ret = new SyncMsgScriptLandCollidingStart(mType, length, data);   break;
            case MsgType.ScriptLandColliding:   ret = new SyncMsgScriptLandColliding(mType, length, data);  break;
            case MsgType.ScriptLandCollidingEnd:ret = new SyncMsgScriptLandCollidingEnd(mType, length, data);   break;

                ret = new SyncMsgEvent(mType, length, data);
                break;

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
    }
    public SyncMsg(MsgType pType, int pLength, byte[] pData)
    {
        MType = pType;
        DataLength = pLength;
        m_data = pData;
        m_rawOutBytes = null;
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

    // Put bytes on stream.
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
        m_rawOutBytes = null;
        return true;
    }
    // Handle the received message. ConvertIn() has been called before this.
    public virtual bool HandleIn(RegionSyncModule regionContext)
    {
        return true;
    }
    // Called before message is sent to convert the internal representation into binary stream data.
    // A separate call so parsing can happen on a different thread from the output writer.
    // Return 'true' if successful handling.
    public virtual bool ConvertOut(RegionSyncModule regionContext)
    {
        m_rawOutBytes = null;
        return true;
    }
}

// ====================================================================================================
// A base class for sync messages whose underlying structure is just an OSDMap
public abstract class SyncMsgGeneric : SyncMsg
{
    protected OSDMap DataMap { get; set; }

    public SyncMsgGeneric()
        : base()
    {
        DataMap = null;
    }
    public SyncMsgGeneric(MsgType pType, int pLength, byte[] pData)
        : base(pType, pLength, pData)
    {
        DataMap = null;
    }

    public override bool ConvertIn(RegionSyncModule regionContext)
    {
        DataMap = DeserializeMessage();
        return (DataMap != null);
    }
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        base.ConvertOut(regionContext);
        string s = OSDParser.SerializeJsonString(DataMap, true);
        m_data = System.Text.Encoding.ASCII.GetBytes(s);
        DataLength = m_data.Length;
        return true;
    }

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
                    m_log.Error(LogHeader + " " + Encoding.ASCII.GetString(msg.Data, 0, msg.Length));
                    // after all of that, print out the actual error
                    m_log.ErrorFormat("{0}: {1}", LogHeader, e);
                }
            }
            data = null;
        }
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
}

// ====================================================================================================
public class SyncMsgUpdatedProperties : SyncMsgGeneric
{
    public SyncMsgUpdatedProperties(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        // Decode synced properties from the message
        HashSet<SyncedProperty> syncedProperties = SyncedProperty.DecodeProperties(DataMap);
        if (syncedProperties == null)
        {
            m_log.ErrorFormat("{0} HandleUpdatedProperties could not get syncedProperties", LogHeader);
            return false;
        }

        UUID uuid = DataMap["uuid"].AsUUID();
        if (uuid == null)
        {
            m_log.ErrorFormat("{0} HandleUpdatedProperties could not get UUID!", LogHeader);
            return false;
        }

        if (syncedProperties.Count > 0)
        {
            // Update local sync info and scene object/presence
            regionContext.RememberLocallyGeneratedEvent(MType);
            HashSet<SyncableProperties.Type> propertiesUpdated = regionContext.InfoManager.UpdateSyncInfoBySync(uuid, syncedProperties);
            regionContext.ForgetLocallyGeneratedEvent();

            regionContext.DetailedUpdateLogging(uuid, propertiesUpdated, syncedProperties, "RecUpdateN", ConnectorContext.otherSideActorID, DataLength);

            // Relay the update properties
            if (regionContext.IsSyncRelay)
                regionContext.EnqueueUpdatedProperty(uuid, propertiesUpdated);    
        }
        return false;
    }
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        return false;
    }
}
// ====================================================================================================
// Send to have other side send us their region info.
// If received, send our region info.
public class SyncMsgGetRegionInfo : SyncMsgGeneric
{
    public SyncMsgGetRegionInfo()
        : base()
    {
    }
    public SyncMsgGetRegionInfo(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        SyncMsgRegionInfo msg = new SyncMsgRegionInfo(regionContext.Scene.RegionInfo);
        ConnectorContext.ImmediateOutgoingMsg(msg);
        return false;
    }
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        return true;
    }
}
// ====================================================================================================
// Sent to tell the other end our region info.
// When received, it is the other side's region info.
public class SyncMsgRegionInfo : SyncMsgGeneric
{
    RegionInfo m_regionInfo;
    public SyncMsgRegionInfo(RegionInfo pRegionInfo)
        : base()
    {
        m_regionInfo = pRegionInfo;
    }
    public SyncMsgRegionInfo(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        m_regionInfo = regionContext.Scene.RegionInfo;
        m_regionInfo.RegionSettings.TerrainTexture1 = DataMap["tex1"].AsUUID();
        m_regionInfo.RegionSettings.TerrainTexture2 = DataMap["tex2"].AsUUID();
        m_regionInfo.RegionSettings.TerrainTexture3 = DataMap["tex3"].AsUUID();
        m_regionInfo.RegionSettings.TerrainTexture4 = DataMap["tex4"].AsUUID();
        m_regionInfo.RegionSettings.WaterHeight = DataMap["waterheight"].AsReal();
        IEstateModule estate = regionContext.Scene.RequestModuleInterface<IEstateModule>();
        if (estate != null)
            estate.sendRegionHandshakeToAll();
        return true;
    }
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        OSDMap data = new OSDMap(5);
        data["tex1"] = OSD.FromUUID(m_regionInfo.RegionSettings.TerrainTexture1);
        data["tex2"] = OSD.FromUUID(m_regionInfo.RegionSettings.TerrainTexture2);
        data["tex3"] = OSD.FromUUID(m_regionInfo.RegionSettings.TerrainTexture3);
        data["tex4"] = OSD.FromUUID(m_regionInfo.RegionSettings.TerrainTexture4);
        data["waterheight"] = OSD.FromReal(m_regionInfo.RegionSettings.WaterHeight);
        DataMap = data;
        base.ConvertOut(regionContext);

        return true;
    }
}
// ====================================================================================================
public class SyncMsgTimeStamp : SyncMsgGeneric
{
    private long m_tickTime;
    public SyncMsgTimeStamp(long pTickTime)
        : base()
    {
        m_tickTime = pTickTime;
    }
    public SyncMsgTimeStamp(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        return false;
    }
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        return false;
    }
}
// ====================================================================================================
public class SyncMsgGetTerrain : SyncMsgGeneric
{
    public SyncMsgGetTerrain()
        : base()
    {
    }
    public SyncMsgGetTerrain(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        return false;
    }
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        return false;
    }
}
// ====================================================================================================
public class SyncMsgTerrain : SyncMsgGeneric
{
    public SyncMsgTerrain(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        return false;
    }
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        return false;
    }
}
// ====================================================================================================
public class SyncMsgGetObjects : SyncMsgGeneric
{
    public SyncMsgGetObjects()
        : base()
    {
    }
    public SyncMsgGetObjects(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        return false;
    }
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        return false;
    }
}
// ====================================================================================================
public class SyncMsgGetPresences : SyncMsgGeneric
{
    public SyncMsgGetPresences()
        : base()
    {
    }
    public SyncMsgGetPresences(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        return false;
    }
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        return false;
    }
}
// ====================================================================================================
public class SyncMsgNewObject : SyncMsgGeneric
{
    public SyncMsgNewObject(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        return false;
    }
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        return false;
    }
}
// ====================================================================================================
public class SyncMsgRemovedObject : SyncMsgGeneric
{
    public SyncMsgRemovedObject(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        return false;
    }
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        return false;
    }
}
// ====================================================================================================
public class SyncMsgLinkObject : SyncMsgGeneric
{
    public SyncMsgLinkObject(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        OSDMap encodedSOG = (OSDMap)DataMap["linkedGroup"];
        SceneObjectGroup linkedGroup;
        Dictionary<UUID, SyncInfoBase> groupSyncInfos;
        if (!DecodeSceneObject(encodedSOG, out linkedGroup, out groupSyncInfos, regionContext.Scene))
        {
            m_log.WarnFormat("{0}: Failed to decode scene object in HandleSyncLinkObject", LogHeader);
            return false; ;
        }
        regionContext.DetailedUpdateWrite("RecLnkObjj", linkedGroup.UUID, 0, ZeroUUID, ConnectorContext.otherSideActorID, DataLength);

        //TEMP DEBUG
        // m_log.DebugFormat(" received linkedGroup: {0}", linkedGroup.DebugObjectUpdateResult());
        //m_log.DebugFormat(linkedGroup.DebugObjectUpdateResult());

        if (linkedGroup == null)
        {
            m_log.ErrorFormat("{0}: HandleSyncLinkObject, no valid Linked-Group has been deserialized", LogHeader);
            return false;
        }

        UUID rootID = DataMap["rootID"].AsUUID();
        int partCount = DataMap["partCount"].AsInteger();
        List<UUID> childrenIDs = new List<UUID>();

        for (int i = 0; i < partCount; i++)
        {
            string partTempID = "part" + i;
            childrenIDs.Add(DataMap[partTempID].AsUUID());
        }

        // if this is a relay node, forward the message
        if (regionContext.IsSyncRelay)
        {
            //SendSceneEventToRelevantSyncConnectors(senderActorID, msg, linkedGroup);
            regionContext.SendSpecialUpdateToRelevantSyncConnectors(ConnectorContext.otherSideActorID, "SndLnkObjR", rootID, this);
        }

        //TEMP SYNC DEBUG
        //m_log.DebugFormat("{0}: received LinkObject from {1}", LogHeader, senderActorID);

        //DSL Scene.LinkObjectBySync(linkedGroup, rootID, childrenIDs);

        //Update properties, if any has changed
        foreach (KeyValuePair<UUID, SyncInfoBase> partSyncInfo in groupSyncInfos)
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
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        return false;
    }
}
// ====================================================================================================
public class SyncMsgDelinkObject : SyncMsgGeneric
{
    public SyncMsgDelinkObject(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        //List<SceneObjectPart> localPrims = new List<SceneObjectPart>();
        List<UUID> delinkPrimIDs = new List<UUID>();
        List<UUID> beforeDelinkGroupIDs = new List<UUID>();
        List<SceneObjectGroup> incomingAfterDelinkGroups = new List<SceneObjectGroup>();
        List<Dictionary<UUID, SyncInfoBase>> incomingPrimSyncInfo = new List<Dictionary<UUID, SyncInfoBase>>();

        int partCount = DataMap["partCount"].AsInteger();
        for (int i = 0; i < partCount; i++)
        {
            string partTempID = "part" + i;
            UUID primID = DataMap[partTempID].AsUUID();
            //SceneObjectPart localPart = Scene.GetSceneObjectPart(primID);
            //localPrims.Add(localPart);
            delinkPrimIDs.Add(primID);
        }

        int beforeGroupCount = DataMap["beforeGroupsCount"].AsInteger();
        for (int i = 0; i < beforeGroupCount; i++)
        {
            string groupTempID = "beforeGroup" + i;
            UUID beforeGroupID = DataMap[groupTempID].AsUUID();
            beforeDelinkGroupIDs.Add(beforeGroupID);
        }

        int afterGroupsCount = DataMap["afterGroupsCount"].AsInteger();
        for (int i = 0; i < afterGroupsCount; i++)
        {
            string groupTempID = "afterGroup" + i;
            //string sogxml = data[groupTempID].AsString();
            SceneObjectGroup afterGroup;
            OSDMap encodedSOG = (OSDMap)DataMap[groupTempID];
            Dictionary<UUID, SyncInfoBase> groupSyncInfo;
            if(!DecodeSceneObject(encodedSOG, out afterGroup, out groupSyncInfo, regionContext.Scene))
            {
                m_log.WarnFormat("{0}: Failed to decode scene object in HandleSyncDelinkObject", LogHeader);
                return false;
            }

            incomingAfterDelinkGroups.Add(afterGroup);
            incomingPrimSyncInfo.Add(groupSyncInfo);
        }

        // if this is a relay node, forward the message
        if (regionContext.IsSyncRelay)
        {
            List<SceneObjectGroup> beforeDelinkGroups = new List<SceneObjectGroup>();
            foreach (UUID sogID in beforeDelinkGroupIDs)
            {
                SceneObjectGroup sog = regionContext.Scene.GetGroupByPrim(sogID);
                beforeDelinkGroups.Add(sog);
            }
            regionContext.SendDelinkObjectToRelevantSyncConnectors(ConnectorContext.otherSideActorID, beforeDelinkGroups, this);
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
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        return false;
    }
}
// ====================================================================================================
public class SyncMsgNewPresence : SyncMsgGeneric
{
    public SyncMsgNewPresence(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        // Decode presence and syncInfo from message data
        SyncInfoBase syncInfo;
        DecodeScenePresence(DataMap, out syncInfo, regionContext.Scene);
        regionContext.DetailedUpdateWrite("RecNewPres", syncInfo.UUID, 0, ZeroUUID, ConnectorContext.otherSideActorID, DataLength);

        // if this is a relay node, forward the message
        if (regionContext.IsSyncRelay)
            regionContext.SendSpecialUpdateToRelevantSyncConnectors(ConnectorContext.otherSideActorID, "SndNewPreR", syncInfo.UUID, this);

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
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        return false;
    }
}
// ====================================================================================================
public class SyncMsgRemovedPresence : SyncMsgGeneric
{
    public SyncMsgRemovedPresence(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        UUID uuid = DataMap["uuid"].AsUUID();

        regionContext.DetailedUpdateWrite("RecRemPres", uuid.ToString(), 0, ZeroUUID, ConnectorContext.otherSideActorID, DataLength);

        if (!regionContext.InfoManager.SyncInfoExists(uuid))
            return false;

        // if this is a relay node, forward the message
        if (regionContext.IsSyncRelay)
        {
            regionContext.SendSpecialUpdateToRelevantSyncConnectors(ConnectorContext.otherSideActorID, "SndRemPreR", uuid, this);
        }
        
        // This limits synced avatars to real clients (no npcs) until we sync PresenceType field
        regionContext.Scene.RemoveClient(uuid, false);
    }
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        return false;
    }
}
// ====================================================================================================
public class SyncMsgRegionName : SyncMsgGeneric
{
    private string m_regionName;
    public SyncMsgRegionName(string pRegionName)
        : base()
    {
        m_regionName = pRegionName;
    }
    public SyncMsgRegionName(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        return false;
    }
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        return false;
    }
}
// ====================================================================================================
public class SyncMsgActorID : SyncMsgGeneric
{
    private string m_actorID;
    public SyncMsgActorID(string pActorID)
        : base()
    {
        m_actorID = pActorID;
    }
    public SyncMsgActorID(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        return false;
    }
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        return false;
    }
}
// ====================================================================================================
public class SyncMsgRegionStatus : SyncMsgGeneric
{
    public SyncMsgRegionStatus(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        return false;
    }
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        return false;
    }
}
// ====================================================================================================
public class SyncMsgEvent : SyncMsgGeneric
{
    public SyncMsgEvent(MsgType pMsgType, int pLength, byte[] pData)
        : base(pMsgType, pLength, pData)
    {
    }
    public override bool HandleIn(RegionSyncModule regionContext)
    {
        //string init_actorID = data["actorID"].AsString();
        string init_syncID = DataMap["syncID"].AsString();
        ulong evSeqNum = DataMap["seqNum"].AsULong();

        regionContext.DetailedUpdateWrite("RecEventtt", MType.ToString(), 0, init_syncID, ConnectorContext.otherSideActorID, DataLength);

        //check if this is a duplicate event message that we have received before
        if (m_eventsReceived.IsSEQReceived(init_syncID, evSeqNum))
        {
            m_log.ErrorFormat("Duplicate event {0} originated from {1}, seq# {2} has been received", msg.Type, init_syncID, evSeqNum);
            return false;
        }
        else
        {
            m_eventsReceived.RecordEventReceived(init_syncID, evSeqNum);
        }

        // if this is a relay node, forward the message
        if (regionContext.IsSyncRelay)
        {
            regionContext.SendSceneEventToRelevantSyncConnectors(ConnectorContext.otherSideActorID, this, null);
        }

        switch (MType)
        {
            case SyncMsg.MsgType.NewScript:
                HandleRemoteEvent_OnNewScript(init_syncID, evSeqNum, data);
                break;
            case SyncMsg.MsgType.UpdateScript:
                HandleRemoteEvent_OnUpdateScript(init_syncID, evSeqNum, data);
                break; 
            case SyncMsg.MsgType.ScriptReset:
                HandleRemoteEvent_OnScriptReset(init_syncID, evSeqNum, data);
                break;
            case SyncMsg.MsgType.ChatFromClient:
                HandleRemoteEvent_OnChatFromClient(init_syncID, evSeqNum, data);
                break;
            case SyncMsg.MsgType.ChatFromWorld:
                HandleRemoteEvent_OnChatFromWorld(init_syncID, evSeqNum, data);
                break;
            case SyncMsg.MsgType.ChatBroadcast:
                HandleRemoteEvent_OnChatBroadcast(init_syncID, evSeqNum, data);
                break;
            case SyncMsg.MsgType.ObjectGrab:
                HandleRemoteEvent_OnObjectGrab(init_syncID, evSeqNum, data);
                break;
            case SyncMsg.MsgType.ObjectGrabbing:
                HandleRemoteEvent_OnObjectGrabbing(init_syncID, evSeqNum, data);
                break;
            case SyncMsg.MsgType.ObjectDeGrab:
                HandleRemoteEvent_OnObjectDeGrab(init_syncID, evSeqNum, data);
                break;
            case SyncMsg.MsgType.Attach:
                HandleRemoteEvent_OnAttach(init_syncID, evSeqNum, data);
                break;
            case SyncMsg.MsgType.PhysicsCollision:
                HandleRemoteEvent_PhysicsCollision(init_syncID, evSeqNum, data);
                break;
            case SyncMsg.MsgType.ScriptCollidingStart:
            case SyncMsg.MsgType.ScriptColliding:
            case SyncMsg.MsgType.ScriptCollidingEnd:
            case SyncMsg.MsgType.ScriptLandCollidingStart:
            case SyncMsg.MsgType.ScriptLandColliding:
            case SyncMsg.MsgType.ScriptLandCollidingEnd:
                //HandleRemoteEvent_ScriptCollidingStart(init_actorID, evSeqNum, data, DateTime.UtcNow.Ticks);
                HandleRemoteEvent_ScriptCollidingEvents(msg.Type, init_syncID, evSeqNum, data, DateTime.UtcNow.Ticks);
                break;
        }
    }
    public override bool ConvertOut(RegionSyncModule regionContext)
    {
        return false;
    }
}
}
