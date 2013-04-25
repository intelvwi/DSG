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
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Text;
using log4net;
using OpenSim.Framework.Monitoring;
using OpenMetaverse;

namespace DSG.RegionSync
{
    public enum SyncConnectorState
    {
        Idle, //not connected
        Initialization, //initializing local copy of Scene
        Syncing, //done initialization, in normal process of syncing terrain, objects, etc
    }
    // For implementations, a lot was copied from RegionSyncClientView, especially the SendLoop/ReceiveLoop.
    public class SyncConnector
    {
        private TcpClient m_tcpConnection = null;
        private RegionSyncListenerInfo m_remoteListenerInfo = null;
        private Thread m_rcvLoop;
        private Thread m_send_loop;

        private string LogHeader = "[SYNC CONNECTOR]";
        // The logfile
        private ILog m_log;

        private string m_zeroUUID = "00000000-0000-0000-0000-000000000000";

        //members for in/out messages queueing
        object stats = new object();
        private SyncConnectorStat msgsIn;
        private SyncConnectorStat msgsOut;
        private SyncConnectorStat bytesIn;
        private SyncConnectorStat bytesOut;
        private SyncConnectorStat currentQueue;
        private DateTime lastStatTime;

        // A queue for outgoing traffic. 
        private BlockingUpdateQueue m_outQ = new BlockingUpdateQueue();
        
        private RegionSyncModule m_regionSyncModule = null;

        //members for keeping track of state of this connector
        private SyncConnectorState m_syncState = SyncConnectorState.Idle;

        // unique connector number across all regions
        private int m_connectorNum = 0;
        public int ConnectorNum
        {
            get { return m_connectorNum; }
        }

        //the actorID of the other end of the connection
        public string otherSideActorID { get; set; }

        //The region name of the other side of the connection
        public string otherSideRegionName { get; set; }

        // Check if the client is connected
        public bool connected
        { 
            get 
            { 
                return (m_tcpConnection != null && m_tcpConnection.Connected); 
            } 
        }

        public string description
        {
            get
            {
                if (otherSideRegionName == null)
                    return String.Format("SyncConnector{0}", m_connectorNum);

                return String.Format("SyncConnector{0}({2}/{1:10})",
                            m_connectorNum, otherSideRegionName, otherSideActorID);
            }
        }

        /// <summary>
        /// The constructor that will be called when a SyncConnector is created passively: a remote SyncConnector has initiated the connection.
        /// </summary>
        /// <param name="connectorNum"></param>
        /// <param name="tcpclient"></param>
        public SyncConnector(int connectorNum, TcpClient tcpclient, RegionSyncModule syncModule)
        {
            m_tcpConnection = tcpclient;
            m_connectorNum = connectorNum;
            m_regionSyncModule = syncModule;
            lastStatTime = DateTime.Now;
            m_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        }

        /// <summary>
        /// The constructor that will be called when a SyncConnector is created actively: it is created to send connection request to a remote listener
        /// </summary>
        /// <param name="connectorNum"></param>
        /// <param name="listenerInfo"></param>
        public SyncConnector(int connectorNum, RegionSyncListenerInfo listenerInfo, RegionSyncModule syncModule)
        {
            m_remoteListenerInfo = listenerInfo;
            m_connectorNum = connectorNum;
            m_regionSyncModule = syncModule;
            lastStatTime = DateTime.Now;
            m_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        }

        //Connect to the remote listener
        public bool Connect()
        {
            m_tcpConnection = new TcpClient();
            try
            {
                m_tcpConnection.Connect(m_remoteListenerInfo.Addr, m_remoteListenerInfo.Port);
            }
            catch (Exception e)
            {
                m_log.WarnFormat("{0} [Start] Could not connect to RegionSyncModule at {1}:{2}", LogHeader, m_remoteListenerInfo.Addr, m_remoteListenerInfo.Port);
                m_log.Warn(e.Message);
                return false;
            }
            return true;
        }

        /// <summary>
        /// Start both the send and receive threads
        /// </summary>
        public void StartCommThreads()
        {
            // Create a thread for the receive loop
            m_rcvLoop = new Thread(new ThreadStart(ReceiveLoop));
            m_rcvLoop.Name = description + " (ReceiveLoop)";
            m_log.WarnFormat("{0} Starting {1} thread", description, m_rcvLoop.Name);
            m_rcvLoop.Start();

            // Create a thread for the send loop
            m_send_loop = new Thread(new ThreadStart(delegate() { SendLoop(); }));
            m_send_loop.Name = description + " (SendLoop)";
            m_log.WarnFormat("{0} Starting {1} thread", description, m_send_loop.Name);
            m_send_loop.Start();
        }

        public void Shutdown()
        {
            m_log.Warn(LogHeader + " shutdown connection");
            // Abort receive and send loop
            m_rcvLoop.Abort();
            m_send_loop.Abort();

            // Close the connection
            m_tcpConnection.Client.Close();
            m_tcpConnection.Close();
        }

        ///////////////////////////////////////////////////////////
        // Sending messages out to the other side of the connection
        ///////////////////////////////////////////////////////////
        // Send messages from the update Q as fast as we can DeQueue them
        // *** This is the main send loop thread for each connected client
        private void SendLoop()
        {
            try
            {
                while (true)
                {
                    SyncMsg msg = m_outQ.Dequeue();
                    if (m_collectingStats) currentQueue.Event(-1);
                    Send(msg);
                }
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("{0} has disconnected: {1} (SendLoop)", description, e);
            }
            Shutdown();
        }

        /// <summary>
        /// Enqueue update of an object/avatar into the outgoing queue, and return right away
        /// </summary>
        /// <param name="id">UUID of the object/avatar</param>
        /// <param name="update">the update infomation in byte format</param>
        public void EnqueueOutgoingUpdate(UUID id, SyncMsg update)
        {
            // m_log.DebugFormat("{0} Enqueue msg {1}", LogHeader, update.ToString());
            // Enqueue is thread safe
            update.LogTransmission(this);
            if (m_outQ.Enqueue(id, update) && m_collectingStats)
                currentQueue.Event(1);
        }

        // Queue the outgoing message so it it sent "now" and before update messages.
        public void ImmediateOutgoingMsg(SyncMsg msg)
        {
            msg.LogTransmission(this);

            // The new way is to add a first queue and to place this message at the front.
            m_outQ.QueueMessageFirst(msg);

            if (m_collectingStats)
                currentQueue.Event(1);
        }

        //Send out a messge directly. This should only by called for short messages that are not sent frequently.
        //Don't call this function for sending out updates. Call EnqueueOutgoingUpdate instead
        private void Send(SyncMsg msg)
        {
            // m_log.DebugFormat("{0} Send msg: {1}: {2}", LogHeader, this.Description, msg.ToString());
            byte[] data = msg.GetWireBytes();
            if (m_tcpConnection.Connected)
            {
                try
                {
                    CollectSendStat(msg.MType.ToString(), msg.DataLength);
                    m_tcpConnection.GetStream().BeginWrite(data, 0, data.Length, ar =>
                    {
                        if (m_tcpConnection.Connected)
                        {
                            try
                            {
                                m_tcpConnection.GetStream().EndWrite(ar);
                            }
                            catch (Exception)
                            { }
                        }
                    }, null);
                }
                catch (Exception e)
                {
                    m_log.ErrorFormat("{0}:Error in Send() {1} has disconnected: {2}.", LogHeader, m_connectorNum, e);
                }
            }
        }

        ///////////////////////////////////////////////////////////
        // Receiving messages from the other side ofthe connection
        ///////////////////////////////////////////////////////////
        private void ReceiveLoop()
        {
            m_log.WarnFormat("{0} Thread running: {1}", LogHeader, m_rcvLoop.Name);
            while (true && m_tcpConnection.Connected)
            {
                SyncMsg msg;
                // Try to get the message from the network stream
                try
                {
                    msg = SyncMsg.SyncMsgFactory(m_tcpConnection.GetStream(), this);
                    // m_log.WarnFormat("{0} Recv msg: {1}", LogHeader, msg.ToString());
                }
                // If there is a problem reading from the client, shut 'er down. 
                catch (Exception e)
                {
                    //ShutdownClient();
                    m_log.ErrorFormat("{0}: ReceiveLoop error. Connector {1} disconnected: {2}.", LogHeader, m_connectorNum, e);
                    Shutdown();
                    return;
                }
                // Try handling the message
                try
                {
                    HandleMessage(msg);
                }
                catch (Exception e)
                {
                    if (msg == null)
                    {
                        m_log.ErrorFormat("{0} Exception handling msg: NULL MESSAGE: {1}", LogHeader, e);
                    }
                    else
                    {
                        m_log.ErrorFormat("{0} Exception handling msg: type={1},len={2}: {3}", LogHeader, msg.MType, msg.DataLength, e);
                    }
                }
            }
        }

        private void HandleMessage(SyncMsg msg)
        {

            // m_log.DebugFormat("{0} Recv msg: {1}: {2}", LogHeader, this.Description, msg.ToString());
            CollectReceiveStat(msg.MType.ToString(), msg.DataLength);

            // TODO: Consider doing the data unpacking on a different thread than the input reader thread
            msg.ConvertIn(m_regionSyncModule);

            // TODO: Consider doing the message processing on a different thread than the input reader thread
            msg.HandleIn(m_regionSyncModule);

            // If this is an initialization message, print out info and start stats gathering if initialized enough
            if (msg.MType == SyncMsg.MsgType.RegionName || msg.MType == SyncMsg.MsgType.ActorID)
            {
                switch (msg.MType)
                {
                    case SyncMsg.MsgType.RegionName:
                        m_log.DebugFormat("Syncing to region \"{0}\"", otherSideRegionName);
                        break;
                    case SyncMsg.MsgType.ActorID:
                        m_log.DebugFormat("Syncing to actor \"{0}\"", otherSideActorID);
                        break;
                }
                if (otherSideRegionName != null && otherSideActorID != null)
                    StartCollectingStats();
            }
        }

        private bool m_collectingStats = false;
        private SortedDictionary<string, SyncConnectorStat> m_packetTypesSent = new SortedDictionary<string,SyncConnectorStat>();
        private SortedDictionary<string, SyncConnectorStat> m_packetTypesRcvd = new SortedDictionary<string,SyncConnectorStat>();
        private List<Stat> m_registeredStats = new List<Stat>();

        private void StartCollectingStats()
        {
            // If stats not enabled or stats have already been initialized, just return.
            if (!m_regionSyncModule.StatCollector.Enabled || m_registeredStats.Count > 0)
                return;

            msgsIn = new SyncConnectorStat(
                "DSG_Msgs_Rcvd|" + description, // shortName
                "Msgs_Rcvd",                    // name
                "connector DSG messages rcvd",  // description
                " messages",                    // unit
                m_regionSyncModule.Scene.Name,  // region
                m_connectorNum,                 // connectornum
                m_regionSyncModule.ActorID,     // myActorID
                otherSideRegionName,            // otherSideRegionName
                otherSideActorID                // otherSideActorID
                );
            // msgsIn.AddHistogram("Msgs_Rcvd_Last_Minute", new EventHistogram(60, 1000)); // last minute in seconds
            // msgsIn.AddHistogram("Msgs_Rcvd_Last_Hour", new EventHistogram(60, 60000));  // last hour in minutes
            // msgsIn.AddHistogram("Msgs_Rcvd_Last_Day", new EventHistogram(24, 3600000)); // last day in hours
            StatsManager.RegisterStat(msgsIn);
            m_registeredStats.Add(msgsIn);

            msgsOut = new SyncConnectorStat(
                "DSG_Msgs_Sent|" + description, // shortName
                "Msgs_Sent",                    // name
                "connector DSG messages sent",  // description
                " messages",                    // unit
                m_regionSyncModule.Scene.Name,  // region
                m_connectorNum,                 // connectornum
                m_regionSyncModule.ActorID,     // myActorID
                otherSideRegionName,            // otherSideRegionName
                otherSideActorID                // otherSideActorID
                );
            // msgsOut.AddHistogram("Msgs_Sent_Last_Minute", new EventHistogram(60, 1000)); // last minute in seconds
            // msgsOut.AddHistogram("Msgs_Sent_Last_Hour", new EventHistogram(60, 60000));  // last hour in minutes
            // msgsOut.AddHistogram("Msgs_Sent_Last_Day", new EventHistogram(24, 3600000)); // last day in hours
            StatsManager.RegisterStat(msgsOut);
            m_registeredStats.Add(msgsOut);

            bytesIn = new SyncConnectorStat(
                "DSG_Bytes_Rcvd|" + description,    // shortName
                "Bytes_Rcvd",                   // name
                "connector DSG bytes rcvd",     // description
                " bytes",                       // unit
                m_regionSyncModule.Scene.Name,  // region
                m_connectorNum,                 // connectornum
                m_regionSyncModule.ActorID,     // myActorID
                otherSideRegionName,            // otherSideRegionName
                otherSideActorID                // otherSideActorID
                );
            // bytesIn.AddHistogram("Bytes_Rcvd_Last_Hour", new EventHistogram(60, 60000));  // last hour in minutes
            StatsManager.RegisterStat(bytesIn);
            m_registeredStats.Add(bytesIn);

            bytesOut = new SyncConnectorStat(
                "DSG_Bytes_Sent|" + description,    // shortName
                "Bytes_Sent",                   // name
                "connector DSG bytes sent",     // description
                " bytes",                       // unit
                m_regionSyncModule.Scene.Name,  // region
                m_connectorNum,                 // connectornum
                m_regionSyncModule.ActorID,     // myActorID
                otherSideRegionName,            // otherSideRegionName
                otherSideActorID                // otherSideActorID
                );
            // bytesOut.AddHistogram("Bytes_Sent_Last_Hour", new EventHistogram(60, 60000));  // last hour in minutes
            StatsManager.RegisterStat(bytesOut);
            m_registeredStats.Add(bytesOut);

            currentQueue = new SyncConnectorStat(
                "DSG_Queued_Msgs|" + description,   // shortName
                "Queued_Msgs",                  // name
                "connector DSG queued updates", // description
                " messages",                    // unit
                m_regionSyncModule.Scene.Name,  // region
                m_connectorNum,                 // connectornum
                m_regionSyncModule.ActorID,     // myActorID
                otherSideRegionName,            // otherSideRegionName
                otherSideActorID                // otherSideActorID
                );
            // currentQueue.AddHistogram("Queue_Size_Last_5Minute", new EventHistogram(300, 1000)); // last 5 minutes in seconds
            StatsManager.RegisterStat(currentQueue);
            m_registeredStats.Add(currentQueue);

            m_collectingStats = true;
        }

        private void CollectSendStat(string type, int length)
        {
            if (m_collectingStats)
            {
                lock (stats)
                {
                    msgsOut.Event();
                    bytesOut.Event(length);
                    SyncConnectorStat msgStat;
                    if (!m_packetTypesSent.TryGetValue(type, out msgStat))
                    {
                        msgStat = new SyncConnectorStat(
                            "DSG_Msgs_Typ_Sent|" + description + "|" + type,    // shortName
                            type + "_Sent",                 // name
                            "connector DSG messages sent of type " + type,      // description
                            " messages",                    // unit
                            m_regionSyncModule.Scene.Name,  // region
                            m_connectorNum,                 // connectornum
                            m_regionSyncModule.ActorID,     // myActorID
                            otherSideRegionName,            // otherSideRegionName
                            otherSideActorID,               // otherSideActorID
                            type                            // messageType
                            );
                        StatsManager.RegisterStat(msgStat);
                        m_packetTypesSent.Add(type, msgStat);
                    }
                    msgStat.Event();
                }
            }
        }

        private void CollectReceiveStat(string type, int length)
        {
            lock (stats)
            {
                if (m_collectingStats)
                {
                    msgsIn.Event();
                    bytesIn.Event(length);

                    SyncConnectorStat msgStat;
                    if (!m_packetTypesRcvd.TryGetValue(type, out msgStat))
                    {
                        msgStat = new SyncConnectorStat(
                            "DSG_Msgs_Typ_Rcvd|" + description + "|" + type,    // shortName
                            type + "_Rcvd",                 // name
                            "connector DSG messages of type " + type,   // description
                            " messages",                    // unit
                            m_regionSyncModule.Scene.Name,  // region
                            m_connectorNum,                 // connectornum
                            m_regionSyncModule.ActorID,     // myActorID
                            otherSideRegionName,            // otherSideRegionName
                            otherSideActorID,               // otherSideActorID
                            type                            // messageType
                            );
                        StatsManager.RegisterStat(msgStat);
                        m_packetTypesRcvd.Add(type, msgStat);
                    }
                    msgStat.Event();
                }
            }
        }

        private void DeregisterConnectorStats()
        {
            foreach (Stat s in m_registeredStats)
            {
                StatsManager.DeregisterStat(s);
            }
        }
    }
}
