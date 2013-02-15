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
    public class SyncConnector : ISyncStatistics
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
        private long msgsIn = 0;
        private long msgsOut = 0;
        private long bytesIn = 0;
        private long bytesOut = 0;
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
        public string otherSideActorID { get; private set; }

        //The region name of the other side of the connection
        public string otherSideRegionName { get; private set; }

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
            SyncStatisticCollector.Register(this);
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
            SyncStatisticCollector.Register(this);
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
                    // Dequeue is thread safe
                    SymmetricSyncMessage msg = m_outQ.Dequeue();
                    Send(msg);
                }
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("{0} has disconnected: {1} (SendLoop)", description, e.Message);
            }
            Shutdown();
        }

        /// <summary>
        /// Enqueue update of an object/avatar into the outgoing queue, and return right away
        /// </summary>
        /// <param name="id">UUID of the object/avatar</param>
        /// <param name="update">the update infomation in byte format</param>
        public void EnqueueOutgoingUpdate(UUID id, SymmetricSyncMessage update)
        {
            // m_log.DebugFormat("{0} Enqueue msg {1}", LogHeader, update.ToString());
            // Enqueue is thread safe
            m_outQ.Enqueue(id, update);
        }

        //Send out a messge directly. This should only by called for short messages that are not sent frequently.
        //Don't call this function for sending out updates. Call EnqueueOutgoingUpdate instead
        public void Send(SymmetricSyncMessage msg)
        {
            // m_log.DebugFormat("{0} Send msg: {1}: {2}", LogHeader, this.Description, msg.ToString());
            byte[] data = msg.ToBytes();
            if (m_tcpConnection.Connected)
            {
                try
                {
                    CollectSendStat(msg.Type.ToString(), msg.Data.Length);
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
                    m_log.WarnFormat("{0}:Error in Send() {1} has disconnected -- error message: {2}.", description, m_connectorNum, e.Message);
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
                SymmetricSyncMessage msg;
                // Try to get the message from the network stream
                try
                {
                    msg = new SymmetricSyncMessage(m_tcpConnection.GetStream());
                    // m_log.WarnFormat("{0} Recv msg: {1}", LogHeader, msg.ToString());
                }
                // If there is a problem reading from the client, shut 'er down. 
                catch (Exception e)
                {
                    //ShutdownClient();
                    m_log.ErrorFormat("{0}: ReceiveLoop error {1} has disconnected -- error message {2}.", description, m_connectorNum, e.Message);
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
                    m_log.WarnFormat("{0} Encountered an exception: {1} {2} {3} (MSGTYPE = {4})", description, e.Message, e.TargetSite, e.ToString(), msg.ToString());
                }
            }
        }

        private void HandleMessage(SymmetricSyncMessage msg)
        {

            // m_log.DebugFormat("{0} Recv msg: {1}: {2}", LogHeader, this.Description, msg.ToString());
            CollectReceiveStat(msg.Type.ToString(), msg.Data.Length);

            switch (msg.Type)
            {
                case SymmetricSyncMessage.MsgType.RegionName:
                    {
                        otherSideRegionName = Encoding.ASCII.GetString(msg.Data, 0, msg.Length);
                        m_regionSyncModule.DetailedUpdateWrite("RcvRegnNam", m_zeroUUID, 0, otherSideRegionName, otherSideActorID, msg.Length);
                        if (m_regionSyncModule.IsSyncRelay)
                        {
                            SymmetricSyncMessage outMsg = new SymmetricSyncMessage(SymmetricSyncMessage.MsgType.RegionName, m_regionSyncModule.Scene.RegionInfo.RegionName);
                            m_regionSyncModule.DetailedUpdateWrite("SndRegnNam", m_zeroUUID, 0, m_regionSyncModule.Scene.RegionInfo.RegionName, this.otherSideActorID, outMsg.Length);
                            Send(outMsg);
                        }
                        m_log.DebugFormat("Syncing to region \"{0}\"", otherSideRegionName);
                        if (otherSideRegionName != null && otherSideActorID != null)
                            StartCollectingStats();
                        return;
                    }
                case SymmetricSyncMessage.MsgType.ActorID:
                    {
                        otherSideActorID = Encoding.ASCII.GetString(msg.Data, 0, msg.Length);
                        m_regionSyncModule.DetailedUpdateWrite("RcvActorID", m_zeroUUID, 0, otherSideActorID, otherSideActorID, msg.Length);
                        if (m_regionSyncModule.IsSyncRelay)
                        {
                            SymmetricSyncMessage outMsg = new SymmetricSyncMessage(SymmetricSyncMessage.MsgType.ActorID, m_regionSyncModule.ActorID);
                            m_regionSyncModule.DetailedUpdateWrite("SndActorID", m_zeroUUID, 0, m_regionSyncModule.ActorID, otherSideActorID, outMsg.Length);
                            Send(outMsg);
                        }
                        m_log.DebugFormat("Syncing to actor \"{0}\"", otherSideActorID);
                        if (otherSideRegionName != null && otherSideActorID != null)
                            StartCollectingStats();
                        return;
                    }
                default:
                    break;
            }

            //For any other messages, we simply deliver the message to RegionSyncModule for now.
            //Later on, we may deliver messages to different modules, say sync message to RegionSyncModule and event message to ActorSyncModule.
            m_regionSyncModule.HandleIncomingMessage(msg, otherSideActorID, this);
        }

        private bool m_collectingStats = false;
        private SortedDictionary<string, long> m_packetTypesSent = new SortedDictionary<string,long>();
        private SortedDictionary<string, long> m_packetTypesRcvd = new SortedDictionary<string,long>();
        private List<Stat> m_registeredStats = new List<Stat>();

        private void StartCollectingStats()
        {
            Stat s;
            s = new Stat(
                "DSG_Msgs_Rcvd (" + description + ")",
                "DSG messages rcvd for this connector",
                "DSG messages rcvd for this connector",
                " messages",
                "dsg",
                m_regionSyncModule.Scene.Name,
                StatType.Pull,
                stat => { stat.Value = this.msgsIn; },
                StatVerbosity.Debug);
            StatsManager.RegisterStat(s);
            m_registeredStats.Add(s);

            s = new Stat(
                "DSG_Msgs_Sent (" + description + ")",
                "DSG messages sent for this connector",
                "DSG messages sent for this connector",
                " messages",
                "dsg",
                m_regionSyncModule.Scene.Name,
                StatType.Pull,
                stat => { stat.Value = this.msgsOut; },
                StatVerbosity.Debug);
            StatsManager.RegisterStat(s);
            m_registeredStats.Add(s);

            s = new Stat(
                "DSG_Bytes_Rcvd (" + description + ")",
                "DSG bytes rcvd for this connector",
                "DSG bytes rcvd for this connector",
                " bytes",
                "dsg",
                m_regionSyncModule.Scene.Name,
                StatType.Pull,
                stat => { stat.Value = this.bytesIn; },
                StatVerbosity.Debug);
            StatsManager.RegisterStat(s);
            m_registeredStats.Add(s);

            s = new Stat(
                "DSG_Bytes_Sent (" + description + ")",
                "DSG bytes sent for this connector",
                "DSG bytes sent for this connector",
                " bytes",
                "dsg",
                m_regionSyncModule.Scene.Name,
                StatType.Pull,
                stat => { stat.Value = this.bytesOut; },
                StatVerbosity.Debug);
            StatsManager.RegisterStat(s);
            m_registeredStats.Add(s);

            s = new Stat(
                "DSG_Queued_Msgs (" + description + ")",
                "DSG queued updates for this connector",
                "DSG queued updates for this connector",
                " messages",
                "dsg",
                m_regionSyncModule.Scene.Name,
                StatType.Pull,
                stat => { stat.Value = this.m_outQ.Count; },
                StatVerbosity.Debug);
            StatsManager.RegisterStat(s);
            m_registeredStats.Add(s);

            m_collectingStats = true;
        }

        private void CollectSendStat(string type, int length)
        {
            lock (stats)
            {
                if (m_collectingStats)
                {
                    msgsOut++;
                    bytesOut += length;
                    long count;
                    if (!m_packetTypesSent.TryGetValue(type, out count))
                    {
                        m_packetTypesSent[type] = 1;
                        Stat s = new Stat(
                            "DSG_Msgs_Sent (" + description + ") (" + type + ")",
                            "DSG messages of type " + type + " sent for this connector",
                            "DSG messages of type " + type + " sent for this connector",
                            " messages",
                            "dsg",
                            m_regionSyncModule.Scene.Name,
                            StatType.Pull,
                            stat => { stat.Value = this.m_packetTypesSent[type.ToString()]; },
                            StatVerbosity.Debug);
                        StatsManager.RegisterStat(s);
                        m_registeredStats.Add(s);
                    }
                    else
                        m_packetTypesSent[type] = count + 1;
                }
            }
        }

        private void CollectReceiveStat(string type, int length)
        {
            lock (stats)
            {
                if (m_collectingStats)
                {
                    msgsIn++;
                    bytesIn += length;

                    long count;
                    if (!m_packetTypesRcvd.TryGetValue(type, out count))
                    {
                        m_packetTypesRcvd[type] = 1;
                        Stat s = new Stat(
                            "DSG_Msgs_Rcvd (" + description + ") (" + type + ")",
                            "DSG messages of type " + type + " received for this connector",
                            "DSG messages of type " + type + " received for this connector",
                            " messages",
                            "dsg",
                            m_regionSyncModule.Scene.Name,
                            StatType.Pull,
                            stat => { stat.Value = this.m_packetTypesRcvd[type.ToString()]; },
                            StatVerbosity.Debug);
                        StatsManager.RegisterStat(s);
                        m_registeredStats.Add(s);
                    }
                    else
                        m_packetTypesRcvd[type] = count + 1;
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

        public string StatisticIdentifier()
        {
            return this.description;
        }


        private void GetStats(bool clear, out long msgsIn, out long msgsOut, out long bytesIn, out long bytesOut, out long queueSize, out double mbpsIn, out double mbpsOut)
        {
            double secondsSinceLastStats = DateTime.Now.Subtract(lastStatTime).TotalSeconds;
            lastStatTime = DateTime.Now;

            lock (stats)
            {
                msgsIn = this.msgsIn;
                msgsOut = this.msgsOut;
                bytesIn = this.bytesIn;
                bytesOut = this.bytesOut;
                queueSize = this.m_outQ.Count;
                mbpsIn = 8 * (bytesIn / secondsSinceLastStats / 1000000);
                mbpsOut = 8 * (bytesOut / secondsSinceLastStats / 1000000);
            }
        }

        public string StatisticLine(bool clearFlag)
        {
            long msgsIn, msgsOut, bytesIn, bytesOut, queueSize;
            double mbpsIn, mbpsOut;
            GetStats(clearFlag, out msgsIn, out msgsOut, out bytesIn, out bytesOut, out queueSize, out mbpsIn, out mbpsOut);
            return String.Format("{0},{1},{2},{3},{4},{5},{6}", msgsIn, msgsOut, bytesIn, bytesOut, queueSize, mbpsIn, mbpsOut);
        }

        public string StatisticTitle()
        {
            return "msgsIn,msgsOut,bytesIn,bytesOut,queueSize,Mbps In,Mbps Out";
        }
    }
}
